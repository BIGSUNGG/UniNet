using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UniNet.CodeGenerator;

var fixtureFiles = new[]
{
    @"C:/Projects/DS/UniNet/Sandbox/Assets/Tests/Fixtures/VerifyPlayer.cs",
    @"C:/Projects/DS/UniNet/Sandbox/Assets/Tests/Fixtures/PayloadMsg.cs",
    @"C:/Projects/DS/UniNet/Sandbox/Assets/Tests/Fixtures/DerivedPayloadMsg.cs",
    @"C:/Projects/DS/UniNet/Sandbox/Assets/Tests/Fixtures/MovementBrain.cs",
    @"C:/Projects/DS/UniNet/Sandbox/Assets/Tests/Fixtures/HealthTank.cs",
    @"C:/Projects/DS/UniNet/Sandbox/Assets/Tests/Fixtures/QuantizedTank.cs",
    @"C:/Projects/DS/UniNet/Sandbox/Assets/Tests/Fixtures/FastArrayTank.cs",
};
var fixtureTrees = fixtureFiles.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f))).ToList();

var stubs = @"
namespace UniNet.Unity { public abstract class NetworkBehaviour { } }
namespace UniNet.Core {
    public enum Delivery { ReliableOrdered, Unreliable }
    public sealed class ServerRpcAttribute : System.Attribute { public bool RequireOwnership { get; set; } }
    public sealed class ClientRpcAttribute : System.Attribute { public Delivery Delivery { get; } public ClientRpcAttribute(Delivery d = Delivery.ReliableOrdered) { Delivery = d; } }
    public sealed class MulticastRpcAttribute : System.Attribute { public Delivery Delivery { get; } public MulticastRpcAttribute(Delivery d = Delivery.ReliableOrdered) { Delivery = d; } }
    public sealed class ReplicatedAttribute : System.Attribute { public string Notify { get; set; } public System.Type Serializer { get; set; } }
}
namespace MessageProtocol.Serialize {
    public struct MessageBufferWriter { public static MessageBufferWriter Create() => default; public void WriteBoolean(bool v) {} public void WriteSByte(sbyte v) {} public void WriteByte(byte v) {} public void WriteInt16(short v) {} public void WriteUInt16(ushort v) {} public void WriteInt32(int v) {} public void WriteUInt32(uint v) {} public void WriteInt64(long v) {} public void WriteUInt64(ulong v) {} public void WriteSingle(float v) {} public void WriteDouble(double v) {} public void WriteString(string v) {} public void WriteBytes(byte[] v) {} public byte[] ToArray() => null; }
    public struct MessageBufferReader { public int Remaining => 0; public bool ReadBoolean() => default; public sbyte ReadSByte() => default; public byte ReadByte() => default; public short ReadInt16() => default; public ushort ReadUInt16() => default; public int ReadInt32() => default; public uint ReadUInt32() => default; public long ReadInt64() => default; public ulong ReadUInt64() => default; public float ReadSingle() => default; public double ReadDouble() => default; public string ReadString() => default; public byte[] ReadBytes(int n) => null; }
}
namespace MessageProtocol {
    public enum MessageKind { Automatic = 0, Standalone = 1, Parent = 2, Child = 3, NonId = 4 }
    public class MessageAttribute : System.Attribute { public MessageAttribute(MessageKind kind = MessageKind.Automatic, uint id = 0, object category = null) { } }
}
namespace UnityEngine { public static class Debug { public static void Log(object m){} public static void LogException(Exception e){} } public struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } } }
";
fixtureTrees.Add(CSharpSyntaxTree.ParseText(stubs));

var rtPath = Path.GetDirectoryName(typeof(object).Assembly.Location);
var refs = new[]
{
    MetadataReference.CreateFromFile(Path.Combine(rtPath, "System.Runtime.dll")),
    MetadataReference.CreateFromFile(Path.Combine(rtPath, "System.Threading.Tasks.dll")),
    MetadataReference.CreateFromFile(Path.Combine(rtPath, "System.Collections.dll")),
    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
};
var comp = CSharpCompilation.Create("Fixtures", fixtureTrees, refs,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
var driver = CSharpGeneratorDriver.Create(new UniNetCodeGenerator());
var result = driver.RunGenerators(comp).GetRunResult();
foreach (var diag in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
    Console.WriteLine("GEN-DIAG: " + diag);
foreach (var tree in result.GeneratedTrees)
    File.WriteAllText(@"C:/Projects/DS/UniNet/.gendump-fixture.cs", tree.ToString());
Console.WriteLine("trees=" + result.GeneratedTrees.Length);
