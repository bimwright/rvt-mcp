using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using RvtMcp.Server.Bake;
using Xunit;

namespace RvtMcp.Tests
{
    public class CodeModuleCrashTests
    {
        [Theory]
        [InlineData("after_revision")]
        [InlineData("after_head")]
        public async Task Killing_the_actual_store_writer_rolls_back_the_entire_transaction(string checkpoint)
        {
            var root=Path.Combine(Path.GetTempPath(),"rvt-module-crash-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); Process child=null, writer=null; Task writerExited=null;
            try
            {
                var path=Path.Combine(root,"bake.db"); using var db=new BakeDb(path); db.Migrate(); var store=new CodeModuleStore(db);
                var original=await store.SaveAsync("root","class Original {}","",null,null,CodeModuleStoreTests.Compile);
                var marker=Path.Combine(root,"checkpoint.txt");
                var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet") {
                    UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden,
                    RedirectStandardOutput=true, RedirectStandardError=true };
                foreach(var argument in new[]{"vstest",Assembly.GetExecutingAssembly().Location,
                    "/TestCaseFilter:FullyQualifiedName=RvtMcp.Tests.CodeModuleCrashTests.Child_transaction_writer",
                    "/ResultsDirectory:"+Path.Combine(root,"child-results")}) start.ArgumentList.Add(argument);
                start.Environment["RVT_MCP_MODULE_CRASH_DB"]=path;
                start.Environment["RVT_MCP_MODULE_CRASH_MARKER"]=marker;
                start.Environment["RVT_MCP_MODULE_CRASH_POINT"]=checkpoint;
                child=Process.Start(start)!;
                var stdout=child.StandardOutput.ReadToEndAsync(); var stderr=child.StandardError.ReadToEndAsync();
                var timer=Stopwatch.StartNew();
                while(!File.Exists(marker)&&!child.HasExited&&timer.Elapsed<TimeSpan.FromSeconds(30)) await Task.Delay(50);
                if(!File.Exists(marker))
                    throw new Exception("Child did not reach the transaction checkpoint. "+(child.HasExited ? await stdout+await stderr : "Timed out."));
                // vstest is a launcher; register the actual writer's exit while it is still alive.
                writer=Process.GetProcessById(int.Parse(File.ReadAllText(marker+".pid")));
                writerExited=writer.WaitForExitAsync();
                Assert.False(writerExited.IsCompleted);
                // A concurrent reader still sees the committed state while the child holds its write transaction.
                Assert.Equal(original.Record.Revision.Hash,store.Resolve(new[]{"root"}).Modules[0].Hash);
                child.Kill(entireProcessTree:true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await writerExited.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(original.Record.Revision.Hash,store.Resolve(new[]{"root"}).Modules[0].Hash);
                using var c=ScriptBaselineFixture.Open(path); using var q=c.CreateCommand(); q.CommandText="SELECT COUNT(*) FROM code_module_revisions";
                Assert.Equal(1L,q.ExecuteScalar());
            }
            finally
            {
                if(child!=null){if(!child.HasExited){child.Kill(entireProcessTree:true);await child.WaitForExitAsync();}child.Dispose();}
                if(writerExited!=null) await writerExited.WaitAsync(TimeSpan.FromSeconds(10));
                writer?.Dispose();
                Directory.Delete(root,true);
            }
        }

        [Fact]
        public async Task Child_transaction_writer()
        {
            var path=Environment.GetEnvironmentVariable("RVT_MCP_MODULE_CRASH_DB");
            if(path==null) return; // Parent tests invoke this method in a dedicated, killable test-host process.
            File.WriteAllText(Environment.GetEnvironmentVariable("RVT_MCP_MODULE_CRASH_MARKER")!+".pid",Environment.ProcessId.ToString());
            using var db=new BakeDb(path); var store=new CodeModuleStore(db);
            var old=store.Resolve(new[]{"root"}).Modules[0];
            store.WriteCheckpointForTests=point=>{
                if(point!=Environment.GetEnvironmentVariable("RVT_MCP_MODULE_CRASH_POINT"))return;
                File.WriteAllText(Environment.GetEnvironmentVariable("RVT_MCP_MODULE_CRASH_MARKER")!,point);
                Thread.Sleep(Timeout.Infinite);
            };
            await store.SaveAsync("root","class Changed {}","",null,old.Hash,CodeModuleStoreTests.Compile);
            throw new Exception("Parent did not interrupt the child transaction.");
        }
    }
}
