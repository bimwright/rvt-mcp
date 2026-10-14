using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Xunit;

namespace RvtMcp.Tests
{
    public class SqliteRuntimeTests
    {
        [Fact]
        public void BundledEngineIsPatchedAndPersistsCommittedDataOnly()
        {
            var path = Path.Combine(Path.GetTempPath(), "rvt-sqlite-" + Guid.NewGuid().ToString("N") + ".db");
            var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
            try
            {
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();
                    var version = Version.Parse((string)Scalar(connection, "SELECT sqlite_version()"));
                    Assert.True(version >= new Version(3, 50, 2), "CVE-2025-6965 requires SQLite >= 3.50.2; loaded " + version);
                    Scalar(connection, "CREATE TABLE sample (id INTEGER PRIMARY KEY, value TEXT NOT NULL)");
                    using (var transaction = connection.BeginTransaction())
                    {
                        Scalar(connection, "INSERT INTO sample VALUES (1, 'committed')", transaction);
                        transaction.Commit();
                    }
                    using (var transaction = connection.BeginTransaction())
                    {
                        Scalar(connection, "INSERT INTO sample VALUES (2, 'rolled back')", transaction);
                        transaction.Rollback();
                    }
                }

                using (var reopened = new SqliteConnection(connectionString))
                {
                    reopened.Open();
                    Assert.Equal(1L, Scalar(reopened, "SELECT COUNT(*) FROM sample"));
                    Assert.Equal("committed", Scalar(reopened, "SELECT value FROM sample WHERE id=1"));
                    Assert.Equal("ok", Scalar(reopened, "PRAGMA integrity_check"));
                }
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static object Scalar(SqliteConnection connection, string sql, SqliteTransaction transaction = null)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = transaction;
            return command.ExecuteScalar();
        }
    }
}
