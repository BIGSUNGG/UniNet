using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UniNet.CodeGenerator;

var fixtures = File.ReadAllText(@"C:/Projects/DS/UniNet/Sandbox/Assets/Scripts/Player.cs");
var stubs = @"
namespace UniNet.Unity { public abstract class NetworkBehaviour { } }
namespace UniNet.Core {
    public enum Delivery { ReliableOrdered, Unreliable }
    public sealed class ServerRpcAttribute : Attribute { }
    public sealed class ClientRpcAttribute : Attribute { public Delivery Delivery { get; } public ClientRpcAttribute(Delivery d = Delivery.ReliableOrdered) { Delivery = d; } }
    public sealed class MulticastRpcAttribute : Attribute { public Delivery Delivery { get; } public MulticastRpcAttribute(Delivery d = Delivery.ReliableOrdered) { Delivery = d; } }
    public sealed class ReplicatedAttribute : Attribute { public string Notify { get; set; } }
}
namespace UnityEngine {
    public static class Debug { public static void Log(object m){} public static void LogException(Exception e){} }
    public static class Input { public static bool GetKeyDown(object k) => false; }
    public class KeyCode { public static object Space = new object(); }
}
";
var trees = new[] { CSharpSyntaxTree.ParseText(stubs), CSharpSyntaxTree.ParseText(fixtures) };
var rtPath = Path.GetDirectoryName(typeof(object).Assembly.Location);
var refs = new[] {
    MetadataReference.CreateFromFile(Path.Combine(rtPath, "System.Runtime.dll")),
    MetadataReference.CreateFromFile(Path.Combine(rtPath, "System.Threading.Tasks.dll")),
    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
};
var comp = CSharpCompilation.Create("Fixtures", trees, refs,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
var driver = CSharpGeneratorDriver.Create(new UniNetCodeGenerator());
var result = driver.RunGenerators(comp).GetRunResult();
foreach (var diag in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
    Console.WriteLine("GEN-DIAG: " + diag);
foreach (var tree in result.GeneratedTrees)
    File.WriteAllText(@"C:/Projects/DS/UniNet/.gendump-player.cs", tree.ToString());
Console.WriteLine("trees=" + result.GeneratedTrees.Length);
