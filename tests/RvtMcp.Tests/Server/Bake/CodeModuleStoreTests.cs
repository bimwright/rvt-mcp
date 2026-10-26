using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RvtMcp.Scripting;
using RvtMcp.Server.Bake;
using Xunit;

namespace RvtMcp.Tests
{
    public class CodeModuleStoreTests : IDisposable
    {
        protected readonly string Root = Path.Combine(Path.GetTempPath(), "rvt-module-store-" + Guid.NewGuid().ToString("N"));
        internal string DbPath => Path.Combine(Root, "bake.db");
        internal readonly BakeDb Db;
        internal readonly CodeModuleStore Store;
        public CodeModuleStoreTests() { Directory.CreateDirectory(Root); Db = new BakeDb(DbPath); Db.Migrate(); Store = new CodeModuleStore(Db); }
        public void Dispose() { Db.Dispose(); Directory.Delete(Root, true); }
        internal static Task<CodeModuleCompilation> Compile(CodeModuleSnapshot snapshot, CancellationToken _) =>
            Task.FromResult(new CodeModuleCompilation(2024, "offline-synthetic-api", new[] {"public static int Value()"}));
        internal Task<CodeModuleSaveResult> Save(string name, string code = null, string[] requires = null, string expected = null, string description = "") =>
            Store.SaveAsync(name, code ?? "public class " + name + " {}", description, requires, expected, Compile);
        internal long Scalar(string sql)
        {
            using var connection = ScriptBaselineFixture.Open(DbPath); using var command = connection.CreateCommand(); command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }
        internal void Sql(string sql)
        {
            using var connection = ScriptBaselineFixture.Open(DbPath); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
        }

        [Fact]
        public async Task Save_normalizes_source_and_noop_keeps_the_original_head_metadata()
        {
            var first = await Save("helper", "public class Helper {}\r\n", description: "Initial");
            Assert.True(first.Changed); Assert.Equal("public class Helper {}\n", first.Record.Revision.Code);
            var second = await Save("helper", "public class Helper {}\n", expected: first.Record.Revision.Hash, description: "Initial");
            Assert.False(second.Changed); Assert.Equal(first.Record.UpdatedAt, second.Record.UpdatedAt);
            Assert.Equal(first.Record.CompiledHostJson, second.Record.CompiledHostJson); Assert.Equal(1, Scalar("SELECT COUNT(*) FROM code_module_revisions"));
            var description = await Save("helper", "public class Helper {}\n", expected: first.Record.Revision.Hash, description: "Changed");
            Assert.True(description.Changed); Assert.Equal(first.Record.Revision.Hash, description.Record.Revision.Hash);
            var host = JObject.Parse(first.Record.CompiledHostJson);
            Assert.Equal(2024, host.Value<int>("host_year")); Assert.True(host.Value<bool>("compile_verified"));
            Assert.False(host.Value<bool>("native_verified")); Assert.False(host.Value<bool>("runtime_observed"));
        }

        [Fact]
        public async Task Save_checks_CAS_before_compile_and_rechecks_after_the_compile_wait()
        {
            var old = await Save("helper"); var calls = 0;
            Task<CodeModuleCompilation> Validate(CodeModuleSnapshot s, CancellationToken c) { calls++; return Compile(s,c); }
            var error = await Assert.ThrowsAsync<ScriptContractException>(() => Store.SaveAsync("helper", "class Changed {}", "", null, null, Validate));
            Assert.Equal(ScriptErrorCodes.ModuleConflict, error.ErrorCode); Assert.Equal(0, calls);
            error = await Assert.ThrowsAsync<ScriptContractException>(() => Store.SaveAsync("helper", "class Outer {}", "", null, old.Record.Revision.Hash,
                async (snapshot, cancellation) => { await Save("helper", "class Winner {}", expected: old.Record.Revision.Hash); return await Compile(snapshot, cancellation); }));
            Assert.Equal(ScriptErrorCodes.ModuleConflict, error.ErrorCode);
            Assert.Equal("class Winner {}", Assert.Single(Store.List()).Revision.Code);
            Assert.Equal(2, Scalar("SELECT COUNT(*) FROM code_module_revisions"));
        }

        [Fact]
        public async Task Dependency_edit_during_compile_proves_no_transaction_is_held_and_refuses_stale_commit()
        {
            var dependency = await Save("dependency"); var compileEntered = false;
            var error = await Assert.ThrowsAsync<ScriptContractException>(() => Store.SaveAsync("root", "class Root {}", "", new[] {"dependency"}, null,
                async (snapshot, cancellation) => {
                    compileEntered = true;
                    Assert.Equal(dependency.Record.Revision.Hash, snapshot.Modules[0].Hash);
                    await Save("dependency", "class Changed {}", expected: dependency.Record.Revision.Hash);
                    Assert.Equal(dependency.Record.Revision.Hash, snapshot.Modules[0].Hash);
                    return await Compile(snapshot, cancellation);
                }));
            Assert.True(compileEntered); Assert.Equal(ScriptErrorCodes.ModuleConflict, error.ErrorCode);
            Assert.Single(Store.List()); Assert.Equal(2, Scalar("SELECT COUNT(*) FROM code_module_revisions"));
        }

