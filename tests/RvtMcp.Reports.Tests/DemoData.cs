using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using RvtMcp.Plugin.Reports.Model;
using RvtMcp.Plugin.Reports.Presentation;

/// <summary>Synthetic reports only: DEMO-A.rvt, DEMO-B.rvt and the token-only family Demo-pump.rfa.</summary>
internal static class DemoData
{
    public static readonly DateTime Now = new DateTime(2026, 10, 28, 15, 0, 0, DateTimeKind.Local);
    public const string TokenA = "0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a";
    public const string TokenB = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";
    public const string TokenPump = "0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c0c";
    public const string LongReport = "r06", BulkReport = "r07";
    public const int BulkCount = 5000;

    public static ActiveDocumentIdentity ActiveA => new ActiveDocumentIdentity { Token = TokenA, ModelKey = "demo-key-a", Title = "DEMO-A.rvt" };
    public static ActiveDocumentIdentity ActiveB => new ActiveDocumentIdentity { Token = TokenB, ModelKey = "demo-key-b", Title = "DEMO-B.rvt" };
    public static ActiveDocumentIdentity ActivePump => new ActiveDocumentIdentity { Token = TokenPump, Title = "Demo-pump.rfa" };

    public static ReportDocument DocA(string reference = "d0") => new ReportDocument
        { Ref = reference, Token = TokenA, ModelKey = "demo-key-a", Title = "DEMO-A.rvt", Kind = ReportDocumentKinds.Central };
    public static ReportDocument DocB(string reference = "d0") => new ReportDocument
        { Ref = reference, Token = TokenB, ModelKey = "demo-key-b", Title = "DEMO-B.rvt", Kind = ReportDocumentKinds.File };
    public static ReportDocument DocPump(string reference) => new ReportDocument
        { Ref = reference, Token = TokenPump, Title = "Demo-pump.rfa", IsFamily = true, Kind = ReportDocumentKinds.Unsaved };

    public static List<AgentReport> Reports()
    {
        var reports = new List<AgentReport>
        {
            Report("r01", "completed", "Moved 6 valves to the level 2 branch and reconnected pump P-2", Local(0, 14, 32), DocA(),
                body: "# Valve relocation on level 2\n\n" +
                      "Moved [6 valves V-01…V-06](revit:#0) to the new branch and reconnected [Pump P-2](revit:#1).\n" +
                      "Clearances were checked in [Level 2 - Mechanical](revit:#2); the issue sheet is [M-201](revit:#3).\n\n" +
                      "## What changed\n" +
                      "- Valve family `M_Gate Valve` swapped to the 50 mm type\n" +
                      "- Branch pipes resized:\n" +
                      "  - DN50 → DN65 on the supply side\n" +
                      "  - DN40 unchanged on the return\n" +
                      "- *Insulation* parameters copied from the **main run**\n\n" +
                      "| Tag | Before | After |\n| --- | --- | --- |\n| V-01 | DN50 | DN65 |\n| V-04 | DN50 | DN65 |\n\n" +
                      "---\n\n" +
                      "Next step: review the pump curve with the mechanical lead.",
                links: new[]
                {
                    Link("6 valves V-01…V-06", "d0", Enumerable.Range(3101, 6).Select(id => Element(id, "Pipe Accessories")).ToArray()),
                    Link("Pump P-2", "d0", Element(2207, "Mechanical Equipment")),
                    Link("Level 2 - Mechanical", "d0", Element(512, "Views", ReportElementKinds.View)),
                    Link("M-201", "d0", Element(640, "Sheets", ReportElementKinds.Sheet)),
                },
                changes: Changes(true, false, Group("d0",
                    Enumerable.Range(4101, 4).Select(id => Changed(id, "Pipe Fittings")),
                    Enumerable.Range(3101, 6).Select(id => Changed(id, "Pipe Accessories")).Concat(new[] { Changed(3201, "Pipes"), Changed(3202, "Pipes") }),
                    new[] { Deleted(3301) }))),
            Report("r02", "failed", "Could not resize the inlet connector of the pump family", Local(0, 13, 5), DocA(),
                extraDocuments: new[] { DocPump("d1") },
                body: "Tried to resize [the inlet connector](revit:#0) in Demo-pump.rfa, but reloading the family failed.\n" +
                      "On the project side only [2 pipe fittings](revit:#1) were touched.",
                links: new[]
                {
                    Link("the inlet connector", "d1", Element(88, "Connectors")),
                    Link("2 pipe fittings", "d0", Element(4101, "Pipe Fittings"), Element(4102, "Pipe Fittings")),
                },
                changes: Changes(true, false,
                    Group("d1", null, new[] { Changed(88, "Connectors"), Changed(91, "Reference Planes"), Changed(92, "Reference Planes") }, new[] { Deleted(95) }),
                    Group("d0", null, new[] { Changed(4101, "Pipe Fittings"), Changed(4102, "Pipe Fittings") }, null))),
            Report("r03", "cancelled", "Stopped the duct check on DEMO-B before any change", Local(-1, 16, 40), DocB(),
                body: "Stopped before checking [Level 1 ducts](revit:#0); nothing was changed.",
                links: new[] { Link("Level 1 ducts", "d0", Element(7001, "Ducts"), Element(7002, "Ducts")) },
                changes: null),
            Report("r04", "completed", "Checked the room tags on level 1: nothing to change", Local(-2, 9, 12), DocA(),
                body: "All room tags on level 1 were already in place.", changes: new ReportChanges { Complete = true }),
            Report("r05", "completed", "Renumbered the M sheets; change capture was partial", Local(-3, 11, 30), DocA(),
                body: "Renumbered the sheets in the **M** series.",
                changes: Changes(false, true, Group("d0", null, new[] { Changed(640, "Sheets"), Changed(641, "Sheets"), Changed(642, "Sheets") }, null))),
            LongBodyReport(),
            BulkChangesReport(),
        };
        for (var n = 8; n <= 27; n++)
        {
            var onA = n % 2 == 0;
            reports.Add(Report("r" + n.ToString("00", CultureInfo.InvariantCulture), n % 5 == 0 ? "failed" : "completed",
                "Routine check " + n + " on " + (onA ? "DEMO-A.rvt" : "DEMO-B.rvt"), Local(-(n - 3), 10, n), onA ? DocA() : DocB(),
                body: "Routine check " + n + " found nothing new.", changes: new ReportChanges { Complete = true }));
        }
        return reports;
    }

