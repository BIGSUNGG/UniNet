// FR-07 — headless generator regression host. Two driver phases plus a runtime phase, one process, no test framework:
//   1) Main driver pass — CSharpGeneratorDriver.RunGeneratorsAndUpdateCompilation over a compilation made of the REAL
//      UniNet.Core sources, minimal UnityEngine/UniNet.Unity shims (Core itself is engine-free; the shims exist only so
//      generated hub code compiles and fixtures can derive NetworkBehaviour) and clean RPC fixtures. Asserts: zero
//      error diagnostics and the updated compilation (generated code included) emits cleanly.
//   2) Negative driver pass — a nested-collection fixture must produce exactly the UNINET002 error diagnostic.
//   3) Runtime phase — the emitted assembly is loaded and the generated send/codec paths are EXECUTED against the real
//      Core runtime (NetworkServer + fake channel): FR-03 collection round-trip/limits/truncation and the FR-01 send
//      gate. Not a string grep — real dispatch through UniNetDispatch and real channel sends.
//
// Usage: dotnet run --project Tests/UniNet.CodeGenerator.Tests -c Release -- <path-to-UniNet.CodeGenerator.dll>
// The generator DLL argument is what lets the parent run the same checks against the source-built analyzer and the
// packaged Package/Runtime/UniNet.Unity/Analyzers/UniNet.CodeGenerator.dll in two separate processes.

