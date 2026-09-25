using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UniNet.CodeGenerator
{
    /// <summary>
    /// UniNet source generator — scans RPC/replication members on NetworkBehaviour-derived types and
    /// generates hub wiring, per-type dispatch, and delta handles that run on top of the DRPC runtime.
    /// Targets Roslyn 4.3 (Unity 6.0 LTS).
    /// </summary>
    [Generator]
    public sealed class UniNetCodeGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var candidates = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList.Types.Count: > 0 },
                transform: static (ctx, _) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node) as INamedTypeSymbol)
                .Where(static t => t != null)
                .Collect()
                .Combine(context.CompilationProvider);

            context.RegisterSourceOutput(candidates, static (spc, source) =>
            {
                var (types, compilation) = source;
                var parser = new Parser(spc.ReportDiagnostic, spc.CancellationToken);
                var models = parser.Parse(types!);
                if (models.Count == 0) return;

                var methodIds = new Dictionary<int, string>();
                foreach (var m in models)
                    m.ValidateIds(d => parser.Report(d), methodIds);

                var symbols = new Dictionary<string, string>();
                foreach (var m in models)
                {
                    var safe = StringBuilderCache.Invoke(m.FullName);
                    var keys = new[] { "__Req_" + safe + "_*", "__Replicate_" + safe + "_Requested", "__" + safe + "Replication" };
                    foreach (var key in keys)
                    {
                        if (symbols.TryGetValue(key, out var other))
                            parser.Report(Diagnostic.Create(Diagnostics.SafeNameCollision, null, m.FullName, other));
                        else
                            symbols[key] = m.FullName;
                    }
                }

                spc.AddSource("UniNet.Generated.g.cs", Emitter.Emit(models, compilation.Assembly.Name));
            });
        }
    }

    /// <summary>Semantic analysis — extracts and validates RPC/replication members of NetworkBehaviour-derived types.</summary>
    internal sealed class Parser
    {
        internal delegate void ReportDelegate(Diagnostic diagnostic);
        private readonly ReportDelegate _report;
        public CancellationToken CancellationToken { get; }

        public Parser(ReportDelegate report, CancellationToken ct)
        {
            _report = report;
            CancellationToken = ct;
        }

        public List<TypeModel> Parse(IEnumerable<INamedTypeSymbol> types)
        {
            var result = new List<TypeModel>();
            foreach (var type in types)
            {
                CancellationToken.ThrowIfCancellationRequested();
                if (!IsNetworkBehaviour(type)) continue;

                var model = new TypeModel
                {
                    Name = type.Name,
                    FullName = type.ToDisplayString(),
                    Namespace = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
                    IsPartial = type.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is ClassDeclarationSyntax c && c.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword))),
                };

                if (!model.IsPartial)
                {
                    Report(Diagnostics.TypeNotPartial, type.Locations[0], type.Name);
                    continue;
                }

                foreach (var member in type.GetMembers())
                {
                    if (member is IMethodSymbol method && method.MethodKind == MethodKind.Ordinary)
                        ParseMethod(model, type, method);
                    else if (member is IFieldSymbol field)
                        ParseField(model, type, field);
                }

                // a replication handle exists only when the type has replicated fields
                model.HasReplicatedFields = model.ReplicatedFields.Count > 0;
                result.Add(model);
            }
            return result;
        }

        private void ParseMethod(TypeModel model, INamedTypeSymbol type, IMethodSymbol method)
        {
            var serverRpc = GetAttribute(method, "UniNet.Core.ServerRpcAttribute");
            var clientRpc = GetAttribute(method, "UniNet.Core.ClientRpcAttribute");
            var multicast = GetAttribute(method, "UniNet.Core.MulticastRpcAttribute");
            if (serverRpc == null && clientRpc == null && multicast == null) return;

            var kind = serverRpc != null ? RpcKind.Server
                : clientRpc != null ? RpcKind.Client
                : RpcKind.Multicast;

            // must be a partial declaration (the body lives in the user's _Implementation method)
            if (!method.IsPartialDefinition)
            {
                Report(Diagnostics.RpcNotPartial, method.Locations[0], method.Name, kind.ToString());
                return;
            }

            var delivery = clientRpc != null ? GetDelivery(clientRpc) : (multicast != null ? GetDelivery(multicast) : "ReliableOrdered");
            var rpc = new RpcModel
            {
                Name = method.Name,
                Kind = kind,
                Delivery = delivery,
                Accessibility = ToKeyword(method.DeclaredAccessibility),
                IdKey = type.ToDisplayString() + "." + method.Name,
                Parameters = method.Parameters.Select(p => new ParamModel { Name = p.Name, Type = p.Type }).ToList(),
            };
            if (serverRpc != null)
            {
                rpc.RequireOwnership = GetRequireOwnership(serverRpc);   // default true — opt out via [ServerRpc(RequireOwnership = false)] (see ADR-0016)
                rpc.HasValidate = GetValidate(serverRpc);                // default false — opt in via [ServerRpc(Validate = true)] (see ADR-0018)
            }

            // parameter type check — primitives, string, or [Message] types
            foreach (var p in rpc.Parameters)
            {
                p.IsMessage = IsMessageType(p.Type);
                if (!p.IsMessage && Emitter.WriteCall(p.Type) == null)
                    Report(Diagnostics.UnsupportedType, method.Locations[0], p.Type.ToDisplayString(), method.Name);
            }

            // _Implementation presence and signature check
            var impl = type.GetMembers(method.Name + "_Implementation").OfType<IMethodSymbol>().FirstOrDefault();
            if (impl == null || !SignatureMatches(impl, method))
            {
                Report(Diagnostics.ImplementationMissing, method.Locations[0], method.Name);
                return;
            }

            // _Validate hook — wired only when the ServerRpc opts in with Validate = true (no auto-detection — see ADR-0018)
            var validate = type.GetMembers(method.Name + "_Validate").OfType<IMethodSymbol>().FirstOrDefault();
            if (rpc.Kind == RpcKind.Server)
            {
                if (rpc.HasValidate)
                {
                    if (validate == null)
                        Report(Diagnostics.ValidateMissing, method.Locations[0], method.Name);
                    else if (!SignatureMatches(validate, method) || validate.ReturnType.ToDisplayString() != "System.Threading.Tasks.Task<bool>")
                        Report(Diagnostics.ValidateSignature, validate.Locations.Length > 0 ? validate.Locations[0] : method.Locations[0], method.Name);
                }
                else if (validate != null)
                {
                    // a _Validate method without the flag never runs — warn about the likely missing opt-in
                    Report(Diagnostics.ValidateNotOptedIn, validate.Locations.Length > 0 ? validate.Locations[0] : method.Locations[0], method.Name);
                }
            }

            model.Rpcs.Add(rpc);
        }

        private void ParseField(TypeModel model, INamedTypeSymbol type, IFieldSymbol field)
        {
            var attr = GetAttribute(field, "UniNet.Core.ReplicatedAttribute");
            if (attr == null) return;

            var rep = new FieldModel
            {
                Name = field.Name,
                Type = field.Type,
                Index = model.ReplicatedFields.Count,
                Condition = ParseCondition(attr),
            };

            if ((rep.Condition & (FieldModel.CondOwnerOnly | FieldModel.CondSkipOwner))
                == (FieldModel.CondOwnerOnly | FieldModel.CondSkipOwner))
            {
                Report(Diagnostics.ContradictoryCondition, field.Locations[0], field.Name);
                return;
            }

            // custom serializer (see ADR-0020) — on a COLLECTION field it applies to the ELEMENT wire format;
            // on a scalar field it takes over the field's format and unlocks non-primitive, non-[Message] types.
            // The Write/Read contract is verified here so violations fail at compile time.
            var serializer = GetSerializerType(attr);

            // collection detection first (T[] / List<T>) — element-level delta encoding (FastArray, see ADR-0020)
            ITypeSymbol? elementType = null;
            bool isArray = false;
            if (field.Type is IArrayTypeSymbol array)
            {
                elementType = array.ElementType;
                isArray = true;
            }
            else if (field.Type is INamedTypeSymbol named
                && named.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>")
            {
                elementType = named.TypeArguments.Length > 0 ? named.TypeArguments[0] : null;
            }

            if (elementType != null)
            {
                rep.IsCollection = true;
                rep.IsArray = isArray;
                rep.ElementType = elementType;
                rep.ElemIsMessage = serializer == null && IsMessageType(elementType);

                if (elementType is IArrayTypeSymbol || (elementType is INamedTypeSymbol el
                        && el.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>"))
                {
                    Report(Diagnostics.NestedCollection, field.Locations[0], field.Name, elementType.ToDisplayString());
                    return;
                }

                if (serializer != null)
                {
                    // element serializer — validates against the ELEMENT type
                    rep.SerializerType = "global::" + serializer.ToDisplayString();
                    var error = ValidateSerializerContract(serializer, elementType, out _);   // element diff stays on object.Equals — Equals is accepted but unused for elements (v1)
                    if (error != null)
                    {
                        Report(Diagnostics.SerializerContract, field.Locations[0], field.Name, serializer.ToDisplayString(), error);
                        return;
                    }
                }
                else if (!IsMessageType(elementType) && (Emitter.WriteCall(elementType) == null || Emitter.ReadCall(elementType) == null))
                {
                    Report(Diagnostics.UnsupportedElementType, field.Locations[0], field.Name, elementType.ToDisplayString());
                    return;
                }
            }
            else if (serializer != null)
            {
                rep.SerializerType = "global::" + serializer.ToDisplayString();
                rep.CanBeNull = field.Type.IsReferenceType
                    || (field.Type is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T);
                var error = ValidateSerializerContract(serializer, field.Type, out rep.HasCustomEquals);
                if (error != null)
                {
                    Report(Diagnostics.SerializerContract, field.Locations[0], field.Name, serializer.ToDisplayString(), error);
                    return;
                }
            }
            else
            {
                rep.IsMessage = IsMessageType(field.Type);
                if (!rep.IsMessage && (Emitter.WriteCall(field.Type) == null || Emitter.ReadCall(field.Type) == null))
                {
                    Report(Diagnostics.UnsupportedType, field.Locations[0], field.Type.ToDisplayString(), field.Name);
                    return;
                }
            }

            // Notify (optional) — void M(T prev)
            var notifyName = GetNotifyName(attr);
            if (notifyName != null)
            {
                var notify = type.GetMembers(notifyName).OfType<IMethodSymbol>()
                    .FirstOrDefault(m => m.Parameters.Length == 1 && m.ReturnsVoid
                        && SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, field.Type));
                if (notify == null)
                {
                    Report(Diagnostics.NotifySignature, field.Locations[0], notifyName, field.Name);
                    return;
                }
                rep.NotifyMethod = notifyName;
            }

            model.ReplicatedFields.Add(rep);
            if (model.ReplicatedFields.Count > 32)
                Report(Diagnostics.ReplicatedFieldLimit, field.Locations[0], type.Name);
        }

        private static string ToKeyword(Accessibility accessibility)
        {
            switch (accessibility)
            {
                case Accessibility.Public: return "public";
                case Accessibility.Protected: return "protected";
                case Accessibility.Internal: return "internal";
                case Accessibility.ProtectedOrInternal: return "protected internal";
                case Accessibility.ProtectedAndInternal: return "private protected";
                default: return "private";
            }
        }

        /// <summary>
        /// Whether the type is marked [Message] — serializable via the object path (MessageId header dispatch).
        /// MessageKind.NonId (4) has no ID in its header so it cannot be dispatched as object → unsupported (UNINET002).
        /// </summary>
        internal static bool IsMessageType(ITypeSymbol type)
        {
            if (type is not INamedTypeSymbol named) return false;
            foreach (var attr in named.GetAttributes())
            {
                if (attr.AttributeClass?.ToDisplayString() != "MessageProtocol.MessageAttribute") continue;
                if (attr.ConstructorArguments.Length > 0
                    && attr.ConstructorArguments[0].Value is int kind
                    && kind == 4)   // MessageKind.NonId
                    return false;
                return true;
            }
            return false;
        }

        private static bool IsNetworkBehaviour(INamedTypeSymbol type)
        {
            var current = type.BaseType;
            while (current != null)
            {
                if (current.ToDisplayString() == "UniNet.Unity.NetworkBehaviour") return true;
                current = current.BaseType;
            }
            return false;
        }

        private static AttributeData? GetAttribute(ISymbol symbol, string fullName)
            => symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == fullName);

        private static string GetDelivery(AttributeData attr)
        {
            if (attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is object value)
            {
                var name = value.ToString();
                if (name == "Unreliable") return "Unreliable";
            }
            return "ReliableOrdered";
        }

        private static string? GetNotifyName(AttributeData attr)
        {
            foreach (var named in attr.NamedArguments)
                if (named.Key == "Notify" && named.Value.Value is string s)
                    return s;
            return null;
        }

        /// <summary>ServerRpcAttribute.RequireOwnership named argument (defaults to true — ownership enforced).</summary>
        private static bool GetRequireOwnership(AttributeData attr)
        {
            foreach (var named in attr.NamedArguments)
                if (named.Key == "RequireOwnership" && named.Value.Value is bool b)
                    return b;
            return true;
        }

        /// <summary>ServerRpcAttribute.Validate named argument (defaults to false — no _Validate hook, see ADR-0018).</summary>
        private static bool GetValidate(AttributeData attr)
        {
            foreach (var named in attr.NamedArguments)
                if (named.Key == "Validate" && named.Value.Value is bool b)
                    return b;
            return false;
        }

        /// <summary>Converts the ReplicatedAttribute constructor condition argument to internal bits (1:1 with ReplicateCondition values).</summary>
        private static int ParseCondition(AttributeData attr)
            => attr.ConstructorArguments.Length > 0 && attr.ConstructorArguments[0].Value is int cond ? cond : 0;

        /// <summary>ReplicatedAttribute.Serializer named argument (custom static serializer, see ADR-0020).</summary>
        private static INamedTypeSymbol? GetSerializerType(AttributeData attr)
        {
            foreach (var named in attr.NamedArguments)
                if (named.Key == "Serializer" && named.Value.Value is INamedTypeSymbol t)
                    return t;
            return null;
        }

        /// <summary>
        /// Validates the custom serializer contract — at least one static Write(ref MessageBufferWriter, [in] T) and
        /// one static Read(ref MessageBufferReader) → T must exist across ALL overloads and base-type declarations
        /// (shared serializer hubs overload Write per type, so a single arbitrary candidate must not decide the verdict).
        /// A static bool Equals([in] T, [in] T) is optional and replaces the default object.Equals delta comparison.
        /// Returns null on success or a human-readable contract violation.
        /// </summary>
        private static string? ValidateSerializerContract(INamedTypeSymbol serializer, ITypeSymbol fieldType, out bool hasOptionalEquals)
        {
            hasOptionalEquals = false;
            const string writerType = "MessageProtocol.Serialize.MessageBufferWriter";
            const string readerType = "MessageProtocol.Serialize.MessageBufferReader";
            // fieldType arrives as a parameter

            bool writeOk = false, readOk = false;
            for (var t = (INamedTypeSymbol?)serializer; t != null; t = t.BaseType)
            {
                foreach (var member in t.GetMembers())
                {
                    if (member is not IMethodSymbol m || !m.IsStatic || m.MethodKind != MethodKind.Ordinary) continue;
                    if (m.Name == "Write" && !writeOk)
                        writeOk = m.ReturnType.SpecialType == SpecialType.System_Void
                            && m.Parameters.Length == 2
                            && m.Parameters[0].RefKind == RefKind.Ref
                            && m.Parameters[0].Type.ToDisplayString() == writerType
                            && (m.Parameters[1].RefKind == RefKind.In || m.Parameters[1].RefKind == RefKind.None)
                            && SymbolEqualityComparer.Default.Equals(m.Parameters[1].Type, fieldType);
                    else if (m.Name == "Read" && !readOk)
                        readOk = m.Parameters.Length == 1
                            && m.Parameters[0].RefKind == RefKind.Ref
                            && m.Parameters[0].Type.ToDisplayString() == readerType
                            && SymbolEqualityComparer.Default.Equals(m.ReturnType, fieldType);
                    else if (m.Name == "Equals")
                        hasOptionalEquals |= m.Parameters.Length == 2
                            && m.ReturnType.SpecialType == SpecialType.System_Boolean
                            && (m.Parameters[0].RefKind == RefKind.In || m.Parameters[0].RefKind == RefKind.None)
                            && (m.Parameters[1].RefKind == RefKind.In || m.Parameters[1].RefKind == RefKind.None)
                            && SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, fieldType)
                            && SymbolEqualityComparer.Default.Equals(m.Parameters[1].Type, fieldType);
                }
                if (writeOk && readOk && hasOptionalEquals) break;   // everything found — stop walking the base chain
            }

            if (!writeOk)
                return "static void Write(ref " + writerType + ", in " + fieldType.ToDisplayString() + ") 오버로드가 없습니다";
            if (!readOk)
                return "static " + fieldType.ToDisplayString() + " Read(ref " + readerType + ") 오버로드가 없습니다";
            return null;
        }

        private static bool SignatureMatches(IMethodSymbol a, IMethodSymbol b)
        {
            if (a.Parameters.Length != b.Parameters.Length) return false;
            for (int i = 0; i < a.Parameters.Length; i++)
                if (!SymbolEqualityComparer.Default.Equals(a.Parameters[i].Type, b.Parameters[i].Type)) return false;
            return true;
        }

        public void Report(DiagnosticDescriptor descriptor, Location location, params object[] args)
            => _report(Diagnostic.Create(descriptor, location, args));

        public void Report(Diagnostic diagnostic) => _report(diagnostic);
    }

    internal enum RpcKind { Server, Client, Multicast }

    internal sealed class TypeModel
    {
        public string Name = "";
        public string FullName = "";
        public string? Namespace;
        public bool IsPartial;
        public List<RpcModel> Rpcs { get; } = new();
        public List<FieldModel> ReplicatedFields { get; } = new();
        public bool HasReplicatedFields;

        /// <summary>Detects method ID collisions.</summary>
        public void ValidateIds(Action<Diagnostic> report, Dictionary<int, string> seen)
        {
            void Check(int id, string key, Location? location)
            {
                if (seen.TryGetValue(id, out var other))
                    report(Diagnostic.Create(Diagnostics.MethodIdCollision, location, key, other));
                else
                    seen[id] = key;
            }

            foreach (var rpc in Rpcs)
                Check(Fnv.MethodId(rpc.IdKey), rpc.IdKey, null);
            if (HasReplicatedFields)
                Check(Fnv.MethodId(FullName + ".__Replicate"), FullName + ".__Replicate", null);
        }
    }

    internal sealed class RpcModel
    {
        public string Name = "";
        public string Accessibility = "private";
        public RpcKind Kind;
        public string Delivery = "ReliableOrdered";
        public string IdKey = "";
        public List<ParamModel> Parameters = new();
        public bool HasValidate;

        /// <summary>ServerRpc only — whether server dispatch enforces that the sender owns the object (default true, see ADR-0016).</summary>
        public bool RequireOwnership = true;
    }

    internal sealed class ParamModel
    {
        public string Name = "";
        public ITypeSymbol Type = null!;
        public bool IsMessage;
    }

    internal sealed class FieldModel
    {
        public const int CondNone = 0;
        public const int CondOwnerOnly = 1;
        public const int CondSkipOwner = 2;
        public const int CondInitialOnly = 4;

        public string Name = "";
        public ITypeSymbol Type = null!;
        public int Index;
        public string? NotifyMethod;
        public bool IsMessage;
        public int Condition = CondNone;

        /// <summary>Custom static serializer (global-qualified display name) — takes over this field's wire format (see ADR-0020).</summary>
        public string? SerializerType;

        /// <summary>Whether the custom serializer exposes the optional static Equals used for delta comparison.</summary>
        public bool HasCustomEquals;

        /// <summary>Whether the field type can hold null (reference or nullable value type) — the generated null guard is emitted only for these (value types without operator== would fail CS0019).</summary>
        public bool CanBeNull;

        /// <summary>Collection field (T[] or List&lt;T&gt;) — element-level delta encoding (FastArray, see ADR-0020).</summary>
        public bool IsCollection;

        /// <summary>Whether the collection is T[] (true) or List&lt;T&gt; (false).</summary>
        public bool IsArray;

        /// <summary>Element type of a collection field.</summary>
        public ITypeSymbol? ElementType;

        /// <summary>Whether the ELEMENT is a [Message] type (element serialization path).</summary>
        public bool ElemIsMessage;

        /// <summary>Whether the InitialOnly condition is set (excluded from delta tracking).</summary>
        public bool IsInitialOnly => (Condition & CondInitialOnly) != 0;

        /// <summary>Whether the OwnerOnly condition is set.</summary>
        public bool IsOwnerOnly => (Condition & CondOwnerOnly) != 0;

        /// <summary>Whether the SkipOwner condition is set.</summary>
        public bool IsSkipOwner => (Condition & CondSkipOwner) != 0;
    }

    internal static class StringBuilderCache
    {
        public static string Invoke(string value)
        {
            var sb = new System.Text.StringBuilder(value.Length);
            foreach (var ch in value) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            return sb.ToString();
        }
    }

    /// <summary>FNV-1a 32-bit — same algorithm as the runtime Fnv1a.MethodId.</summary>
    internal static class Fnv
    {
        public static int MethodId(string value)
        {
            uint hash = 2166136261;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= 16777619;
            }
            int id = (int)(hash & 0x7FFFFFFF);
            return id < 64 ? id + 64 : id;
        }
    }

    /// <summary>UNINET0xx diagnostic definitions.</summary>
    internal static class Diagnostics
    {
        private static DiagnosticDescriptor Make(int id, string title, string format, DiagnosticSeverity severity)
            => new("UNINET" + id.ToString("000"), title, format, "UniNet", severity, true);

        public static readonly DiagnosticDescriptor TypeNotPartial =
            Make(1, "NetworkBehaviour 파생 타입은 partial이어야 함", "타입 '{0}' 은(는) NetworkBehaviour 파생이므로 partial 로 선언해야 합니다", DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor RpcNotPartial =
            Make(3, "RPC 메서드는 partial 선언이어야 함", "RPC 메서드 '{0}' ({1}) 은(는) 본문 없이 partial 로 선언하고 구현은 {0}_Implementation 에 작성해야 합니다", DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor UnsupportedType =
            Make(2, "지원하지 않는 타입", "타입 '{0}' 은(는) RPC/리플리케이션 매개변수로 지원되지 않습니다 ('{1}') — 기본형·string 또는 [Message] 마킹 타입(NonId 제외)만 지원", DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor ImplementationMissing =
            Make(4, "_Implementation 누락/불일치", "RPC '{0}' 에 대응하는 '{0}_Implementation' 메서드가 없거나 시그니처가 일치하지 않습니다", DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor ValidateSignature =
            Make(5, "_Validate 시그니처 불일치", "RPC '{0}' 의 _Validate 는 같은 매개변수와 Task<bool> 반환형이어야 합니다", DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor NotifySignature =
            Make(6, "RepNotify 시그니처 불일치", "Notify 메서드 '{0}' 은(는) 리플리케이션 필드 '{1}' 와 같은 타입 1개를 인자로 받고 void 를 반환해야 합니다", DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor MethodIdCollision =
            Make(7, "메서드 ID 충돌", "메서드 ID 충돌: '{0}' 와(과) '{1}'", DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor ReplicatedFieldLimit =
            Make(8, "[Replicated] 필드 수 상한", "타입 '{0}' 의 [Replicated] 필드가 32개를 초과했습니다 — 델타 마스크가 uint(32비트)라 데이터가 파손됩니다. 필드를 줄이거나 타입을 분할하세요", DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor SafeNameCollision =
            Make(9, "생성명 충돌", "타입 '{0}' 와(과) '{1}' 의 생성 코드 심볼명이 충돌합니다", DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor ContradictoryCondition =
            Make(10, "모순된 리플리케이션 조건", "필드 '{0}' 의 조건 OwnerOnly|SkipOwner 는 모순입니다 — 소유자에게도 보내지 않는 필드가 된다", DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor ValidateMissing =
            Make(11, "_Validate 누락", "RPC '{0}' 은(는) Validate = true 로 선언됐지만 대응하는 '{0}_Validate' 메서드가 없습니다 — 같은 매개변수에 Task<bool> 반환형으로 작성하세요", DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor ValidateNotOptedIn =
            Make(12, "_Validate가 옵트인되지 않음", "ServerRpc '{0}' 에 _Validate 메서드가 있지만 Validate 플래그가 없어 실행되지 않습니다 — [ServerRpc(Validate = true)]로 옵트인하세요", DiagnosticSeverity.Warning);

        public static readonly DiagnosticDescriptor SerializerContract =
            Make(13, "직렬화기 서명 불일치", "필드 '{0}' 의 Serializer '{1}' 계약 위반: {2} — static void Write(ref MessageBufferWriter, in T) + static T Read(ref MessageBufferReader) [선택: static bool Equals(in T, in T)] 형태로 작성하세요", DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor UnsupportedElementType =
            Make(14, "지원하지 않는 컬렉션 요소 타입", "필드 '{0}' 의 요소 타입 '{1}' 은(는) 지원되지 않습니다 — 기본형·string·[Message] 마킹 타입(NonId 제외) 또는 Serializer 지정 타입만 가능합니다", DiagnosticSeverity.Error);

        public static readonly DiagnosticDescriptor NestedCollection =
            Make(15, "중첩 컬렉션 미지원", "필드 '{0}' 의 요소 타입 '{1}' 은(는) 컬렉션입니다 — 중첩 컬렉션(T[][]·List<List<T>> 등)은 지원되지 않습니다. 요소를 [Message] 타입으로 감싸거나 평면화하세요", DiagnosticSeverity.Error);
    }
}
