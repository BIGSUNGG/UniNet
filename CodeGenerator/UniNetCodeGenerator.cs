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
    /// UniNet 소스 제너레이터 — NetworkBehaviour 파생 타입의 RPC/리플리케이션 멤버를 스캔해
    /// DRPC 런타임 위에서 동작하는 허브 배선·타입별 디스패치·델타 핸들을 생성한다.
    /// Roslyn 4.3(Unity 6.0 LTS) 타깃.
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
                .Collect();

            context.RegisterSourceOutput(candidates, static (spc, types) =>
            {
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

                spc.AddSource("UniNet.Generated.g.cs", Emitter.Emit(models));
            });
        }
    }

    /// <summary>의미론 분석 — NetworkBehaviour 파생의 RPC/리플리케이션 멤버 추출·검증.</summary>
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

                // 리플리케이션 핸들은 필드가 있을 때만
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

            // partial 선언 검사
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

            // 매개변수 타입 검사 — 기본형·string 또는 [Message] 타입
            foreach (var p in rpc.Parameters)
            {
                p.IsMessage = IsMessageType(p.Type);
                if (!p.IsMessage && Emitter.WriteCall(p.Type) == null)
                    Report(Diagnostics.UnsupportedType, method.Locations[0], p.Type.ToDisplayString(), method.Name);
            }

            // _Implementation 존재·시그니처 검사
            var impl = type.GetMembers(method.Name + "_Implementation").OfType<IMethodSymbol>().FirstOrDefault();
            if (impl == null || !SignatureMatches(impl, method))
            {
                Report(Diagnostics.ImplementationMissing, method.Locations[0], method.Name);
                return;
            }

            // _Validate (선택) — 같은 매개변수 + Task<bool> 반환
            var validate = type.GetMembers(method.Name + "_Validate").OfType<IMethodSymbol>().FirstOrDefault();
            if (validate != null)
            {
                if (!SignatureMatches(validate, method) || validate.ReturnType.ToDisplayString() != "System.Threading.Tasks.Task<bool>")
                    Report(Diagnostics.ValidateSignature, validate.Locations.Length > 0 ? validate.Locations[0] : method.Locations[0], method.Name);
                else
                    rpc.HasValidate = true;
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
            };

            rep.IsMessage = IsMessageType(field.Type);
            if (!rep.IsMessage && (Emitter.WriteCall(field.Type) == null || Emitter.ReadCall(field.Type) == null))
            {
                Report(Diagnostics.UnsupportedType, field.Locations[0], field.Type.ToDisplayString(), field.Name);
                return;
            }

            // Notify (선택) — void M(T prev)
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
        /// [Message] 마킹 타입 여부 — object 직렬화 경로(MessageId 헤더 디스패치)로 직렬화 가능한 타입.
        /// MessageKind.NonId(4)는 헤더에 ID가 없어 object 디스패치 불가 → 미지원(UNINET002).
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

        /// <summary>메서드 ID 충돌 검사.</summary>
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
    }

    internal sealed class ParamModel
    {
        public string Name = "";
        public ITypeSymbol Type = null!;
        public bool IsMessage;
    }

    internal sealed class FieldModel
    {
        public string Name = "";
        public ITypeSymbol Type = null!;
        public int Index;
        public string? NotifyMethod;
        public bool IsMessage;
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

    /// <summary>FNV-1a 32bit — 런타임 Fnv1a.MethodId와 동일 알고리즘.</summary>
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

    /// <summary>UNINET0xx 진단 정의.</summary>
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
    }
}
