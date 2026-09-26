using System;
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

        [Fact]
        public void NormalizePath_leaves_scheme_paths_alone()
        {
            // RSN:// and other non-filesystem strings must not be glued onto
            // the current directory by GetFullPath.
            var normalized = OpenModelLocalPolicy.NormalizePath("RSN://server/RevitServerModel.rvt");
            Assert.StartsWith("RSN:", normalized);
            Assert.DoesNotContain(Environment.CurrentDirectory, normalized);
        }
    }
}
