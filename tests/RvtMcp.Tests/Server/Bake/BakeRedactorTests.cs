using System;
using System.Diagnostics;
using System.Linq;
using RvtMcp.Plugin;
using RvtMcp.Server.Memory;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace RvtMcp.Tests
{
    public class BakeRedactorTests
    {
        [Fact]
        public void RedactForBake_RemovesPathsUrlsSecretsAndBimNames()
        {
            var input =
                @"Load C:\Users\Admin\Documents\Project Alpha.rvt from https://example.com/client/model " +
                @"with api key sk-testsecret12345 and password {""password"":""p@ssw0rd""}. " +
                @"Also open Door Type A.rfa with {""viewName"":""Level 01 - Coordination"",""sheetName"":""A101 - Floor Plan"",""familyName"":""Door Type A"",""projectName"":""Project A""}.";

            var redacted = BakeRedactor.RedactForBake(input);

            Assert.DoesNotContain(@"C:\Users\Admin\Documents\Project Alpha.rvt", redacted);
            Assert.DoesNotContain("Project Alpha", redacted);
            Assert.DoesNotContain("https://example.com/client/model", redacted);
            Assert.DoesNotContain("sk-testsecret12345", redacted);
            Assert.DoesNotContain(@"""password"":""p@ssw0rd""", redacted);
            Assert.DoesNotContain("Door Type A.rfa", redacted);
            Assert.DoesNotContain("Door Type A", redacted);
            Assert.DoesNotContain("Level 01 - Coordination", redacted);
            Assert.DoesNotContain("A101 - Floor Plan", redacted);
            Assert.DoesNotContain(@"""familyName"":""Door Type A""", redacted);
            Assert.DoesNotContain(@"""projectName"":""Project A""", redacted);
        }

        [Fact]
        public void RedactForBake_ReplacesEntireSpacedRevitFileNames()
        {
            var redacted = BakeRedactor.RedactForBake("Use This Is A Very Long Project A&B (Final)+MEP.rvt and This Is A Very Long Door Type A+B (Rated).rfa.");

            Assert.Equal("Use <project_file> and <family_file>.", redacted);
            Assert.DoesNotContain("This Is A Very Long Project", redacted);
            Assert.DoesNotContain("This Is A Very Long Door", redacted);
        }

        [Fact]
        public void RedactForBake_DoesNotConsumeNormalContextBeforeFileName()
        {
            var redacted = BakeRedactor.RedactForBake("The command opened Project A.rvt successfully.");

            Assert.Equal("The command opened <project_file> successfully.", redacted);
        }

        [Fact]
        public void RedactForBake_PreservesPoliteCommandBeforeStandaloneFileName()
        {
            var redacted = BakeRedactor.RedactForBake("Please open Project A.rvt before retry.");

            Assert.Equal("Please open <project_file> before retry.", redacted);
        }

        [Fact]
        public void RedactForBake_PreservesQuestionCommandBeforeStandaloneFileName()
        {
            var redacted = BakeRedactor.RedactForBake("Can you open Project A.rvt before retry?");

            Assert.Equal("Can you open <project_file> before retry?", redacted);
        }

        [Fact]
        public void RedactForBake_PreservesRevitExtensionNearMisses()
        {
            const string input = "Archive Project A.rvt.bak, Project B.rvt-backup, Family C.rfa.tmp, and Family D.rfa_draft before retry.";

            var redacted = BakeRedactor.RedactForBake(input);

            Assert.Equal(input, redacted);
        }

        [Fact]
        public void RedactForBake_RedactsSameExtensionListsSeparatedByPunctuation()
        {
            var redacted = BakeRedactor.RedactForBake("Use A.rvt,B.rvt,C.rvt and D.rfa;E.rfa!");

            Assert.Equal("Use <project_file>,<project_file>,<project_file> and <family_file>;<family_file>!", redacted);
        }

        [Fact]
        public void RedactForBake_PreservesSentenceContextBeforeStandaloneFileName()
        {
            var redacted = BakeRedactor.RedactForBake("Operation completed. Project A.rvt opened.");

            Assert.Equal("Operation completed. <project_file> opened.", redacted);
        }

        [Fact]
        public void RedactForBake_RedactsWhitespaceSeparatedSameExtensionFiles()
        {
            var redacted = BakeRedactor.RedactForBake("Open Alpha.rvt and Beta.rvt successfully. Files: DoorA.rfa DoorB.rfa");

            Assert.Equal("Open <project_file> and <project_file> successfully. Files: <family_file> <family_file>", redacted);
        }

        [Fact]
        public void RedactForBake_RedactsValidFileAfterNearMiss()
        {
            var redacted = BakeRedactor.RedactForBake("Archive Project A.rvt.bak Project B.rvt and Family A.rfa.tmp Family B.rfa");

            Assert.Equal("Archive Project A.rvt.bak <project_file> and Family A.rfa.tmp <family_file>", redacted);
        }

        [Fact]
        public void RedactForBake_LargeNearMissCompletesQuickly()
        {
            var input = string.Join(" ", Enumerable.Repeat("Project", 25000)) + ".rvx";

            var sw = Stopwatch.StartNew();
            var redacted = BakeRedactor.RedactForBake(input);
            sw.Stop();

            Assert.Equal(input, redacted);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Redaction took {sw.Elapsed.TotalMilliseconds:N0} ms.");
        }

        [Fact]
        public void RedactForBake_UsesStableTokensForRepeatedJsonNames()
        {
            var input = @"{""viewName"":""Level 01"",""other"":{""viewName"":""Level 01""},""sheetName"":""A101"",""result"":""Level 01""}";

            var redacted = BakeRedactor.RedactForBake(input, redactResultFields: true);

            Assert.Equal(
                @"{""viewName"":""<view_name_1>"",""other"":{""viewName"":""<view_name_1>""},""sheetName"":""<sheet_name_1>"",""result"":""<result_1>""}",
                redacted);
        }

        [Fact]
        public void RedactForBake_PreservesResultFieldByDefault()
        {
            var redacted = BakeRedactor.RedactForBake(@"{""result"":""OK"",""count"":3}");

            Assert.Equal(@"{""result"":""OK"",""count"":3}", redacted);
        }

        [Fact]
        public void RedactForBake_RedactsCommonBimNameFields()
        {
            var input = @"{""name"":""Executive Office"",""number"":""L1-101"",""level"":""Level 01"",""levelName"":""Level 01 - Coordination"",""sheetNumber"":""A101"",""department"":""Architecture"",""typeName"":""Door Type A"",""family"":""Door Type B"",""systemName"":""CHW Tenant A""}";

            var redacted = BakeRedactor.RedactForBake(input);

            Assert.DoesNotContain("Executive Office", redacted);
            Assert.DoesNotContain("L1-101", redacted);
            Assert.DoesNotContain("Level 01", redacted);
            Assert.DoesNotContain("Level 01 - Coordination", redacted);
            Assert.DoesNotContain("A101", redacted);
            Assert.DoesNotContain("Architecture", redacted);
            Assert.DoesNotContain("Door Type A", redacted);
            Assert.DoesNotContain("Door Type B", redacted);
            Assert.DoesNotContain("CHW Tenant A", redacted);
            Assert.Contains("<name_1>", redacted);
            Assert.Contains("<number_1>", redacted);
            Assert.Contains("<level_name_1>", redacted);
            Assert.Contains("<sheet_number_1>", redacted);
            Assert.Contains("<department_1>", redacted);
            Assert.Contains("<type_name_1>", redacted);
            Assert.Contains("<family_name_1>", redacted);
            Assert.Contains("<system_name_1>", redacted);
        }

        [Fact]
        public void RedactForBake_RedactsModelOverviewMarkdownContainer()
        {
            var input = JsonConvert.SerializeObject(new
            {
                markdown =
                    "## Project\n" +
                    "Name: Project Alpha\n" +
                    @"Path: C:\Users\Admin\Documents\Project Alpha.rvt" + "\n" +
                    "Active View: Level 01 - Coordination (FloorPlan)\n\n" +
                    "## Element Categories\n" +
                    "- Walls: 12\n\n" +
                    "## MEP Systems\n" +
                    "- Chilled Water Tenant A\n" +
                    "- CHW: Tenant A\n"
            });

            var redacted = BakeRedactor.RedactForBake(input);

            Assert.DoesNotContain("Project Alpha", redacted);
            Assert.DoesNotContain("Level 01 - Coordination", redacted);
            Assert.DoesNotContain("Chilled Water Tenant A", redacted);
            Assert.DoesNotContain("CHW: Tenant A", redacted);
            Assert.DoesNotContain(@"C:\Users\Admin\Documents", redacted);
            Assert.Contains("<project_name_1>", redacted);
            Assert.Contains("<view_name_1>", redacted);
            Assert.Contains("<system_name_1>", redacted);
            Assert.Contains("<system_name_2>", redacted);
            Assert.Contains("<project_file>", redacted);
            Assert.Contains("Walls", redacted);
        }

        [Fact]
        public void RedactForBake_RedactsMarkdownWhenJsonAlsoContainsSecrets()
        {
            var input = JsonConvert.SerializeObject(new
            {
                markdown =
                    "## Project\n" +
                    "Name: Project Alpha\n" +
                    "Active View: Level 01 - Coordination (FloorPlan)\n" +
                    "## MEP Systems\n" +
                    "- Chilled Water Tenant A\n",
                apiKey = "sk-testsecret12345"
            });

            var redacted = BakeRedactor.RedactForBake(input);

            Assert.DoesNotContain("Project Alpha", redacted);
            Assert.DoesNotContain("Level 01 - Coordination", redacted);
            Assert.DoesNotContain("Chilled Water Tenant A", redacted);
            Assert.DoesNotContain("sk-testsecret12345", redacted);
            Assert.Contains("<project_name_1>", redacted);
            Assert.Contains("<view_name_1>", redacted);
            Assert.Contains("<system_name_1>", redacted);
            Assert.Contains("[REDACTED]", redacted);
        }

        [Fact]
        public void RedactForBake_RepeatedNearMissExtensionsCompleteQuickly()
        {
            var input = string.Concat(Enumerable.Repeat("A.rvt.bak.", 10000));

            var sw = Stopwatch.StartNew();
            var redacted = BakeRedactor.RedactForBake(input);
            sw.Stop();

            Assert.Equal(input, redacted);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Redaction took {sw.Elapsed.TotalMilliseconds:N0} ms.");
        }

        [Fact]
        public void RedactForBake_RedactsDottedStandaloneRevitFileNames()
        {
            var redacted = BakeRedactor.RedactForBake("Open Project.Alpha.rvt and Door.TypeA.rfa.");

            Assert.Equal("Open <project_file> and <family_file>.", redacted);
        }

        [Fact]
        public void RedactForBake_ReplacesJsonEscapedWindowsAndUncPaths()
        {
            var input = @"{""local"":""C:\\Users\\Admin\\Documents\\Project A&B.rvt"",""unc"":""\\\\srv01\\share\\client\\Door Type A+B.rfa""}";

            var redacted = BakeRedactor.RedactForBake(input);

            Assert.DoesNotContain(@"C:\\Users\\Admin\\Documents", redacted);
            Assert.DoesNotContain(@"\\\\srv01\\share\\client", redacted);
            Assert.DoesNotContain("Project A&B", redacted);
            Assert.DoesNotContain("Door Type A+B", redacted);
            Assert.Contains("<project_file>", redacted);
            Assert.Contains("<family_file>", redacted);
        }

        [Fact]
        public void JournalEntryCreate_RedactsSendCodeParamsToHashAndLength()
        {
            const string codeBody = "return \"SensitiveWallType42\";";
            var entry = JournalEntry.Create(
                "send_code_to_revit",
                @"{""code"":""return \""SensitiveWallType42\"";"",""transactionMode"":""none""}",
                success: true,
                durationMs: 25);

            var metadata = JObject.Parse(entry.Params);

            Assert.Equal(BakeRedactor.HashBody(codeBody), (string)metadata["code_hash"]);
            Assert.Equal(codeBody.Length, (int)metadata["code_length"]);
            Assert.DoesNotContain("SensitiveWallType42", entry.Params);
            Assert.DoesNotContain("transactionMode", entry.Params);
        }

        [Fact]
        public void JournalEntryCreate_RedactsSendCodeResult()
        {
            var entry = JournalEntry.Create(
                "send_code_to_revit",
                @"{""code"":""return doc.PathName;""}",
                success: true,
                durationMs: 25,
                resultJson: @"{""path"":""C:\\Users\\Admin\\Documents\\Project A&B.rvt"",""viewName"":""Level 01""}");

            Assert.DoesNotContain("Project A&B", entry.Result);
            Assert.DoesNotContain(@"C:\\Users\\Admin\\Documents", entry.Result);
            Assert.DoesNotContain("Level 01", entry.Result);
            Assert.Contains("<project_file>", entry.Result);
        }

        [Fact]
        public void JournalEntryCreate_RedactsNonSendCodeParamsResultAndError()
        {
            var entry = JournalEntry.Create(
                "get_current_view_info",
                @"{""path"":""C:\\Users\\Admin\\Documents\\Project A&B.rvt"",""viewName"":""Level 01""}",
                success: false,
                durationMs: 25,
                error: @"Could not open C:\Users\Admin\Documents\Project A&B.rvt from https://example.com/client?token=sk-testsecret12345",
                resultJson: @"{""family"":""Door Type A+B.rfa"",""sheetName"":""A101 - Floor Plan""}");

            var serialized = JsonConvert.SerializeObject(entry);

            Assert.DoesNotContain("Project A&B", serialized);
            Assert.DoesNotContain(@"C:\\Users\\Admin\\Documents", serialized);
            Assert.DoesNotContain(@"C:\Users\Admin\Documents", serialized);
            Assert.DoesNotContain("Level 01", serialized);
            Assert.DoesNotContain("Door Type A+B", serialized);
            Assert.DoesNotContain("A101 - Floor Plan", serialized);
            Assert.DoesNotContain("https://example.com", serialized);
            Assert.DoesNotContain("sk-testsecret12345", serialized);
            Assert.Contains("<project_file>", serialized);
            Assert.Contains("<family_file>", serialized);
        }
    }
}
