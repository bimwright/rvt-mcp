using System;
using System.IO;
using System.Runtime.CompilerServices;
using RvtMcp.Plugin.Handlers;
using Xunit;
using static RvtMcp.Plugin.Handlers.OpenModelLocalPolicy;

namespace RvtMcp.Tests
{
    /// <summary>
    /// The local-file protection rules behind revit_open_model's Create New
    /// Local flow (design doc §2.5). These run without Revit because the
    /// policy is extracted into OpenModelLocalPolicy.
    /// </summary>
    public class OpenModelLocalPolicyTests
    {
        [Fact]
        public void Decide_creates_local_when_target_is_free()
        {
            Assert.Equal(CollisionDecision.CreateLocal,
                OpenModelLocalPolicy.Decide(targetExists: false, targetOpenInSession: false,
                    headerRead: false, isWorkshared: false, allLocalChangesSavedToCentral: false));
        }

        [Fact]
        public void Decide_stops_when_target_is_open_in_session_even_if_header_proves_synced()
        {
            // A document or loaded link at the target path is never touched.
            Assert.Equal(CollisionDecision.StopOpenInSession,
                OpenModelLocalPolicy.Decide(targetExists: true, targetOpenInSession: true,
                    headerRead: true, isWorkshared: true, allLocalChangesSavedToCentral: true));
        }

        [Fact]
        public void Decide_stops_when_old_local_header_is_unreadable()
        {
            // Unverified sync state is treated as protected, not movable.
            Assert.Equal(CollisionDecision.StopUnverifiable,
                OpenModelLocalPolicy.Decide(targetExists: true, targetOpenInSession: false,
                    headerRead: false, isWorkshared: false, allLocalChangesSavedToCentral: true));
        }

        [Fact]
        public void Decide_stops_when_old_local_has_unsynced_changes()
        {
            Assert.Equal(CollisionDecision.StopUnsynced,
                OpenModelLocalPolicy.Decide(targetExists: true, targetOpenInSession: false,
                    headerRead: true, isWorkshared: true, allLocalChangesSavedToCentral: false));
        }

        [Fact]
        public void Decide_renames_a_synced_workshared_local()
        {
            Assert.Equal(CollisionDecision.RenameAndCreate,
                OpenModelLocalPolicy.Decide(targetExists: true, targetOpenInSession: false,
                    headerRead: true, isWorkshared: true, allLocalChangesSavedToCentral: true));
        }

        [Fact]
        public void Decide_renames_a_non_workshared_file_at_the_target()
        {
            Assert.Equal(CollisionDecision.RenameAndCreate,
                OpenModelLocalPolicy.Decide(targetExists: true, targetOpenInSession: false,
                    headerRead: true, isWorkshared: false, allLocalChangesSavedToCentral: false));
        }