using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class Program
{
    private const string FixtureNs = "UniNet.Tests.Headless";

    private static readonly List<string> Failures = new();
    private static int _checks;

    private static int Main(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("usage: UniNet.CodeGenerator.Tests <path-to-UniNet.CodeGenerator.dll>");
            return 2;
        }

        var repoRoot = FindRepoRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("repo root not found (walked up from " + AppContext.BaseDirectory + ")");
        var generatorDll = Path.GetFullPath(args[0]);

        // ── input compilation: real Core sources + engine/Unity shims + clean fixtures ──
        var parse = new CSharpParseOptions(LanguageVersion.CSharp10);
        var trees = new List<SyntaxTree> { Parse(ShimUnityEngine), Parse(ShimUniNetUnity), Parse(FixtureSources) };
        var coreDir = Path.Combine(repoRoot, "Package", "Runtime", "UniNet.Core");
        foreach (var file in Directory.EnumerateFiles(coreDir, "*.cs", SearchOption.AllDirectories))
            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(file), parse, file));

        var refs = new List<MetadataReference>();
        var fwDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var name in new[]   // netstandard facade first, then the implementation set (DIM support etc.)
                 {
                     "netstandard.dll", "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Collections.dll",
                     "System.Collections.Concurrent.dll", "System.Linq.dll", "System.Linq.Expressions.dll",
                     "System.Threading.dll", "System.Threading.Tasks.dll", "System.Memory.dll", "System.Buffers.dll",
                     "System.ObjectModel.dll", "System.Text.RegularExpressions.dll", "System.Numerics.Vectors.dll",
                     "System.Security.Cryptography.dll", "System.Security.Cryptography.Algorithms.dll",
                     "System.Security.Cryptography.Primitives.dll"
                 })
        {
            var path = Path.Combine(fwDir, name);
            if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
        }
        var depsDir = Path.Combine(repoRoot, "Package", "Runtime", "Dependencies");
        foreach (var path in Directory.EnumerateFiles(depsDir, "*.dll"))
            refs.Add(MetadataReference.CreateFromFile(path));

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary).WithNullableContextOptions(NullableContextOptions.Enable);
        var generatorType = Assembly.LoadFrom(generatorDll).GetType("UniNet.CodeGenerator.UniNetCodeGenerator")
            ?? throw new InvalidOperationException("generator type not found in " + generatorDll);
        var generator = (IIncrementalGenerator)Activator.CreateInstance(generatorType)!;

        // ── main pass: generate + compile + emit ──
        CSharpGeneratorDriver.Create(new[] { generator })
            .RunGeneratorsAndUpdateCompilation(
                CSharpCompilation.Create("UniNet.Tests.Headless", trees, refs, options),
                out var updated, out var genDiags);
        Check(genDiags.All(d => d.Severity != DiagnosticSeverity.Error),
            "main driver pass — no error diagnostics (got: " + string.Join(",", genDiags.Select(d => d.Id)) + ")");

        using var pe = new MemoryStream();
        var emit = updated!.Emit(pe);
        Check(emit.Success, "generated code compiles (" + string.Join("; ", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.GetMessage())) + ")");
        if (!emit.Success) return 1;   // F13 failfast — never fall through to loading an empty PE (BadImageFormatException)

        // ── F13 (AUDIT-20261027) named-argument pass — public RPC/companion declarations must keep the user's
        // parameter names so existing named-argument call sites keep binding (compile assertion against `updated`).
        using var namedPe = new MemoryStream();
        var namedEmit = updated.AddSyntaxTrees(new[] { Parse(NamedArgProbe) }).Emit(namedPe);
        Check(namedEmit.Success, "F13 named-argument pass — user parameter names preserved on public surfaces (" + string.Join("; ", namedEmit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.GetMessage())) + ")");

        // ── negative pass: unsupported nested-collection RPC arg → UNINET002. Real Core sources included so the
        // [ServerRpc] attribute resolves — without it the parameter is an error type and the generator sees no RPC.
        var negativeTrees = new List<SyntaxTree> { Parse(ShimUnityEngine), Parse(ShimUniNetUnity), Parse(NegativeFixture) };
        negativeTrees.AddRange(trees.Where(t => !t.FilePath.EndsWith("CodecRpcObject.generated", StringComparison.Ordinal) && t.FilePath.Length > 0));
        CSharpGeneratorDriver.Create(new[] { generator })
            .RunGeneratorsAndUpdateCompilation(CSharpCompilation.Create("UniNet.Tests.Headless.Negative",
                negativeTrees, refs, options),
                out _, out var negDiags);
        Check(negDiags.Any(d => d.Id == "UNINET002" && d.Severity == DiagnosticSeverity.Error),
            "negative driver pass — nested collection RPC reports UNINET002 (got: " + string.Join(",", negDiags.Select(d => d.Id)) + ")");
        Check(negDiags.Count(d => d.Id == "UNINET002") == 2,
            "RPC custom-only element arrays are rejected with UNINET002, unlike replicated-field serializers");
        Check(!negDiags.Any(d => d.Id is "UNINET013" or "UNINET014"),
            "a zero-byte replicated element serializer remains valid; it does not make that element an RPC type");

        // ── runtime phase: execute the generated send/codec paths on the real Core runtime ──
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => ResolveFromDirs(e.Name, depsDir, fwDir);
        RunRuntimeChecks(AssemblyLoadContext.Default.LoadFromStream(new MemoryStream(pe.ToArray())));

        Console.WriteLine();
        Console.WriteLine($"{_checks - Failures.Count}/{_checks} checks passed — generator {Path.GetFileName(generatorDll)}");
        foreach (var f in Failures) Console.WriteLine("[FAIL] " + f);
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void RunRuntimeChecks(Assembly asm)
    {
        asm.GetType("UniNet.Generated.__UniNetRegistration")!.GetMethod("Register")!.Invoke(null, null);

        var env = asm.GetType("UniNet.Core.Hosting.UniNetEnvironment")!;
        var serverType = asm.GetType("UniNet.Core.Hosting.NetworkServer")!;
        var dispatch = asm.GetType("UniNet.Core.Hosting.UniNetDispatch")!;
        var fnv = asm.GetType("UniNet.Core.Hosting.Fnv1a")!;
        var codecType = asm.GetType($"{FixtureNs}.CodecRpcObject")!;
        var pump = env.GetMethod("PumpMain")!;

        var server = Activator.CreateInstance(serverType)!;
        env.GetMethod("SetServer")!.Invoke(null, new[] { server });

        const ulong netId = 0x1234;
        var fixture = Activator.CreateInstance(codecType)!;
        codecType.GetProperty("NetId")!.SetValue(fixture, netId);
        codecType.GetProperty("IsServer")!.SetValue(fixture, true);   // the shim exposes the registration stamp as a settable property
        serverType.GetMethod("RegisterSceneObject")!.Invoke(server, new object[] { netId, new object[] { fixture } });

        var channel = ChannelProxy.Create(asm);
        var connId = (long)serverType.GetMethod("AttachConnection")!.Invoke(server, new object[] { channel })!;
        pump.Invoke(null, null);   // Welcome + attach tail (approval still held — the default expected version is non-empty)

        channel.Expect("SendWelcome", 1, "FR-01 — system Welcome unaffected while unapproved");

        int clId = MethodId(fnv, $"{FixtureNs}.CodecRpcObject.ClPing");
        int mcId = MethodId(fnv, $"{FixtureNs}.CodecRpcObject.McPing");
        int tailId = MethodId(fnv, $"{FixtureNs}.CodecRpcObject.RpcListTail");

        // FR-01 — unapproved: immediate drop for broadcast / targeted / multicast network sends (no queue, no replay).
        Invoke(codecType, fixture, "ClPing", 1);
        Invoke(codecType, fixture, "ClPingTo", connId, 1);   // generated To companion (same methodId as the broadcast)
        Invoke(codecType, fixture, "McPing", 1);
        pump.Invoke(null, null);
        channel.Expect("UniNetSend", 0, "FR-01 — unapproved connection receives zero game RPCs");
        Check((int)Field(codecType, fixture, "McCalls")! == 1, "FR-01 — multicast local execution preserved");

        // FR-01 — approval WITHOUT any scene transition (the no-scene positive control) unlocks normal sends.
        var version = serverType.GetProperty("ExpectedProtocolVersion")!.GetValue(server);
        serverType.GetMethod("OnClientVersionDeclared", new[] { typeof(long), typeof(string), typeof(string) })!
            .Invoke(server, new[] { connId, version, string.Empty });
        pump.Invoke(null, null);
        Invoke(codecType, fixture, "ClPing", 2);
        Invoke(codecType, fixture, "ClPingTo", connId, 2);
        Invoke(codecType, fixture, "McPing", 2);
        channel.Expect("UniNetSend", 3, "FR-01 — approved no-scene connection: broadcast + To + multicast all send");
        channel.Reset();

        // FR-01 — scene-held (AwaitingSceneReady) drops again; the ready report restores normal sends.
        serverType.GetMethod("LoadScene")!.Invoke(server, new object[] { "gate" });
        pump.Invoke(null, null);
        Invoke(codecType, fixture, "ClPing", 3);
        channel.Expect("UniNetSend", 0, "FR-01 — scene-held connection receives zero game RPCs");
        serverType.GetMethod("OnClientSceneLoaded", new[] { typeof(long), typeof(string) })!.Invoke(server, new object[] { connId, "gate" });
        pump.Invoke(null, null);
        Invoke(codecType, fixture, "ClPing", 3);
        channel.Expect("UniNetSend", 1, "FR-01 — ready report restores normal sends");

        // FR-03 — non-empty List + trailing scalar argument: count / elements / tail preserved on the real dispatch path.
        int callsBefore = (int)Field(codecType, fixture, "ListTailCalls")!;
        var wire = (byte[])codecType.GetMethod("__UniNetEncode_RpcListTail", AllFlags)!.Invoke(null,
            new object[] { netId, (byte)0, new List<int> { 11, 22, 33 }, 99 })!;
        byte[] expected;
        using (var w = global::MessageProtocol.Serialize.MessageBufferWriter.Create())
        {
            w.WriteUInt64(netId); w.WriteByte(0); w.WriteBoolean(false); w.WriteUInt16(3);
            w.WriteInt32(11); w.WriteInt32(22); w.WriteInt32(33); w.WriteInt32(99);
            expected = w.ToArray();
        }
        Check(wire.SequenceEqual(expected), "FR-03 — List{11,22,33}+tail wire bytes exact (flag + count + elements + tail)");
        Dispatch(dispatch, tailId, connId, wire);
        pump.Invoke(null, null);
        Check((int)Field(codecType, fixture, "ListTailCalls")! == callsBefore + 1, "FR-03 — dispatch reached the implementation exactly once");
        var got = Field(codecType, fixture, "LastValues") as List<int>;
        Check(got is { Count: 3 } && got[0] == 11 && got[1] == 22 && got[2] == 33, "FR-03 — received List count/elements restored");
        Check((int)Field(codecType, fixture, "LastTail")! == 99, "FR-03 — trailing argument after the List restored");

        // FR-03 — truncated collection payload: the implementation must NOT run (reader over-read is isolated).
        Dispatch(dispatch, tailId, connId, wire.Take(9 + 1 + 2 + 4 + 4).ToArray());   // count says 3, only two elements follow
        pump.Invoke(null, null);
        Check((int)Field(codecType, fixture, "ListTailCalls")! == callsBefore + 1, "FR-03 — truncated payload never reaches the implementation");

        // FR-03 — trailing (unconsumed) bytes: decode mismatch drops the call before the implementation.
        Dispatch(dispatch, tailId, connId, wire.Concat(new byte[] { 0xFF }).ToArray());
        pump.Invoke(null, null);
        Check((int)Field(codecType, fixture, "ListTailCalls")! == callsBefore + 1, "FR-03 — payload with unconsumed bytes never reaches the implementation");

        // ── F05 (AUDIT-20261027) — array receive contract on the real generated handler ──
        int arrayId = MethodId(fnv, $"{FixtureNs}.CodecRpcObject.RpcArrTail");

        // Array + trailing scalar: dispatch reaches the implementation; count/elements/tail restored.
        int arrCallsBefore = (int)Field(codecType, fixture, "ArrTailCalls")!;
        var arrWire = (byte[])codecType.GetMethod("__UniNetEncode_RpcArrTail", AllFlags)!.Invoke(null,
            new object[] { netId, (byte)0, new[] { 7, 8, 9 }, 5 })!;
        Dispatch(dispatch, arrayId, connId, arrWire);
        pump.Invoke(null, null);
        Check((int)Field(codecType, fixture, "ArrTailCalls")! == arrCallsBefore + 1, "F05 — array dispatch reached the implementation exactly once");
        var arrGot = Field(codecType, fixture, "LastArr") as int[];
        Check(arrGot is { Length: 3 } && arrGot[0] == 7 && arrGot[1] == 8 && arrGot[2] == 9, "F05 — received array count/elements restored");
        Check((int)Field(codecType, fixture, "LastArrTail")! == 5, "F05 — trailing argument after the array restored");

        // Null boundary: null flag only, no count bytes; the implementation receives null.
        var nullWire = (byte[])codecType.GetMethod("__UniNetEncode_RpcArrTail", AllFlags)!.Invoke(null,
            new object[] { netId, (byte)0, null!, 42 })!;
        Dispatch(dispatch, arrayId, connId, nullWire);
        pump.Invoke(null, null);
        Check(Field(codecType, fixture, "LastArr") is null && (int)Field(codecType, fixture, "LastArrTail")! == 42,
            "F05 — null array round-trips (null flag, no count bytes)");

        // Empty boundary: count 0, no element bytes.
        var emptyWire = (byte[])codecType.GetMethod("__UniNetEncode_RpcArrTail", AllFlags)!.Invoke(null,
            new object[] { netId, (byte)0, new int[0], 43 })!;
        Dispatch(dispatch, arrayId, connId, emptyWire);
        pump.Invoke(null, null);
        var emptyGot = Field(codecType, fixture, "LastArr") as int[];
        Check(emptyGot is { Length: 0 } && (int)Field(codecType, fixture, "LastArrTail")! == 43, "F05 — empty array round-trips (count 0)");
        Check((int)Field(codecType, fixture, "ArrTailCalls")! == arrCallsBefore + 3, "F05 — null and empty arrays both reached the implementation");

        // Header-only forged huge array — [netId 8][subId 1][null=false 1][count=65535 2], zero element bytes.
        // Must be rejected WITHOUT a count-sized pre-allocation and never reach the implementation (the allocation
        // probe mirrors the audit reproduction: warmed-up dispatch, then GC.GetAllocatedBytesForCurrentThread delta).
        byte[] forged;
        using (var fw = global::MessageProtocol.Serialize.MessageBufferWriter.Create())
        {
            fw.WriteUInt64(netId); fw.WriteByte(0); fw.WriteBoolean(false); fw.WriteUInt16(65535);
            forged = fw.ToArray();
        }
        for (int warm = 0; warm < 5; warm++) Dispatch(dispatch, arrayId, connId, forged);   // warm JIT/reflection paths
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        Dispatch(dispatch, arrayId, connId, forged);
        long forgedAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        pump.Invoke(null, null);
        Check((int)Field(codecType, fixture, "ArrTailCalls")! == arrCallsBefore + 3, "F05 — forged huge array never reaches the implementation");
        Check(forgedAlloc < 65536, $"F05 — forged huge array rejected before a count-sized allocation (allocated {forgedAlloc} bytes for a {forged.Length}-byte frame)");

        // No trailing argument may accidentally rescue a malformed length: clamping the count would turn these
        // truncated frames into valid empty/short arrays and wrongly invoke the implementation.
        int onlyId = MethodId(fnv, $"{FixtureNs}.CodecRpcObject.RpcArrOnly");
        int bytesId = MethodId(fnv, $"{FixtureNs}.CodecRpcObject.RpcBytesOnly");
        Dispatch(dispatch, onlyId, connId, forged);
        pump.Invoke(null, null);
        Check((int)Field(codecType, fixture, "ArrOnlyCalls")! == 0, "F05 — header-only terminal array is rejected, never clamped into empty success");
        byte[] shortBytes;
        using (var fw = global::MessageProtocol.Serialize.MessageBufferWriter.Create())
        {
            fw.WriteUInt64(netId); fw.WriteByte(0); fw.WriteBoolean(false); fw.WriteUInt16(2); fw.WriteByte(7);
            shortBytes = fw.ToArray();
        }
        Dispatch(dispatch, bytesId, connId, shortBytes);
        pump.Invoke(null, null);
        Check((int)Field(codecType, fixture, "BytesOnlyCalls")! == 0, "F05 — terminal byte array rejects a count exceeding its actual payload");
        int bytesBefore = (int)Field(codecType, fixture, "BytesOnlyCalls")!;
        var validBytes = (byte[])codecType.GetMethod("__UniNetEncode_RpcBytesOnly", AllFlags)!.Invoke(null,
            new object[] { netId, (byte)0, new byte[] { 1, 2 } })!;
        Dispatch(dispatch, bytesId, connId, validBytes);
        pump.Invoke(null, null);
        Check((int)Field(codecType, fixture, "BytesOnlyCalls")! == bytesBefore + 1
            && ((byte[])Field(codecType, fixture, "LastBytes")!).SequenceEqual(new byte[] { 1, 2 }), "F05 — valid terminal byte array still dispatches exactly");
        int onlyBefore = (int)Field(codecType, fixture, "ArrOnlyCalls")!;
        var validEmpty = (byte[])codecType.GetMethod("__UniNetEncode_RpcArrOnly", AllFlags)!.Invoke(null,
            new object[] { netId, (byte)0, Array.Empty<int>() })!;
        Dispatch(dispatch, onlyId, connId, validEmpty);
        pump.Invoke(null, null);
        Check((int)Field(codecType, fixture, "ArrOnlyCalls")! == onlyBefore + 1, "F05 — valid empty terminal array is not rejected");

        // FR-03 — 65535 allowed / 65536 explicitly rejected BEFORE serialization (arrays and Lists).
        foreach (var encode in new[] { "__UniNetEncode_RpcArrTail", "__UniNetEncode_RpcListTail" })
        {
            bool isList = encode.EndsWith("RpcListTail", StringComparison.Ordinal);
            object big = isList ? new List<int>(new int[65536]) : new int[65536];
            var ex = TryThrow(codecType, encode, netId, (byte)0, big, 7);
            Check(ex?.InnerException is ArgumentException, "FR-03 — 65536 elements rejected explicitly (" + encode + ")");

            object max = isList ? new List<int>(new int[65535]) : new int[65535];
            var wireMax = (byte[])codecType.GetMethod(encode, AllFlags)!.Invoke(null, new object[] { netId, (byte)0, max, 7 })!;
            Check(wireMax.Length == 9 + 1 + 2 + 65535 * 4 + 4, "FR-03 — 65535 elements encode (null flag + ushort count, no truncation) (" + encode + ")");   // 9 header + 1 null flag + 2 count + elements + 4 tail

            // F05 — a full 65,535-element payload still receives end-to-end on the real dispatch path: the receiver's
            // remaining-bytes guard never rejects a valid payload (each supported RPC element occupies >= 1 byte on the wire).
            string callsField = isList ? "ListTailCalls" : "ArrTailCalls";
            int maxCallsBefore = (int)Field(codecType, fixture, callsField)!;
            Dispatch(dispatch, isList ? tailId : arrayId, connId, wireMax);
            pump.Invoke(null, null);
            Check((int)Field(codecType, fixture, callsField)! == maxCallsBefore + 1, "FR-03/F05 — 65,535-element payload dispatches to the implementation (" + encode + ")");
        }

        // ── F13 (AUDIT-20261027) — legal user parameter names that collide with the generated plumbing, on the real paths ──
        // RpcHygiene(int netId, int w, int r, List<int> __n, int[] __i, int server): ServerRpc whose names collide with
        // the private encoder's frame fields (netId/subId), the writer/reader locals (w/r) and body locals (server) —
        // encode → real server dispatch → implementation must round-trip every value.
        int awaitId = MethodId(fnv, $"{FixtureNs}.CodecRpcObject.RpcAwait");
        var awaitWire = (byte[])codecType.GetMethod("__UniNetEncode_RpcAwait", AllFlags)!.Invoke(null,
            new object[] { netId, (byte)0, 7 })!;
        Dispatch(dispatch, awaitId, connId, awaitWire);
        pump.Invoke(null, null);
        Check((int)Field(codecType, fixture, "AwaitCalls")! == 1 && (int)Field(codecType, fixture, "LastAwait")! == 7,
            "F13 — contextual await parameter works through the generated async validation path");
        var invalidAwait = (byte[])codecType.GetMethod("__UniNetEncode_RpcAwait", AllFlags)!.Invoke(null,
            new object[] { netId, (byte)0, -1 })!;
        Dispatch(dispatch, awaitId, connId, invalidAwait);
        pump.Invoke(null, null);
        Check((int)Field(codecType, fixture, "AwaitCalls")! == 1,
            "F13 — contextual await escaping does not bypass validation rejection");

        int hygId = MethodId(fnv, $"{FixtureNs}.CodecRpcObject.RpcHygiene");
        int hygCallsBefore = (int)Field(codecType, fixture, "HygCalls")!;
        var hygWire = (byte[])codecType.GetMethod("__UniNetEncode_RpcHygiene", AllFlags)!.Invoke(null,
            new object[] { netId, (byte)0, 5, 2, 3, new List<int> { 4, 5 }, new[] { 6 }, 4 })!;
        Dispatch(dispatch, hygId, connId, hygWire);
        pump.Invoke(null, null);
        Check((int)Field(codecType, fixture, "HygCalls")! == hygCallsBefore + 1, "F13 — collision-named ServerRpc dispatch reached the implementation exactly once");
        var hygList = Field(codecType, fixture, "HygList") as List<int>;
        var hygArr = Field(codecType, fixture, "HygArr") as int[];
        Check((int)Field(codecType, fixture, "HygNetId")! == 5 && (int)Field(codecType, fixture, "HygW")! == 2 && (int)Field(codecType, fixture, "HygR")! == 3,
            "F13 — protocol-named args (netId/w/r) restored with their user values, not frame fields");
        Check(hygList is { Count: 2 } && hygList[0] == 4 && hygList[1] == 5 && hygArr is { Length: 1 } && hygArr[0] == 6,
            "F13 — loop-local-named args (__n/__i) restored exactly");
        Check((int)Field(codecType, fixture, "HygServer")! == 4, "F13 — body-local-named arg (server) restored with its user value");

        // ClHygiene(int subId, int senderConnId, int o, int connId, string e): ClientRpc colliding with the dispatch
        // frame (subId/senderConnId), the instance parameter (o), the To companion's recipient (connId) and the
        // element-write local (e). Broadcast + targeted To through the real channel, then the generated client dispatch.
        channel.Reset();
        Invoke(codecType, fixture, "ClHygiene", 5, 6, 7, 8, "evt");
        Invoke(codecType, fixture, "ClHygieneTo", connId, 5, 6, 7, 8, "evt");
        channel.Expect("UniNetSend", 2, "F13 — collision-named ClientRpc broadcast + targeted To both send");
        channel.Reset();
        Invoke(codecType, fixture, "ClHygieneToOwner", 5, 6, 7, 8, "evt");
        channel.Expect("UniNetSend", 1, "F13 — ToOwner sends once to the actual assigned owner");
        serverType.GetMethod("SetOwner")!.Invoke(server, new object[] { netId, 0L });
        channel.Reset();
        Invoke(codecType, fixture, "ClHygieneToOwner", 5, 6, 7, 8, "evt");
        channel.Expect("UniNetSend", 0, "F13 — ToOwner without an owner sends nothing and does not throw");
        serverType.GetMethod("SetOwner")!.Invoke(server, new object[] { netId, connId });
        int clHygBefore = (int)Field(codecType, fixture, "ClHygCalls")!;
        codecType.GetMethod("__UniNetClientDispatch_ClHygiene", AllFlags)!.Invoke(null,
            new object[] { fixture, 5, 6, 7, 8, "evt" });
        Check((int)Field(codecType, fixture, "ClHygCalls")! == clHygBefore + 1
            && (int)Field(codecType, fixture, "ClHygSubId")! == 5 && (int)Field(codecType, fixture, "ClHygSenderConnId")! == 6
            && (int)Field(codecType, fixture, "ClHygO")! == 7 && (int)Field(codecType, fixture, "ClHygConnId")! == 8
            && (string)Field(codecType, fixture, "ClHygE")! == "evt",
            "F13 — generated client dispatch restores every collision-named argument");

        // McHygiene(int owner, int sender, string @event): MulticastRpc colliding with the wrapper body locals
        // (owner/sender) and a keyword-named argument (@event). Local execution + the client-side re-entry dispatch.
        int mcHygBefore = (int)Field(codecType, fixture, "McHygCalls")!;
        channel.Reset();
        Invoke(codecType, fixture, "McHygiene", 9, 10, "mc");
        channel.Expect("UniNetSend", 1, "F13 — collision-named MulticastRpc still network-sends");
        Check((int)Field(codecType, fixture, "McHygCalls")! == mcHygBefore + 1 && (int)Field(codecType, fixture, "McHygOwner")! == 9
            && (int)Field(codecType, fixture, "McHygSender")! == 10 && (string)Field(codecType, fixture, "McHygEvent")! == "mc",
            "F13 — keyword and body-local-named multicast args run locally with user values");
        bool hygWasServer = (bool)codecType.GetProperty("IsServer")!.GetValue(fixture)!;
        codecType.GetProperty("IsServer")!.SetValue(fixture, false);
        codecType.GetMethod("__UniNetClientDispatch_Multicast_McHygiene", AllFlags)!.Invoke(null,
            new object[] { fixture, 11, 12, "mc2" });
        codecType.GetProperty("IsServer")!.SetValue(fixture, hygWasServer);
        Check((int)Field(codecType, fixture, "McHygCalls")! == mcHygBefore + 2 && (int)Field(codecType, fixture, "McHygOwner")! == 11
            && (int)Field(codecType, fixture, "McHygSender")! == 12 && (string)Field(codecType, fixture, "McHygEvent")! == "mc2",
            "F13 — generated multicast re-entry dispatch restores every collision-named argument");
    }

    // ── small helpers ──

    private static int MethodId(Type fnv, string idKey) => (int)fnv.GetMethod("MethodId")!.Invoke(null, new object[] { idKey })!;

    private static void Dispatch(Type dispatch, int methodId, long connId, byte[] wire)
    {
        var table = dispatch.GetMethod("ServerHandlers")!.Invoke(null, null)!;
        var entry = ((System.Collections.IDictionary)table)[methodId]!;
        var handler = (Delegate)entry.GetType().GetProperty("Handler")!.GetValue(entry)!;
        ((Task)handler.DynamicInvoke(connId, wire)!).GetAwaiter().GetResult();
    }

    private static object? Field(Type t, object o, string name) => t.GetField(name, AllFlags)!.GetValue(o);

    private static void Invoke(Type t, object o, string method, params object[] args)
        => t.GetMethod(method, AllFlags)!.Invoke(o, args);

    private static Exception? TryThrow(Type t, string method, params object[] args)
    {
        try { t.GetMethod(method, AllFlags)!.Invoke(null, args); return null; }
        catch (TargetInvocationException e) { return e; }
    }

    private const BindingFlags AllFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static void Check(bool ok, string label)
    {
        _checks++;
        Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + label);
        if (!ok) Failures.Add(label);
    }

    private static SyntaxTree Parse(string text) => CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.CSharp10));

    private static string? FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent!)
            if (Directory.Exists(Path.Combine(dir.FullName, "Package", "Runtime", "UniNet.Core")) && Directory.Exists(Path.Combine(dir.FullName, "CodeGenerator")))
                return dir.FullName;
        return null;
    }

    private static Assembly? ResolveFromDirs(string? name, params string[] dirs)
    {
        var simple = name?.Split(',')[0];
        if (simple == null) return null;
        foreach (var dir in dirs)
        {
            var path = Path.Combine(dir, simple + ".dll");
            if (File.Exists(path)) return Assembly.LoadFrom(path);
        }
        return null;
    }

    /// <summary>Recording IUniNetSystemChannel built with DispatchProxy — the interface lives in the emitted assembly, so the host cannot implement it at compile time. Must stay unsealed: DispatchProxy emits a derived proxy type.</summary>
    private class ChannelProxy : DispatchProxy
    {
        internal readonly List<string> Calls = new();

        public static ChannelProxy Create(Assembly asm)
        {
            var iface = asm.GetType("UniNet.Core.Hosting.IUniNetSystemChannel")!;
            var create = typeof(DispatchProxy).GetMethod("Create", Type.EmptyTypes)!.MakeGenericMethod(iface, typeof(ChannelProxy));
            return (ChannelProxy)create.Invoke(null, null)!;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod != null && !targetMethod.Name.EndsWith("UniNetConnId"))
                Calls.Add(targetMethod!.Name);
            if (targetMethod?.Name == "SendDisconnectNoticeAsync") return Task.CompletedTask;
            return targetMethod?.ReturnType == typeof(void) ? null : Activator.CreateInstance(targetMethod!.ReturnType);
        }

        internal void Expect(string method, int count, string label)
            => Check(Calls.Count(c => c == method) == count, $"{label} — {method} calls expected {count}, got {Calls.Count(c => c == method)}");

        internal void Reset() => Calls.Clear();
    }

    // ── compile-time sources: engine shim, UniNet.Unity shim, RPC fixtures ──
    // Core is engine-free ("noEngineReferences"); these shims exist ONLY for the generated hub code and fixtures.

    private const string ShimUnityEngine = @"
