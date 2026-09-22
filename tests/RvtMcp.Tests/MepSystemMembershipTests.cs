using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace RvtMcp.Tests
{
    // Compile the actual Revit adapter with small API doubles in an isolated assembly.
    // This covers source selection and read failures; real connector behavior needs Revit.
    public class MepSystemMembershipTests
    {
        private static readonly Lazy<MethodInfo> Probe = new Lazy<MethodInfo>(() => CompileProbe());

        [Theory]
        [InlineData("piping", "3|0|False|1,2,3")]
        [InlineData("mechanical", "3|0|False|1,2,3")]
        [InlineData("electrical", "1|1|False|4")]
        [InlineData("fixture", "0|1|False|4")]
        [InlineData("base", "0|0|False|5")]
        [InlineData("empty", "0|0|True|")]
        [InlineData("mixed", "3|2|False|1,2,3,4,5")]
        public void ReadsCorrectMembersAndDeduplicatesInventory(string scenario, string expected)
        {
            Assert.Equal(expected, Run(scenario));
        }

        [Theory]
        [InlineData("network-throws")]
        [InlineData("terminal-throws")]
        [InlineData("base-throws")]
        [InlineData("network-null")]
        [InlineData("terminal-null")]
        [InlineData("enumeration-throws")]
        [InlineData("null-member")]
        public void FailedReadsNeverProduceAnEmptySnapshot(string scenario)
        {
            var error = Assert.Throws<TargetInvocationException>(() => Run(scenario));
            var failure = Assert.IsType<InvalidOperationException>(error.InnerException);
            Assert.StartsWith("Failed to read MEP system membership:", failure.Message);
        }

        private static string Run(string scenario) => (string)Probe.Value.Invoke(null, new object[] { scenario });

        private static MethodInfo CompileProbe([CallerFilePath] string testFile = "")
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
            var sources = new[] { "MepSystemMembership.cs", "MepMembershipPolicy.cs" }
                .Select(name => File.ReadAllText(Path.Combine(root, "src", "shared", "Handlers", name)))
                .Append(ApiAndProbe).Select(source => CSharpSyntaxTree.ParseText(source));
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("MembershipProbe_" + Guid.NewGuid().ToString("N"),
                sources, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            using var output = new MemoryStream();
            var result = compilation.Emit(output);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            return Assembly.Load(output.ToArray()).GetType("MembershipProbe")!.GetMethod("Run")!;
        }

        private const string ApiAndProbe = """
using System;
using System.Collections;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using RvtMcp.Plugin.Handlers;
namespace Autodesk.Revit.DB {
    public class ElementId { public long Value; }
    public class Element { public ElementId Id; public Element(long id) { Id = new ElementId { Value = id }; } }
    public class FamilyInstance : Element { public FamilyInstance(long id) : base(id) {} }
    public class ElementSet : IEnumerable {
        private readonly Element[] items;
        public bool ThrowOnEnumeration;
        public ElementSet(params Element[] items) { this.items = items; }
        public IEnumerator GetEnumerator() {
            if (ThrowOnEnumeration) throw new InvalidOperationException("enumeration failed");
            return items.GetEnumerator();
        }
    }
    public class MEPSystem {
        public ElementSet Terminals = new ElementSet();
        public FamilyInstance Equipment;
        public bool ThrowTerminals, ThrowBase;
        public int TerminalReads, BaseReads;
        public ElementSet Elements { get { TerminalReads++; if (ThrowTerminals) throw new Exception("terminal failed"); return Terminals; } }
        public FamilyInstance BaseEquipment { get { BaseReads++; if (ThrowBase) throw new Exception("base failed"); return Equipment; } }
    }
}
namespace Autodesk.Revit.DB.Plumbing {
    public class PipingSystem : MEPSystem {
        public ElementSet Network = new ElementSet();
        public bool ThrowNetwork;
        public int NetworkReads;
        public ElementSet PipingNetwork { get { NetworkReads++; if (ThrowNetwork) throw new Exception("network failed"); return Network; } }
    }
}
namespace Autodesk.Revit.DB.Mechanical {
    public class MechanicalSystem : MEPSystem {
        public ElementSet Network;
        public int NetworkReads;
        public ElementSet DuctNetwork { get { NetworkReads++; return Network; } }
    }
}
namespace RvtMcp.Plugin { public static class RevitCompat { public static long GetId(ElementId id) => id.Value; } }
public static class MembershipProbe {
    public static string Run(string scenario) {
        var pipe = new PipingSystem();
        MEPSystem system = pipe;
        var network = new ElementSet(new Element(1), new Element(2), new Element(3));
        switch (scenario) {
            case "piping": pipe.Network = network; break;
            case "mechanical": system = new MechanicalSystem { Network = network }; break;
            case "electrical": system = new MEPSystem { Terminals = new ElementSet(new Element(4)) }; break;
            case "fixture": pipe.Terminals = new ElementSet(new Element(4)); break;
            case "base": pipe.Equipment = new FamilyInstance(5); break;
            case "empty": break;
            case "mixed": pipe.Network = network; pipe.Terminals = new ElementSet(new Element(3), new Element(4)); pipe.Equipment = new FamilyInstance(5); break;
            case "network-throws": pipe.ThrowNetwork = true; break;
            case "terminal-throws": pipe.ThrowTerminals = true; break;
            case "base-throws": pipe.ThrowBase = true; break;
            case "network-null": pipe.Network = null; break;
            case "terminal-null": pipe.Terminals = null; break;
            case "enumeration-throws": pipe.Network = new ElementSet { ThrowOnEnumeration = true }; break;
            case "null-member": pipe.Network = new ElementSet(new Element[] { null }); break;
            default: throw new Exception("unknown scenario");
        }
        var snapshot = MepSystemMembership.Read(system);
        if (system.TerminalReads != 1 || system.BaseReads != 1 ||
            (system is PipingSystem p && p.NetworkReads != 1) ||
            (system is MechanicalSystem m && m.NetworkReads != 1)) throw new Exception("membership read more than once");
        return snapshot.Counts.ElementCount + "|" + snapshot.Counts.TerminalCount + "|" + snapshot.Counts.IsEmpty + "|" +
            string.Join(",", snapshot.AllElements.Select(e => e.Id.Value));
    }
}
""";
    }
}
