using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using RvtMcp.Failures;
using RvtMcp.Server;
using Xunit;

namespace RvtMcp.Tests
{
    public class DialogAnswerTests
    {
        internal static RevitMessageCatalogData Catalog() => new RevitMessageCatalogData { RevitYear = 2024,
            TaskDialogs = new List<TaskDialogEntry> { new TaskDialogEntry { Id = "TaskDialog_Test_Answer", CommonButtons = "Close, Cancel",
                Buttons = new List<CatalogButton> { new CatalogButton { Id = "1002", Text = "Synthetic choice" } } } } };
        internal static JArray Answer(string button = "Close") => new JArray(new JObject { ["code"] = "TaskDialog_Test_Answer", ["button"] = button });

        private static JArray Approved([CallerFilePath] string file = "") => JArray.Parse(File.ReadAllText(
            Path.Combine(Path.GetDirectoryName(file), "..", "..", "Golden", "dialog-answer-policy.json")));

        public static IEnumerable<object[]> DeniedPairs() => Approved().OfType<JObject>().Where(x => x.Value<string>("id")[0] != 'A')
            .Select(x => new object[] { x.Value<string>("code"), x.Value<string>("button") });

        [Theory, MemberData(nameof(DeniedPairs))]
        public void Approved_denied_pair_is_rejected_on_server_and_plugin(string code, string button)
        {
            var entry = new TaskDialogEntry { Id = code, CommonButtons = "Cancel" };
            if (int.TryParse(button, out _)) entry.Buttons.Add(new CatalogButton { Id = button, Text = "Synthetic denied choice" });
            else entry.CommonButtons += ", " + button;
            var catalog = new RevitMessageCatalogData { TaskDialogs = new List<TaskDialogEntry> { entry } };
            var pairs = new JArray(new JObject { ["code"] = code, ["button"] = button });
            var error = Assert.Throws<ArgumentException>(() => DialogAnswerPolicy.Validate(pairs, catalog));
            Assert.Contains("Allowed buttons: Cancel", error.Message);
            Assert.Null(DialogAnswerPolicy.Select(pairs, catalog, code, false, false, false));
            pairs[0]["button"] = "Cancel";
            Assert.Single(DialogAnswerPolicy.Validate(pairs, catalog));
        }

        [Fact]
        public void Approved_exceptions_remain_declarable_and_policy_rows_are_unique()
        {
            var rows = Approved().OfType<JObject>().ToList();
            Assert.Equal(145, rows.Count);
            Assert.Equal(145, rows.Select(x => x["code"] + "|" + x["button"]).Distinct().Count());
            foreach (var row in rows.Where(x => x.Value<string>("id").StartsWith("A")))
            {
                var button = row.Value<string>("button");
                var entry = new TaskDialogEntry { Id = row.Value<string>("code"), CommonButtons = int.TryParse(button, out _) ? "None" : button };
                if (int.TryParse(button, out _)) entry.Buttons.Add(new CatalogButton { Id = button, Text = "Synthetic allowed choice" });
                var catalog = new RevitMessageCatalogData { TaskDialogs = new List<TaskDialogEntry> { entry } };
                Assert.Single(DialogAnswerPolicy.Validate(new JArray(row.DeepClone()), catalog));
            }
        }

        [Fact]
        public void Validation_normalizes_catalog_names_and_rejects_bad_batches()
        {
            var input = Answer("close"); input[0]["code"] = " Test_Answer ";
            Assert.Equal("Close", DialogAnswerPolicy.Validate(input, Catalog())[0].Value<string>("button"));
            Assert.Equal("Synthetic choice", DialogAnswerPolicy.Validate(Answer("1002"), Catalog())[0].Value<string>("text"));
            Assert.Throws<ArgumentException>(() => DialogAnswerPolicy.Validate(Answer("8"), Catalog()));
            Assert.Throws<ArgumentException>(() => DialogAnswerPolicy.Validate(Answer("CommandLink2"), Catalog()));
            input = Answer(); input.Add(Answer()[0]);
            Assert.Throws<ArgumentException>(() => DialogAnswerPolicy.Validate(input, Catalog()));
            input = Answer(); input.Add(new JObject { ["code"] = "TaskDialog_Unknown", ["button"] = "Close" });
            Assert.Throws<ArgumentException>(() => DialogAnswerPolicy.Validate(input, Catalog()));
            Assert.Throws<ArgumentException>(() => DialogAnswerPolicy.Validate(new JArray(), Catalog()));
            Assert.Throws<ArgumentException>(() => DialogAnswerPolicy.Validate(new JArray(Enumerable.Range(0, 65).Select(_ => Answer()[0])), Catalog()));
            Assert.Throws<ArgumentException>(() => DialogAnswerPolicy.Validate(Answer(), null));
            var native = new RevitMessageCatalogData { TaskDialogs = new List<TaskDialogEntry> { new TaskDialogEntry { Id = "Dialog_Revit_DocWarnDialog", CommonButtons = "Close" } } };
            input = Answer(); input[0]["code"] = "Dialog_Revit_DocWarnDialog";
            Assert.Null(DialogAnswerPolicy.Select(input, native, "Dialog_Revit_DocWarnDialog", false, false, false));
        }

