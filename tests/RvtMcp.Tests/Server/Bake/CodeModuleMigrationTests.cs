using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RvtMcp.Scripting;
using RvtMcp.Server.Bake;
using Xunit;

namespace RvtMcp.Tests
{
    public class CodeModuleMigrationTests : IDisposable
    {
        private readonly string _root=Path.Combine(Path.GetTempPath(),"rvt-module-migration-"+Guid.NewGuid().ToString("N"));
        private string PathToDb=>Path.Combine(_root,"bake.db");
        public CodeModuleMigrationTests(){Directory.CreateDirectory(_root);}
        public void Dispose(){Directory.Delete(_root,true);}
        private static long Scalar(string path,string sql)
        {using var c=ScriptBaselineFixture.Open(path);using var q=c.CreateCommand();q.CommandText=sql;return Convert.ToInt64(q.ExecuteScalar());}

        [Fact]
        public void Backup_includes_committed_WAL_data_and_keeps_the_populated_v1_snapshot()
        {
            var dll=ScriptBaselineFixture.CreateLegacyDatabase(PathToDb);
            using var writer=ScriptBaselineFixture.Open(PathToDb); using(var q=writer.CreateCommand())
            {q.CommandText="PRAGMA journal_mode=WAL; UPDATE registry SET usage_count=123 WHERE name='baseline_compiled'";q.ExecuteNonQuery();}
            using var db=new BakeDb(PathToDb); db.Migrate();
            Assert.NotNull(db.MigrationBackupPath); Assert.True(File.Exists(db.MigrationBackupPath));
            Assert.Equal(1,Scalar(db.MigrationBackupPath,"SELECT version FROM schema_version"));
            Assert.Equal(123,Scalar(db.MigrationBackupPath,"SELECT usage_count FROM registry WHERE name='baseline_compiled'"));
            Assert.Equal(5,Scalar(db.MigrationBackupPath,"SELECT COUNT(*) FROM registry"));
            Assert.Equal(0,Scalar(db.MigrationBackupPath,"SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'code_module%'"));
            Assert.Equal(dll, db.ReadRegistryRecords().Single(r=>r.Name=="baseline_compiled").DllBytes);
            Assert.Equal(2,Scalar(PathToDb,"SELECT version FROM schema_version")); db.Migrate();
            Assert.Single(Directory.GetFiles(_root,"*.bak"));
        }

        [Fact]
        public void Migration_failure_rolls_back_schema_and_preserves_a_consistent_backup()
        {
            ScriptBaselineFixture.CreateLegacyDatabase(PathToDb);
            using var db=new BakeDb(PathToDb){MigrationBeforeCommitForTests=()=>throw new IOException("Injected migration interruption")};
            Assert.Throws<IOException>(()=>db.Migrate());
            Assert.Equal(1,Scalar(PathToDb,"SELECT version FROM schema_version"));
            Assert.Equal(0,Scalar(PathToDb,"SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'code_module%'"));
            Assert.Equal(5,Scalar(PathToDb,"SELECT COUNT(*) FROM registry"));
            Assert.Equal(1,Scalar(db.MigrationBackupPath,"SELECT version FROM schema_version"));
            db.MigrationBeforeCommitForTests=null;db.Migrate();Assert.Equal(2,Scalar(PathToDb,"SELECT version FROM schema_version"));
        }

        [Fact]
        public async Task Concurrent_migration_makes_one_pre_schema_backup()
        {
            ScriptBaselineFixture.CreateLegacyDatabase(PathToDb);
            await Task.WhenAll(Enumerable.Range(0,2).Select(_=>Task.Run(()=>{using var db=new BakeDb(PathToDb);db.Migrate();})));
            Assert.Single(Directory.GetFiles(_root,"*.bak"));Assert.Equal(2,Scalar(PathToDb,"SELECT version FROM schema_version"));
        }

        [Fact]
        public void Corrupt_current_schema_is_not_recreated_and_future_version_is_not_downgraded()
        {
            ScriptBaselineFixture.CreateLegacyDatabase(PathToDb);
            using var db=new BakeDb(PathToDb);db.Migrate();var store=new CodeModuleStore(db);
            using(var c=ScriptBaselineFixture.Open(PathToDb))using(var q=c.CreateCommand())
            {q.CommandText="DROP TABLE code_modules";q.ExecuteNonQuery();}
            db.Migrate(); // legacy preset/macro paths call Migrate and must keep working
            Assert.Equal(0,Scalar(PathToDb,"SELECT COUNT(*) FROM sqlite_master WHERE name='code_modules'"));
            Assert.Equal(5,db.ReadRegistryRecords().Count);
            Assert.Equal(ScriptErrorCodes.ModuleStoreCorrupt,Assert.Throws<ScriptContractException>(()=>store.Resolve(new[]{"root"})).ErrorCode);
            using(var c=ScriptBaselineFixture.Open(PathToDb))using(var q=c.CreateCommand())
            {q.CommandText="UPDATE schema_version SET version=99";q.ExecuteNonQuery();}
            db.Migrate();Assert.Equal(99,Scalar(PathToDb,"SELECT version FROM schema_version"));
            Assert.Equal(5,db.ReadRegistryRecords().Count);
            var future=Assert.Throws<ScriptContractException>(()=>store.Resolve(new[]{"root"}));
            Assert.Equal(ScriptErrorCodes.ModuleStoreCorrupt,future.ErrorCode);Assert.Contains("newer schema",future.Message);
            Assert.Single(Directory.GetFiles(_root,"*.bak"));
        }

        [Fact]
        public void Module_tables_without_v2_record_are_left_for_the_store_to_refuse()
        {
            ScriptBaselineFixture.CreateLegacyDatabase(PathToDb);
            using(var c=ScriptBaselineFixture.Open(PathToDb))using(var q=c.CreateCommand())
            {q.CommandText="CREATE TABLE code_modules(name TEXT PRIMARY KEY)";q.ExecuteNonQuery();}
            using var db=new BakeDb(PathToDb);db.Migrate();
            Assert.Equal(1,Scalar(PathToDb,"SELECT version FROM schema_version"));
            Assert.Equal(5,db.ReadRegistryRecords().Count);Assert.Empty(Directory.GetFiles(_root,"*.bak"));
            Assert.Equal(ScriptErrorCodes.ModuleStoreCorrupt,Assert.Throws<ScriptContractException>(()=>new CodeModuleStore(db).Resolve(new[]{"root"})).ErrorCode);
        }

        [Fact]
        public void Store_does_not_initialize_a_missing_database()
        {
            using var db=new BakeDb(PathToDb);var store=new CodeModuleStore(db);
            Assert.Equal(ScriptErrorCodes.ModuleStoreCorrupt,Assert.Throws<ScriptContractException>(()=>store.Resolve(new[]{"root"})).ErrorCode);
            Assert.False(File.Exists(PathToDb));
        }
    }
}