namespace UnityEngine
{
    public static class Debug
    {
        public static void Log(object m) { }
        public static void LogWarning(object m) { }
        public static void LogError(object m) { }
        public static void LogException(System.Exception e) { }
    }
    public enum RuntimeInitializeLoadType { AfterAssembliesLoaded }
    public sealed class RuntimeInitializeOnLoadMethodAttribute : System.Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute() { }
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType t) { }
    }
}
";

    private const string ShimUniNetUnity = @"
namespace UniNet.Unity
{
    public abstract class NetworkBehaviour
    {
        public ulong NetId { get; set; }
        public byte SubId { get; set; }
        public bool IsServer { get; set; }
        public bool IsClient => false;
        public bool IsOwner => false;
    }
    public static class UniNetSpawn
    {
        public static void Apply(ulong netId, float px, float py, float pz, float qx, float qy, float qz, float qw,
            ulong prefabId, byte subCount, ulong[] typeKeys, byte[][] states) { }
        public static void Remove(ulong netId) { }
    }
    public static class UniNetManager
    {
        public static void OnSceneTransitionRequested(string scene) { }
    }
}
";

    private const string FixtureSources = @"
namespace UniNet.Tests.Headless
{
    public sealed partial class CodecRpcObject : global::UniNet.Unity.NetworkBehaviour
    {
        public int ListTailCalls;
        public System.Collections.Generic.List<int> LastValues;
        public int LastTail;
        public int ArrTailCalls;
        public int[] LastArr;
        public int LastArrTail;
        public int ArrOnlyCalls;
        public int BytesOnlyCalls;
        public byte[] LastBytes;
        public int ClCalls;
        public int McCalls;
        public int AwaitCalls;
        public int LastAwait;