        [Fact]
        public void User_choice_stop_and_storage_failure_prevent_agent_answer()
        {
            foreach (var flags in new[] { (true, false, false), (false, true, false), (false, false, true) })
                Assert.Null(DialogAnswerPolicy.Select(Answer(), Catalog(), "TaskDialog_Test_Answer", flags.Item1, flags.Item2, flags.Item3));
            Assert.NotNull(DialogAnswerPolicy.Select(Answer(), Catalog(), "TaskDialog_Test_Answer", false, false, false));
        }

        [Fact]
        public void Authorization_precedes_native_call_and_native_failure_has_an_outcome_receipt()
        {
            var events = new List<string>();
            Assert.False(DialogAnswerPolicy.ApplyRecorded(() => { events.Add("authorization_failed"); return false; },
                () => throw new Exception("must not apply"), _ => throw new Exception("must not record result")));
            Assert.Equal(new[] { "authorization_failed" }, events);
            foreach (var outcome in new[] { "true", "false", "throw" })
            {
                events.Clear();
                var applied = DialogAnswerPolicy.ApplyRecorded(() => { events.Add("authorized"); return true; },
                    () => { events.Add("native"); if (outcome == "throw") throw new Exception(); return outcome == "true"; },
                    result => events.Add("receipt:" + result));
                Assert.Equal(outcome == "true", applied);
                Assert.Equal(new[] { "authorized", "native", "receipt:" + applied }, events);
            }
        }

        [Fact]
        public void Next_and_session_are_bound_to_target_generation_and_writes()
        {
            var session = new DialogAnswerSession();
            session.Set("a", 1, "next", Answer());
            Assert.Null(session.ForWrite("a", 1, false));
            Assert.NotNull(session.ForWrite("a", 1, true)); // observing or a pre-send rejection does not consume
            session.Sending(); Assert.Null(session.ForWrite("a", 1, true));
            session.Set("a", 1, "session", Answer()); session.Sending();
            Assert.NotNull(session.ForWrite("a", 1, true));
            Assert.Null(session.ForWrite("a", 2, true)); // same PID/id, new process/binding generation
            session.Set("a", 2, "session", Answer());
            Assert.Null(session.ForWrite("b", 3, false)); Assert.Null(session.ForWrite("a", 2, true));
            session.Set("a", 2, "session", Answer()); session.Clear(); Assert.Null(session.ForWrite("a", 2, true));
        }

        [Fact]
        public void Summary_counts_agent_rule_and_user_occurrences_without_overwriting_answers()
        {
            JObject Choice(string by, string button = "Close") => new JObject { ["button"] = button, ["text"] = "Synthetic choice", ["by"] = by };
            var group = new JObject { ["count"] = 3 };
            FailureCapture.DescribeAnswers(group, new[] { Choice("rule"), Choice("agent") });
            Assert.Equal("mixed", group.Value<string>("answeredBy"));
            foreach (var by in new[] { "rule", "agent", "user" }) Assert.Equal(1, group["answeredCount"].Value<int>(by));
            Assert.Null(group["answer"]);
            group = new JObject { ["count"] = 2 };
            FailureCapture.DescribeAnswers(group, new[] { Choice("agent"), Choice("agent") });
            Assert.Equal("agent", group.Value<string>("answeredBy")); Assert.Equal("Close", group["answer"].Value<string>("button"));
            group = new JObject { ["count"] = 2 };
            FailureCapture.DescribeAnswers(group, new[] { Choice("rule"), Choice("agent", "1002") });
            Assert.Equal("mixed", group.Value<string>("answeredBy")); Assert.Null(group["answer"]);
            group = new JObject { ["count"] = 2 };
            FailureCapture.DescribeAnswers(group, new[] { new JObject { ["button"] = "1002", ["meaning"] = "Synthetic rule", ["by"] = "rule" }, Choice("agent", "1002") });
            Assert.Equal("mixed", group.Value<string>("answeredBy"));
            Assert.Equal("1002", group["answer"].Value<string>("button"));

        }
    }
}
