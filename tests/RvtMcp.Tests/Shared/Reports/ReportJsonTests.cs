using System;
using Newtonsoft.Json.Linq;
using RvtMcp.Plugin.Reports.Model;
using Xunit;

namespace RvtMcp.Tests.Shared.Reports
{
    public sealed class ReportJsonTests
    {
        [Fact]
        public void Schema_v1_round_trips_all_fields_with_snake_case_names()
        {
            const string json = @"{
              'schema_version':1,'report_id':'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
              'task_id':'synthetic-task','outcome':'completed','summary':'Synthetic result',
              'body':'Fixture [piece](revit:#0)',
              'created_utc':'2026-01-02T03:04:05.0000000Z','updated_utc':'2026-01-02T04:05:06.0000000Z',
              'source':{'target_id':'synthetic-123','pid':123,'host_year':2099,'process_start_utc':'2026-01-02T01:00:00.0000000Z'},
              'documents':[{'ref':'d0','token':'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','model_key':'synthetic-key','title':'Synthetic document','is_family':false,'kind':'central'}],
              'links':[{'label':'piece','doc':'d0','view':{'id':456,'unique_id':'synthetic-view'},
                        'elements':[{'id':123,'unique_id':'synthetic-piece','kind':'element','category':'Synthetic category','name':'Synthetic piece'}]}],
              'changes':{'complete':true,'truncated':false,'documents':[{'doc':'d0',
                         'added':[{'id':1,'unique_id':'synthetic-added','category':'Synthetic category'}],
                         'modified':[],'deleted':[{'id':3,'unique_id':null,'category':null}]}]}
            }";

            Assert.True(ReportJson.TryDeserialize(json, out var report, out var status));
            Assert.Equal(ReportJsonReadStatus.Success, status);
            Assert.Equal(123L, report.Links[0].Elements[0].Id);
            Assert.Equal("synthetic-added", report.Changes.Documents[0].Added[0].UniqueId);
            Assert.True(JToken.DeepEquals(JObject.Parse(json), JObject.Parse(ReportJson.Serialize(report))));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("null")]
        [InlineData("[]")]
        [InlineData("{}")]
        [InlineData("{\"schema_version\":0}")]
        [InlineData("{\"schema_version\":-1}")]
        [InlineData("{\"schema_version\":\"1\"}")]
        [InlineData("{\"schema_version\":1.5}")]
        [InlineData("{\"schema_version\":1,\"documents\":42}")]
        [InlineData("{\"schema_version\":1} true")]
        public void Corrupt_files_return_a_status_and_no_report(string json)
        {
            Assert.False(ReportJson.TryDeserialize(json, out var report, out var status));
            Assert.Equal(ReportJsonReadStatus.Corrupt, status);
            Assert.Null(report);
        }

        [Theory]
        [InlineData("2")]
        [InlineData("9999999999999999999999999999999999999999")]
        public void Newer_schema_is_distinguished_before_reading_its_fields(string version)
        {
            Assert.False(ReportJson.TryDeserialize("{\"schema_version\":" + version + ",\"documents\":42}", out var report, out var status));
            Assert.Equal(ReportJsonReadStatus.UnsupportedSchema, status);
            Assert.Null(report);
        }

        [Fact]
        public void Missing_changes_is_written_as_explicit_null_and_differs_from_empty_changes()
        {
            var report = new AgentReport();
            Assert.Contains("\"changes\":null", ReportJson.Serialize(report));
            report.Changes = new ReportChanges();
            var json = ReportJson.Serialize(report);
            Assert.True(ReportJson.TryDeserialize(json, out var reread, out _));
            Assert.NotNull(reread.Changes);
            Assert.Empty(reread.Changes.Documents);
        }

        [Fact]
        public void Unknown_fields_are_ignored_and_date_like_text_is_preserved_exactly()
        {
            const string dateLike = "2026-01-02T03:04:05.1234567+07:00";
            var json = "{\"schema_version\":1,\"summary\":\"" + dateLike + "\",\"body\":\"" + dateLike
                + "\",\"created_utc\":\"" + dateLike + "\",\"future_field\":{\"enabled\":true}}";
            Assert.True(ReportJson.TryDeserialize(json, out var report, out _));
            Assert.Equal(dateLike, report.Summary);
            Assert.Equal(dateLike, report.Body);
            Assert.Equal(dateLike, report.CreatedUtc);
            Assert.DoesNotContain("future_field", ReportJson.Serialize(report));
        }

        [Fact]
        public void Unknown_metadata_like_fields_are_ignored_as_ordinary_fields()
        {
            Assert.True(ReportJson.TryDeserialize("{\"$ref\":\"synthetic\",\"schema_version\":1,\"summary\":\"fixture\"}", out var report, out _));
            Assert.Equal("fixture", report.Summary);
        }

        [Fact]
        public void Utc_timestamp_formatter_uses_round_trip_iso_8601()
        {
            var value = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).AddTicks(1234567);
            Assert.Equal("2026-01-02T03:04:05.1234567Z", ReportJson.FormatUtc(value));
        }

        [Theory]
        [InlineData(ReportDocumentKinds.Detached)]
        [InlineData(ReportDocumentKinds.Unsaved)]
        public void Detached_and_unsaved_documents_preserve_null_model_key(string kind)
        {
            var report = new AgentReport();
            report.Documents.Add(new ReportDocument { Kind = kind, IsFamily = true });
            var json = ReportJson.Serialize(report);
            Assert.Contains("\"model_key\":null", json);
            Assert.True(ReportJson.TryDeserialize(json, out var reread, out _));
            Assert.Equal(kind, reread.Documents[0].Kind);
            Assert.True(reread.Documents[0].IsFamily);
            Assert.Null(reread.Documents[0].ModelKey);
        }
    }
}
