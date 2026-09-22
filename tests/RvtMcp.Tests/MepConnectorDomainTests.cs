using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace RvtMcp.Tests
{
    public class MepConnectorDomainTests
    {
        private static readonly Lazy<MethodInfo> Probe = new(() => CompileProbe());

        [Theory]
        [InlineData("piping", false)]
        [InlineData("mechanical", false)]
        [InlineData("electrical", false)]
        [InlineData("piping", true)]
        [InlineData("mechanical", true)]
        [InlineData("electrical", true)]
        public void CountsOnlyOpenPhysicalConnectorsInTheSystemsDomain(string domain, bool familyInstance)
        {
            Assert.Equal(1, (int)Probe.Value.Invoke(null, new object[] { domain, familyInstance })!);
        }

        private static MethodInfo CompileProbe([CallerFilePath] string testFile = "")
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
            var source = File.ReadAllText(Path.Combine(root, "src", "shared", "Handlers", "AnalyzeMepNetworkHandler.cs"));
            // Execute the production connector traversal and manager lookup with API doubles.
            var methods = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Where(m => m.Identifier.ValueText is "CountOpenEndConnectors" or "GetConnectorManager")
                .Select(m => m.ToFullString());
            var probeSource = "using System; using Autodesk.Revit.DB; public class ConnectorProbe {" +
                string.Join(Environment.NewLine, methods) + ProbeBody + "}" + Api;
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("ConnectorProbe_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(probeSource) }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var output = new MemoryStream();
            var result = compilation.Emit(output);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            return Assembly.Load(output.ToArray()).GetType("ConnectorProbe")!.GetMethod("Run")!;
        }

        private const string ProbeBody = """
public static int Run(string domain, bool familyInstance) {
    var connectors = new ConnectorSet();
    foreach (Domain d in new[] {Domain.DomainPiping, Domain.DomainHvac, Domain.DomainElectrical}) {
        connectors.Add(new Connector {Domain=d, ConnectorType=ConnectorType.End});
        connectors.Add(new Connector {Domain=d, ConnectorType=ConnectorType.End, IsConnected=true});
        connectors.Add(new Connector {Domain=d, ConnectorType=ConnectorType.Logical});
        connectors.Add(new Connector {Domain=d, ConnectorType=ConnectorType.Curve});
    }
    var manager=new ConnectorManager {Connectors=connectors};
    Element element=familyInstance
        ? (Element)new FamilyInstance {MEPModel=new MEPModel {ConnectorManager=manager}}
        : new MEPCurve {ConnectorManager=manager};
    var method=typeof(ConnectorProbe).GetMethod("CountOpenEndConnectors",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    // The legacy one-argument traversal demonstrates the bug by returning 3.
    var args=method.GetParameters().Length==1 ? new object[]{element} : new object[]{element,domain};
    return (int)method.Invoke(null,args);
}
""";

        private const string Api = """
namespace Autodesk.Revit.DB {
    public enum Domain {DomainUndefined, DomainPiping, DomainHvac, DomainElectrical}
    public enum ConnectorType {End, Logical, Curve}
    public class Element {}
    public class MEPCurve : Element {public ConnectorManager ConnectorManager;}
    public class FamilyInstance : Element {public MEPModel MEPModel;}
    public class MEPModel {public ConnectorManager ConnectorManager;}
    public class ConnectorManager {public ConnectorSet Connectors;}
    public class ConnectorSet : System.Collections.Generic.List<Connector> {}
    public class Connector {public Domain Domain; public ConnectorType ConnectorType; public bool IsConnected;}
}
""";
    }
}
