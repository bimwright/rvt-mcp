using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using RvtMcp.Plugin.Localization;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            InitializeLanguage();
            if (args.Length == 1 && args[0] == "--demo") return Snapshots.Demo();
            if (args.Length == 2 && args[0] == "--snapshot") return Snapshots.Save(args[1]);
            if (args.Length == 2 && args[0] == "--check") return RunChecks(args[1]);
            if (args.Length != 0)
            {
                Console.Error.WriteLine("usage: [--demo | --snapshot <folder> | --check <number>]");
                return 2;
            }
            return RunChecks();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    /// <summary>English catalog with real per-locale fallbacks, built inline so language switches are synchronous.</summary>
    private static void InitializeLanguage()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var english = EmbeddedCatalog.Load(assembly, "en");
        L.ForceSyncBuilds = true;
        L.Initialize("English", "en", locale => StringTable.Build(locale, english, EmbeddedCatalog.Load(assembly, locale), null), null);
    }

    private static int RunChecks(string number = null)
    {
        var checks = new List<KeyValuePair<string, Action>>
        {
            Check("1 Wide layout", ReportsChecks.Wide),
            Check("2 Mode switch", ReportsChecks.ModeSwitch),
            Check("3 Reopen and placement", ReportsChecks.Reopen),
            Check("4 Maximize", ReportsChecks.Maximize),
            Check("5 Markdown", ContentChecks.Markdown),
            Check("6 Links", ContentChecks.Links),
            Check("7 Actual changes", ContentChecks.Changes),
            Check("8 Reload", ContentChecks.Reload),
            Check("9 ShowReport", ContentChecks.ShowReport),
            Check("10 Delete and copy", ContentChecks.DeleteAndCopy),
            Check("11 Status tones", ReportsChecks.StatusTones),
            Check("12 Empty", ReportsChecks.Empty),
            Check("13 Language", ReportsChecks.Language),
            Check("14 Mouse wheel", ContentChecks.Wheel),
            Check("15 Keyboard", ReportsChecks.Keyboard),
            Check("16 Performance", ContentChecks.Performance),
            Check("17 Lifecycle", ReportsChecks.Lifecycle),
            Check("19 Text size", ReportsChecks.TextSize),
            Check("20 Link focus preserves reading position", ReportReadingChecks.LinkFocus),
            Check("21 Deferred report tables", ReportReadingChecks.DeferredTables),
            Check("22 Compact day groups", ReportFeedChecks.Groups),
            Check("23 Compact reading on new reports", ReportFeedChecks.Reading),
            Check("24 Compact updated report", ReportFeedChecks.Updated),
            Check("25 Compact virtualized feed", ReportFeedChecks.VirtualizedFeed),
            Check("26 Reports owner and transport", ReportFinishChecks.HostTitle),
            Check("27 Copy feedback", ReportFinishChecks.CopyFeedback),
            Check("28 Reduced motion", ReportFinishChecks.Motion),
            Check("29 Per-mode reading positions", ReportFinishChecks.ModeReading),
            Check("30 Exact History evidence", ReportHistoryChecks.History),
        };
        if (number != null)
        {
            checks = checks.Where(c => c.Key.StartsWith(number + " ", StringComparison.Ordinal)).ToList();
            if (checks.Count == 0) throw new ArgumentException("Unknown check: " + number);
        }
        var failed = new List<string>();
        using (var bindingErrors = new WpfBindingErrors())
        {
            foreach (var check in checks)
            {
                try
                {
                    check.Value();
                    Console.WriteLine("PASS " + check.Key);
                }
                catch (Exception ex)
                {
                    failed.Add(check.Key);
                    Console.WriteLine("FAIL " + check.Key + ": " + ex.Message);
                    if (!(ex.Message ?? "").StartsWith("FAIL", StringComparison.Ordinal)) Console.WriteLine(ex);
                }
            }
            try
            {
                bindingErrors.AssertClean();
                Console.WriteLine("PASS 18 No WPF binding errors after every scenario");
            }
            catch (Exception ex)
            {
                failed.Add("18 Binding");
                Console.WriteLine("FAIL 18 Binding: " + ex.Message);
            }
        }
        Console.WriteLine(failed.Count == 0 ? "PASS: all Reports window checks" : "FAILED: " + string.Join(", ", failed));
        return failed.Count == 0 ? 0 : 1;
    }

    private static KeyValuePair<string, Action> Check(string name, Action action) => new KeyValuePair<string, Action>(name, action);
}