        [Fact]
        public void PathsEqual_matches_same_file_spelled_differently()
        {
            Assert.True(OpenModelLocalPolicy.PathsEqual(@"C:\A\Model.rvt", @"c:\a\MODEL.rvt"));
            Assert.True(OpenModelLocalPolicy.PathsEqual(@"C:/A/Model.rvt", @"C:\A\Model.rvt"));
            Assert.True(OpenModelLocalPolicy.PathsEqual(@"C:\A\x\..\Model.rvt", @"C:\A\Model.rvt"));
            Assert.True(OpenModelLocalPolicy.PathsEqual(@"C:\A\Model.rvt\", @"C:\A\Model.rvt"));
        }

        [Fact]
        public void PathsEqual_distinguishes_different_files()
        {
            Assert.False(OpenModelLocalPolicy.PathsEqual(@"C:\A\Model.rvt", @"C:\B\Model.rvt"));
            Assert.False(OpenModelLocalPolicy.PathsEqual(@"C:\A\Model.rvt", null));
            Assert.False(OpenModelLocalPolicy.PathsEqual("", @"C:\A\Model.rvt"));
        }

        // The two refusal guards in OpenModelHandler (unreadable input header,
        // missing Revit.ini ProjectPath) live inside Execute where no test can
        // reach them; the handler routes through these decisions, so reverting
        // a guard means breaking a decision these tests pin (§2.1, §4.2).

        [Fact]
        public void DecideInput_refuses_when_header_is_unreadable()
        {
            Assert.Equal(InputDecision.RefuseUnclassified,
                OpenModelLocalPolicy.DecideInput(headerRead: false, isWorkshared: false, worksets: null));
            // Even a valid worksets argument cannot rescue an unclassified file.
            Assert.Equal(InputDecision.RefuseUnclassified,
                OpenModelLocalPolicy.DecideInput(headerRead: false, isWorkshared: false, worksets: "all"));
        }

        [Fact]
        public void DecideInput_validates_worksets_against_the_header()
        {
            Assert.Equal(InputDecision.RefuseBadWorksets,
                OpenModelLocalPolicy.DecideInput(true, isWorkshared: true, worksets: "some"));
            Assert.Equal(InputDecision.RefuseWorksetsOnPlainFile,
                OpenModelLocalPolicy.DecideInput(true, isWorkshared: false, worksets: "all"));
            Assert.Equal(InputDecision.Proceed,
                OpenModelLocalPolicy.DecideInput(true, isWorkshared: true, worksets: "LastViewed"));
            Assert.Equal(InputDecision.Proceed,
                OpenModelLocalPolicy.DecideInput(true, isWorkshared: false, worksets: null));
        }

        [Fact]
        public void ParseProjectPath_finds_the_configured_folder()
        {
            Assert.Equal(@"D:\Workspace\RevitLocalProjects",
                OpenModelLocalPolicy.ParseProjectPath(new[] { "ProjectPath=D:\\Workspace\\RevitLocalProjects" }));
            // Quoted values and the ";"-separated form Revit writes both parse.
            Assert.Equal(@"D:\My Dir",
                OpenModelLocalPolicy.ParseProjectPath(new[] { "  projectpath= \"D:\\My Dir\" ; D:\\Other" }));
        }

        [Fact]
        public void ParseProjectPath_returns_null_when_the_key_is_absent_or_empty()
        {
            Assert.Null(OpenModelLocalPolicy.ParseProjectPath(new[] { "[Directories]", "SomeOther=D:\\X" }));
            Assert.Null(OpenModelLocalPolicy.ParseProjectPath(new[] { "ProjectPath=" }));
            Assert.Null(OpenModelLocalPolicy.ParseProjectPath(new[] { "ProjectPath   " }));
            Assert.Null(OpenModelLocalPolicy.ParseProjectPath(null));
        }

        [Fact]
        public void DecideLocalDir_never_falls_back_to_another_folder()
        {
            Assert.Equal(LocalDirDecision.StopMissing, OpenModelLocalPolicy.DecideLocalDir(null));
            Assert.Equal(LocalDirDecision.StopMissing, OpenModelLocalPolicy.DecideLocalDir("  "));
            Assert.Equal(LocalDirDecision.UseConfigured, OpenModelLocalPolicy.DecideLocalDir(@"D:\Workspace\RevitLocalProjects"));
        }

        [Fact]
        public void NormalizePath_leaves_scheme_paths_alone()
        {
            // RSN:// and other non-filesystem strings must not be glued onto
            // the current directory by GetFullPath.
            var normalized = OpenModelLocalPolicy.NormalizePath("RSN://server/RevitServerModel.rvt");
            Assert.StartsWith("RSN:", normalized);
            Assert.DoesNotContain(Environment.CurrentDirectory, normalized);
        }

        // The refusal guards sit inside Execute() where tests cannot reach them;
        // this pins that the handler actually routes through the tested policy
        // instead of drifting back to ad-hoc fallthrough logic.
        [Fact]
        public void Handler_routes_both_refusal_guards_through_the_tested_policy()
        {
            var repoRoot = GetRepoRoot();
            var source = File.ReadAllText(Path.Combine(repoRoot, "src", "shared", "Handlers", "OpenModelHandler.cs"));

            Assert.Contains("OpenModelLocalPolicy.DecideInput", source);
            Assert.Contains("InputDecision.RefuseUnclassified", source);
            Assert.Contains("OpenModelLocalPolicy.DecideLocalDir", source);
            Assert.Contains("LocalDirDecision.StopMissing", source);
            // No SpecialFolder-based fallback (Documents/Personal/…) - §4.2 is ProjectPath only.
            Assert.DoesNotContain("SpecialFolder", source);
        }

        private static string GetRepoRoot([CallerFilePath] string testFile = "")
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
        }
    }
}