        [Fact]
        public async Task Native_compile_failure_and_cancellation_leave_no_revision_or_head()
        {
            var failure = await Assert.ThrowsAsync<ScriptContractException>(() => Store.SaveAsync("root", "class Root {}", "", null, null,
                (_,__) => throw new ScriptContractException(ScriptErrorCodes.CompileFailed, "Invalid helper")));
            Assert.Equal(ScriptErrorCodes.CompileFailed, failure.ErrorCode);
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.SaveAsync("root", "class Root {}", "", null, null,
                (snapshot, token) => { cancellation.Cancel(); return Compile(snapshot, token); }, cancellation.Token));
            Assert.Empty(Store.List()); Assert.Equal(0, Scalar("SELECT COUNT(*) FROM code_module_revisions"));
        }

        [Fact]
        public async Task Ordered_diamond_snapshot_is_immutable_and_pinned_manifest_rejects_a_later_edit()
        {
            var leaf = await Save("leaf"); await Save("left", requires: new[] {"leaf"}); await Save("right", requires: new[] {"leaf"});
            var root = await Save("root", requires: new[] {"right", "left"});
            var snapshot = Store.Resolve(new[] {"root"}); Assert.Equal(new[] {"leaf", "right", "left", "root"}, snapshot.Modules.Select(m => m.Name));
            var pin = snapshot.Modules.Select(m => new ScriptModuleManifestEntry(m.Name, m.Hash, m.Requires)).ToArray();
            await Save("leaf", "class Edited {}", expected: leaf.Record.Revision.Hash);
            Assert.Equal(leaf.Record.Revision.Hash, snapshot.Modules[0].Hash);
            Assert.Equal(ScriptErrorCodes.ModuleChanged, Assert.Throws<ScriptContractException>(() => Store.Resolve(new[] {"root"}, pin)).ErrorCode);
            var revalidated=await Save("root",requires:new[]{"right","left"},expected:root.Record.Revision.Hash);
            Assert.False(revalidated.Changed);Assert.Equal(root.Record.CompiledHostJson,revalidated.Record.CompiledHostJson);
            Assert.NotEqual(JObject.Parse(root.Record.CompiledHostJson).Value<string>("module_manifest_hash"),revalidated.ValidationManifestHash);
            var orderChanged = await Save("root", requires: new[] {"left", "right"}, expected: root.Record.Revision.Hash);
            Assert.NotEqual(root.Record.Revision.Hash, orderChanged.Record.Revision.Hash);
        }

        [Fact]
        public async Task Missing_dependencies_and_cycle_introducing_edits_are_rejected()
        {
            Assert.Equal(ScriptErrorCodes.ModuleNotFound, (await Assert.ThrowsAsync<ScriptContractException>(() => Save("root", requires: new[] {"missing"}))).ErrorCode);
            var a = await Save("a"); await Save("b", requires: new[] {"a"});
            Assert.Equal(ScriptErrorCodes.ModuleCycle, (await Assert.ThrowsAsync<ScriptContractException>(() => Save("a", requires: new[] {"b"}, expected: a.Record.Revision.Hash))).ErrorCode);
            Assert.Equal(2, Store.List().Count);
        }

        [Fact]
        public async Task Shared_graph_paths_cannot_bypass_the_depth_budget()
        {
            for (var i=1; i<=ScriptLimits.DependencyDepth; i++) await Save("m" + i, requires: i == 1 ? null : new[] {"m" + (i-1)});
            Assert.Equal(16, Store.Resolve(new[] {"m16"}).Modules.Count);
            Assert.Equal(ScriptErrorCodes.ModuleLimitExceeded,
                (await Assert.ThrowsAsync<ScriptContractException>(() => Save("root", requires: new[] {"m1", "m16"}))).ErrorCode);
        }

        [Fact]
        public async Task Graph_count_and_aggregate_UTF8_budgets_are_enforced()
        {
            for(var i=0;i<32;i++) await Save("m"+i);
            Assert.Equal(32, Store.Resolve(Enumerable.Range(0,32).Select(i=>"m"+i)).Modules.Count);
            Assert.Equal(ScriptErrorCodes.ModuleLimitExceeded, (await Assert.ThrowsAsync<ScriptContractException>(() =>
                Save("root", requires: Enumerable.Range(0,32).Select(i=>"m"+i).ToArray()))).ErrorCode);
            for(var i=0;i<5;i++) await Save("large"+i, "class Large"+i+" {/*"+new string('x', 53000)+"*/}");
            Assert.Equal(ScriptErrorCodes.ModuleLimitExceeded, Assert.Throws<ScriptContractException>(() =>
                Store.Resolve(Enumerable.Range(0,5).Select(i=>"large"+i))).ErrorCode);
        }