        [global::UniNet.Core.ServerRpc(Validate = true)]
        internal partial void RpcAwait(int await);
        private System.Threading.Tasks.Task<bool> RpcAwait_Validate(int await) => System.Threading.Tasks.Task.FromResult(await >= 0);
        private void RpcAwait_Implementation(int await) { AwaitCalls++; LastAwait = await; }

        // F13 (AUDIT-20261027) — collision-named fixtures: legal parameter names that hit generated plumbing.
        public int HygCalls; public int HygNetId; public int HygW; public int HygR;
        public System.Collections.Generic.List<int> HygList; public int[] HygArr; public int HygServer;
        public int ClHygCalls; public int ClHygSubId; public int ClHygSenderConnId; public int ClHygO; public int ClHygConnId; public string ClHygE;
        public int McHygCalls; public int McHygOwner; public int McHygSender; public string McHygEvent;

        [global::UniNet.Core.ServerRpc]
        internal partial void RpcHygiene(int netId, int w, int r, System.Collections.Generic.List<int> __n, int[] __i, int server);
        private void RpcHygiene_Implementation(int netId, int w, int r, System.Collections.Generic.List<int> __n, int[] __i, int server)
        { HygCalls++; HygNetId = netId; HygW = w; HygR = r; HygList = __n; HygArr = __i; HygServer = server; }

