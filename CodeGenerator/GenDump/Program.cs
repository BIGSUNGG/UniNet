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
};
var fixtureTrees = fixtureFiles.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f))).ToList();

var stubs = @"
namespace UniNet.Unity { public abstract class NetworkBehaviour { } }
namespace UniNet.Core {
    public enum Delivery { ReliableOrdered, Unreliable }
    public sealed class ServerRpcAttribute : System.Attribute { }
    public sealed class ClientRpcAttribute : System.Attribute { public Delivery Delivery { get; } public ClientRpcAttribute(Delivery d = Delivery.ReliableOrdered) { Delivery = d; } }
    public sealed class MulticastRpcAttribute : System.Attribute { public Delivery Delivery { get; } public MulticastRpcAttribute(Delivery d = Delivery.ReliableOrdered) { Delivery = d; } }
    public sealed class ReplicatedAttribute : System.Attribute { public string Notify { get; set; } }
}
namespace MessageProtocol {
    public enum MessageKind { Automatic = 0, Standalone = 1, Parent = 2, Child = 3, NonId = 4 }
    public class MessageAttribute : System.Attribute { public MessageAttribute(MessageKind kind = MessageKind.Automatic, uint id = 0, object category = null) { } }
}
namespace UnityEngine { public static class Debug { public static void Log(object m){} public static void LogException(Exception e){} } }
";
fixtureTrees.Add(CSharpSyntaxTree.ParseText(stubs));

var rtPath = Path.GetDirectoryName(typeof(object).Assembly.Location);
var refs = new[]
{
    MetadataReference.CreateFromFile(Path.Combine(rtPath, "System.Runtime.dll")),
    MetadataReference.CreateFromFile(Path.Combine(rtPath, "System.Threading.Tasks.dll")),
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