        [Theory]
        [InlineData("after_revision")] [InlineData("after_head")] [InlineData("after_delete")]
        public async Task Exceptions_inside_write_transactions_roll_back_all_changes(string checkpoint)
        {
            var original = await Save("root");
            Store.WriteCheckpointForTests = point => {if(point == checkpoint) throw new IOException("Injected transaction failure");};
            if(checkpoint == "after_delete") Assert.Throws<IOException>(() => Store.Delete("root", original.Record.Revision.Hash));
            else await Assert.ThrowsAsync<IOException>(() => Save("root", "class Changed {}", expected: original.Record.Revision.Hash));
            Assert.Equal(original.Record.Revision.Hash, Assert.Single(Store.List()).Revision.Hash);
            Assert.Equal(1, Scalar("SELECT COUNT(*) FROM code_module_revisions"));
        }

        [Fact]
        public async Task Simultaneous_savers_have_one_CAS_winner()
        {
            var original = await Save("root"); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count=0;
            async Task<string> Writer(int index)
            {
                using var db = new BakeDb(DbPath); var store = new CodeModuleStore(db);
                try { await store.SaveAsync("root", "class Changed"+index+" {}", "", null, original.Record.Revision.Hash,
                    async (snapshot, token) => {if(Interlocked.Increment(ref count)==2) entered.TrySetResult(); await release.Task; return await Compile(snapshot, token);}); return "saved"; }
                catch(ScriptContractException ex){ return ex.ErrorCode; }
            }
            var first=Writer(1); var second=Writer(2); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); release.SetResult();
            Assert.Equal(new[] {ScriptErrorCodes.ModuleConflict, "saved"}, (await Task.WhenAll(first,second)).OrderBy(x=>x, StringComparer.Ordinal));
            Assert.Equal(2, Scalar("SELECT COUNT(*) FROM code_module_revisions"));
        }

        [Fact]
        public async Task Delete_checks_live_dependents_and_keeps_immutable_revisions()
        {
            var leaf=await Save("leaf"); var root=await Save("root", requires:new[]{"leaf"});
            Assert.Equal(ScriptErrorCodes.ModuleInUse, Assert.Throws<ScriptContractException>(()=>Store.Delete("leaf",leaf.Record.Revision.Hash)).ErrorCode);
            Store.Delete("root",root.Record.Revision.Hash); Store.Delete("leaf",leaf.Record.Revision.Hash);
            Assert.Empty(Store.List()); Assert.Equal(2,Scalar("SELECT COUNT(*) FROM code_module_revisions"));
            Assert.Throws<SqliteException>(()=>Sql("UPDATE code_module_revisions SET code='tampered'"));
            Assert.Throws<SqliteException>(()=>Sql("DELETE FROM code_module_revisions"));
        }

        [Theory]
        [InlineData("hash")] [InlineData("missing")] [InlineData("source")]
        public async Task Corrupt_heads_or_revisions_fail_closed_without_reset(string corruption)
        {
            var saved=await Save("root");
            if(corruption=="missing") Sql("PRAGMA foreign_keys=OFF; UPDATE code_modules SET revision_hash='"+new string('0',64)+"'");
            else {Sql("DROP TRIGGER code_module_revisions_no_update");
                Sql(corruption=="source" ? "UPDATE code_module_revisions SET code='class Tampered {}'" : "UPDATE code_module_revisions SET code_sha256='"+new string('0',64)+"'");
                Sql("CREATE TRIGGER code_module_revisions_no_update BEFORE UPDATE ON code_module_revisions BEGIN SELECT RAISE(ABORT,'immutable'); END");}
            Assert.Equal(ScriptErrorCodes.ModuleStoreCorrupt,Assert.Throws<ScriptContractException>(()=>Store.Resolve(new[]{"root"})).ErrorCode);
            Assert.Equal(1,Scalar("SELECT COUNT(*) FROM code_modules"));
            Assert.Equal(1,Scalar("SELECT COUNT(*) FROM code_module_revisions"));
        }

        [Theory]
        [InlineData("class Bad { string token=\"sk-examplecredential123456\"; }")]
        [InlineData("class Bad { string password=\"example-private-password\"; }")]
        [InlineData("class Bad { string json=\"{\\\"api_key\\\":\\\"example-private-key\\\"}\"; }")]
        public async Task Persistence_rejects_credential_like_source_without_rewriting_it(string source)
        {
            var error=await Assert.ThrowsAsync<ScriptContractException>(()=>Save("root",source));
            Assert.Equal(ScriptErrorCodes.SourceNotStorable,error.ErrorCode); Assert.DoesNotContain(source,error.Message);
            Assert.Empty(Store.List()); Assert.Equal(0,Scalar("SELECT COUNT(*) FROM code_module_revisions"));
        }

        [Fact]
        public async Task Store_is_independent_of_adaptive_bake_and_source_is_not_redacted()
        {
            const string source="class Helper { string key=System.Environment.GetEnvironmentVariable(\"API_KEY\"); }";
            var result=await Save("root",source); Assert.Equal(source,result.Record.Revision.Code);
            Assert.Equal(source,Assert.Single(Store.List()).Revision.Code);
            Assert.Empty(Store.List("absent")); Assert.Empty(Store.List(startIndex:1));
        }

        [Fact]
        public async Task Save_can_use_the_actual_non_executing_compiler_and_compile_failure_does_not_persist()
        {
            var marker=Path.Combine(Root,"initializer.txt");
            var code="public static class Helper { static Helper(){System.IO.File.WriteAllText("+JsonConvert.SerializeObject(marker)+",\"invoked\");} public static int Value=>7; }";
            Task<CodeModuleCompilation> Validate(CodeModuleSnapshot snapshot,CancellationToken token)
            {
                var result=SendCodeCompilerTests.Compile(new ScriptInput("return null;",modules:snapshot.Roots,compileOnly:true),snapshot.Modules.ToArray());
                if(!result.Success) throw new ScriptContractException(ScriptErrorCodes.CompileFailed,"Helper compile failed.");
                Assert.Null(result.AssemblyBytes); return Compile(snapshot,token);
            }
            await Store.SaveAsync("helper",code,"",null,null,Validate); Assert.False(File.Exists(marker));
            Assert.Equal(ScriptErrorCodes.CompileFailed,(await Assert.ThrowsAsync<ScriptContractException>(()=>
                Store.SaveAsync("invalid","return 7;","",null,null,Validate))).ErrorCode);
            Assert.Single(Store.List()); Assert.Equal(1,Scalar("SELECT COUNT(*) FROM code_module_revisions"));
        }

        [Fact]
        public async Task Dependency_delete_during_compile_refuses_the_commit_and_deleted_pins_are_changed()
        {
            var dependency=await Save("dependency");var snapshot=Store.Resolve(new[]{"dependency"});
            var pinned=snapshot.Modules.Select(m=>new ScriptModuleManifestEntry(m.Name,m.Hash,m.Requires)).ToArray();
            var error=await Assert.ThrowsAsync<ScriptContractException>(()=>Store.SaveAsync("root","class Root {}","",new[]{"dependency"},null,
                (graph,token)=>{Store.Delete("dependency",dependency.Record.Revision.Hash);return Compile(graph,token);}));
            Assert.Equal(ScriptErrorCodes.ModuleConflict,error.ErrorCode);Assert.Empty(Store.List());
            Assert.Equal(ScriptErrorCodes.ModuleChanged,Assert.Throws<ScriptContractException>(()=>Store.Resolve(new[]{"dependency"},pinned)).ErrorCode);
            var restored=await Save("dependency");Assert.Equal(dependency.Record.Revision.Hash,restored.Record.Revision.Hash);
            Assert.Equal(1,Scalar("SELECT COUNT(*) FROM code_module_revisions"));
        }

        [Theory]
        [InlineData("Bad")] [InlineData("../bad")] [InlineData("con")]
        public async Task Invalid_names_are_rejected_before_compile(string name)
        {
            Assert.Equal(ScriptErrorCodes.InputInvalid,(await Assert.ThrowsAsync<ScriptContractException>(()=>Save(name))).ErrorCode);
            Assert.Empty(Store.List());
        }

        [Fact]
        public async Task Invalid_dependency_contracts_and_per_module_UTF8_limits_are_enforced()
        {
            Assert.Equal(ScriptErrorCodes.InputInvalid,(await Assert.ThrowsAsync<ScriptContractException>(()=>Save("root",requires:new[]{"a","a"}))).ErrorCode);
            Assert.Equal(ScriptErrorCodes.ModuleCycle,(await Assert.ThrowsAsync<ScriptContractException>(()=>Save("root",requires:new[]{"root"}))).ErrorCode);
            Assert.Equal(ScriptErrorCodes.ModuleLimitExceeded,(await Assert.ThrowsAsync<ScriptContractException>(()=>Save("root","class X {/*"+new string('漢',24000)+"*/}"))).ErrorCode);
            Assert.Equal(ScriptErrorCodes.ModuleLimitExceeded,(await Assert.ThrowsAsync<ScriptContractException>(()=>Save("root",requires:Enumerable.Range(0,33).Select(i=>"m"+i).ToArray()))).ErrorCode);
            Assert.Empty(Store.List());
        }
    }
}