        [global::UniNet.Core.ClientRpc]
        internal partial void ClHygiene(int subId, int senderConnId, int o, int connId, string e);
        private void ClHygiene_Implementation(int subId, int senderConnId, int o, int connId, string e)
        { ClHygCalls++; ClHygSubId = subId; ClHygSenderConnId = senderConnId; ClHygO = o; ClHygConnId = connId; ClHygE = e; }

        [global::UniNet.Core.MulticastRpc]
        internal partial void McHygiene(int owner, int sender, string @event);
        private void McHygiene_Implementation(int owner, int sender, string @event)
        { McHygCalls++; McHygOwner = owner; McHygSender = sender; McHygEvent = @event; }

        [global::UniNet.Core.ServerRpc]
        internal partial void RpcListTail(System.Collections.Generic.List<int> values, int tail);
        private void RpcListTail_Implementation(System.Collections.Generic.List<int> values, int tail)
        { ListTailCalls++; LastValues = values; LastTail = tail; }

        [global::UniNet.Core.ServerRpc]
        internal partial void RpcArrTail(int[] values, int tail);
        private void RpcArrTail_Implementation(int[] values, int tail) { ArrTailCalls++; LastArr = values; LastArrTail = tail; }

        [global::UniNet.Core.ServerRpc]
        internal partial void RpcArrOnly(int[] values);
        private void RpcArrOnly_Implementation(int[] values) { ArrOnlyCalls++; }