    /// <summary>About 32 KiB of mixed Markdown with 40 distinct links.</summary>
    public static AgentReport LongBodyReport()
    {
        const int linkCount = 40;
        var body = new StringBuilder("# Model audit\n\n");
        for (var i = 1; Encoding.UTF8.GetByteCount(body.ToString()) < 32 * 1024 - 420; i++)
        {
            var k = i % (linkCount - 1);
            body.Append("## Section ").Append(i).Append("\n");
            body.Append("Checked [element group ").Append(i).Append("](revit:#").Append(k).Append(") on level ").Append(i % 5)
                .Append(". Some **bold** and *italic* words, `Mark_").Append(i).Append("` and a second line\nwith a line break.\n");
            body.Append("- first item of section ").Append(i).Append("\n");
            body.Append("- second item with [another link](revit:#").Append(k + 1).Append(")\n\n");
            body.Append("| Field | Value |\n| --- | --- |\n| Section | ").Append(i).Append(" |\n\n");
        }
        var links = Enumerable.Range(0, linkCount).Select(i => i % 3 == 0
            ? Link("group " + i, "d0", Element(20000 + i * 2, "Walls"), Element(20001 + i * 2, "Walls"))
            : Link("group " + i, "d0", Element(20000 + i * 2, "Walls"))).ToArray();
        return Report(LongReport, "completed", "Model audit with a long report body", Local(-4, 10, 0), DocA(), body: body.ToString(), links: links,
            changes: new ReportChanges { Complete = true });
    }

    public static AgentReport BulkChangesReport()
    {
        var categories = new[] { "Pipes", "Pipe Fittings", "Ducts", "Generic Models" };
        return Report(BulkReport, "completed", "Updated the Mark parameter on 5,000 elements", Local(-5, 17, 45), DocA(),
            body: "Set **Mark** on every pipe, fitting and duct.",
            changes: Changes(true, false, Group("d0", Enumerable.Range(0, BulkCount).Select(i => Changed(100000 + i, categories[i % categories.Length])), null, null)));
    }

    public static AgentReport Report(string id, string outcome, string summary, string createdUtc, ReportDocument primary,
        string body = null, ReportLink[] links = null, ReportChanges changes = null, ReportDocument[] extraDocuments = null)
    {
        var documents = new List<ReportDocument> { primary };
        if (extraDocuments != null) documents.AddRange(extraDocuments);
        return new AgentReport
        {
            ReportId = id, TaskId = "task-" + id, Outcome = outcome, Summary = summary, Body = body,
            CreatedUtc = createdUtc, UpdatedUtc = createdUtc, Documents = documents,
            Links = (links ?? new ReportLink[0]).ToList(), Changes = changes,
            Source = new ReportSource { TargetId = "revit-2022-1000", Pid = 1000, HostYear = 2022 }
        };
    }

    public static ReportLink Link(string label, string doc, params ReportElement[] elements) =>
        new ReportLink { Label = label, Doc = doc, Elements = elements.ToList() };

    public static ReportElement Element(long id, string category, string kind = ReportElementKinds.Element) =>
        new ReportElement { Id = id, UniqueId = "demo-uid-" + id, Kind = kind, Category = category, Name = category + " " + id };

    public static ReportChangedElement Changed(long id, string category) =>
        new ReportChangedElement { Id = id, UniqueId = "demo-uid-" + id, Category = category };

    public static ReportChangedElement Deleted(long id) => new ReportChangedElement { Id = id };

    public static ReportChanges Changes(bool complete, bool truncated, params ReportDocumentChanges[] documents) =>
        new ReportChanges { Complete = complete, Truncated = truncated, Documents = documents.ToList() };

    public static ReportDocumentChanges Group(string doc, IEnumerable<ReportChangedElement> added,
        IEnumerable<ReportChangedElement> modified, IEnumerable<ReportChangedElement> deleted) => new ReportDocumentChanges
    {
        Doc = doc,
        Added = (added ?? new ReportChangedElement[0]).ToList(),
        Modified = (modified ?? new ReportChangedElement[0]).ToList(),
        Deleted = (deleted ?? new ReportChangedElement[0]).ToList()
    };

    /// <summary>ISO UTC for a local time <paramref name="days"/> from <see cref="Now"/>.</summary>
    public static string Local(int days, int hour, int minute) =>
        new DateTimeOffset(Now.Date.AddDays(days).AddHours(hour).AddMinutes(minute)).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
}
