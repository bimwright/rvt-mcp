using System;
using System.IO;
using System.Threading;
using RvtMcp.Plugin;
using Xunit;

namespace RvtMcp.Tests
{
    public class LogFileLockTests
    {
        [Fact]
        public void TimeoutDoesNotExecuteUnlockedCallbackAndLockCanBeReused()
        {
            var path = Path.Combine(Path.GetTempPath(), "rvt-lock-" + Guid.NewGuid().ToString("N"));
            Exception failure = null;
            bool called = false;
            McpLogger.WithFileLock(path, () =>
            {
                var contender = new Thread(() =>
                {
                    try { McpLogger.WithFileLock(path, () => { called = true; return true; }, 50); }
                    catch (Exception ex) { failure = ex; }
                });
                contender.Start();
                Assert.True(contender.Join(TimeSpan.FromSeconds(10)));
                return true;
            });

            Assert.IsType<TimeoutException>(failure);
            Assert.False(called);
            Assert.True(McpLogger.WithFileLock(path, () => true));
        }

        [Fact]
        public void ThrowingCallbackReleasesLockForAnotherThread()
        {
            var path = Path.Combine(Path.GetTempPath(), "rvt-lock-" + Guid.NewGuid().ToString("N"));
            Assert.Throws<InvalidOperationException>(() => McpLogger.WithFileLock<bool>(path,
                () => throw new InvalidOperationException("test")));
            Exception failure = null;
            var contender = new Thread(() =>
            {
                try { McpLogger.WithFileLock(path, () => true, 100); }
                catch (Exception ex) { failure = ex; }
            });
            contender.Start();
            Assert.True(contender.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(failure);
        }

        [Fact]
        public void DifferentDirectoriesDoNotShareLockEvenWithSameFilename()
        {
            var dir = Path.Combine(Path.GetTempPath(), "rvt-lock-" + Guid.NewGuid().ToString("N"));
            Exception failure = null;
            McpLogger.WithFileLock(Path.Combine(dir, "a", "mcp-calls.jsonl"), () =>
            {
                var contender = new Thread(() =>
                {
                    try { McpLogger.WithFileLock(Path.Combine(dir, "b", "mcp-calls.jsonl"), () => true, 100); }
                    catch (Exception ex) { failure = ex; }
                });
                contender.Start();
                Assert.True(contender.Join(TimeSpan.FromSeconds(10)));
                return true;
            });
            Assert.Null(failure);
        }
    }
}