        [global::UniNet.Core.ServerRpc]
        internal partial void RpcBytesOnly(byte[] values);
        private void RpcBytesOnly_Implementation(byte[] values) { BytesOnlyCalls++; LastBytes = values; }

        [global::UniNet.Core.ClientRpc]
        internal partial void ClPing(int x);
        private void ClPing_Implementation(int x) { ClCalls++; }

        [global::UniNet.Core.MulticastRpc]
        internal partial void McPing(int x);
        private void McPing_Implementation(int x) { McCalls++; }
    }
}
";

    private const string NegativeFixture = @"
namespace UniNet.Tests.Headless
{
    // Deliberate negative probe — nested collections are unsupported (UNINET002). Driver diagnostics only —
    // this compilation is never emitted (an unimplemented `internal partial` would be CS8795 by design).
    public sealed partial class BadRpcObject : global::UniNet.Unity.NetworkBehaviour
    {
        [global::UniNet.Core.ServerRpc]
        internal partial void RpcNested(System.Collections.Generic.List<System.Collections.Generic.List<int>> nested);
        private void RpcNested_Implementation(System.Collections.Generic.List<System.Collections.Generic.List<int>> nested) { }

        [global::UniNet.Core.Replicated(Serializer = typeof(ZeroSerializer))]
        public ZeroElement[] Field;
        [global::UniNet.Core.ServerRpc]
        internal partial void RpcCustomOnly(ZeroElement[] values);
        private void RpcCustomOnly_Implementation(ZeroElement[] values) { }
    }
    public struct ZeroElement { }
    public static class ZeroSerializer
    {
        public static void Write(ref global::MessageProtocol.Serialize.MessageBufferWriter writer, in ZeroElement value) { }
        public static ZeroElement Read(ref global::MessageProtocol.Serialize.MessageBufferReader reader) => default;
    }
}
";

    private const string NamedArgProbe = @"
namespace UniNet.Tests.Headless
{
    // F13 — existing user call sites bind by name: the public surfaces must keep the user's parameter names.
    public static class NamedArgProbe
    {
        public static void Call(CodecRpcObject o, long c)
        {
            o.RpcHygiene(netId: 1, w: 2, r: 3, __n: null, __i: null, server: 4);
            o.RpcAwait(@await: 7);
            o.ClHygiene(subId: 5, senderConnId: 6, o: 7, connId: 8, e: ""evt"");
            o.ClHygieneTo(c, subId: 5, senderConnId: 6, o: 7, connId: 8, e: ""evt"");
            o.ClHygieneToOwner(subId: 5, senderConnId: 6, o: 7, connId: 8, e: ""evt"");
            o.McHygiene(owner: 9, sender: 10, @event: ""mc"");
        }
    }
}
";
}
