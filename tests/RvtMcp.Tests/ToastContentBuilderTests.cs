using System;
using RvtMcp.Plugin;
using RvtMcp.Plugin.Localization;
using RvtMcp.Plugin.Views.Toast;
using Xunit;

namespace RvtMcp.Tests
{
    [Collection("L10n")]
    public class ToastContentBuilderTests : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rvt-toast-test-" + Guid.NewGuid().ToString("N"));
        public ToastContentBuilderTests()
        {
            L.ResetForTests();
            var en = EmbeddedCatalog.Load(typeof(ToastContentBuilderTests).Assembly, "en");
            L.InitializeForTests(StringTable.Build("en", en, null, null));
        }

        public void Dispose()
        {
            L.ResetForTests();
            if (System.IO.Directory.Exists(_root)) System.IO.Directory.Delete(_root, true);
        }

        [Fact]
        public void BuildCompleted_get_current_view_info_includes_view_name()
        {
            var vm = ToastContentBuilder.BuildCompleted(
                toolName: "get_current_view_info",
                paramsJson: null,
                resultJson: "{\"viewName\":\"Level 1\",\"viewType\":\"FloorPlan\",\"scale\":100,\"levelName\":\"Level 1\"}",
                success: true,
                errorMessage: null,
                durationMs: 12,
                toolDescription: null
            );

            Assert.Equal("MCP · Query", vm.CategoryLabel);
            Assert.Contains("Level 1", vm.Summary);
            Assert.Contains("FloorPlan", vm.Summary);
            Assert.Contains("Scale 1:100", vm.Detail);
        }

        [Fact]
        public void BuildCompleted_list_rooms_reports_counts()
        {
            var vm = ToastContentBuilder.BuildCompleted(
                toolName: "list_rooms",
                paramsJson: "{\"level_name\":\"L1\"}",
                resultJson: "{\"total\":3,\"returned\":3,\"counts\":{\"placed\":2,\"unplaced\":1}}",
                success: true,
                errorMessage: null,
                durationMs: 40,
                toolDescription: null
            );

            Assert.Equal("Rooms found: 3", vm.Summary);
            Assert.Contains("Level: L1", vm.Detail);
            Assert.Contains("Placed 2", vm.Detail);
        }

        [Fact]
        public void BuildCompleted_capture_view_image_sets_thumbnail_path()
        {
            var capturesDir = System.IO.Path.Combine(_root, "captures");
            System.IO.Directory.CreateDirectory(capturesDir);
            var path = System.IO.Path.Combine(capturesDir, "toast-test-thumb.png");
            System.IO.File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

            try
            {
                var vm = ToastContentBuilder.BuildCompleted(
                    toolName: "capture_view_image",
                    paramsJson: null,
                    resultJson: $"{{\"saved_path\":\"{path.Replace("\\", "\\\\")}\",\"pixel_size\":800,\"image_format\":\"png\",\"view_id\":19}}",
                    success: true,
                    errorMessage: null,
                    durationMs: 200,
                    toolDescription: null
                );

                Assert.Equal("MCP · Snapshot", vm.CategoryLabel);
                Assert.Contains("toast-test-thumb.png", vm.Summary);
                Assert.Contains("800px PNG", vm.Summary);
                Assert.DoesNotContain("Click to open", vm.Detail);
                Assert.Equal(path, vm.ThumbnailPath);
            }
            finally
            {
                if (System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
            }
        }

        [Fact]
        public void BuildCompleted_failure_uses_error_message()
        {
            var vm = ToastContentBuilder.BuildCompleted(
                toolName: "capture_view_image",
                paramsJson: null,
                resultJson: null,
                success: false,
                errorMessage: "output_path is required.",
                durationMs: 0,
                toolDescription: "Export a view to a raster image."
            );

            Assert.Equal("MCP · Failed", vm.CategoryLabel);
            Assert.Contains("output_path", vm.Summary);
            Assert.Contains("raster image", vm.Detail);
            Assert.False(vm.Success);
        }

        [Fact]
        public void BuildCompleted_handles_local_path_placeholder_without_throwing()
        {
            // Verify that redacted <local_path> placeholder does not throw on invalid characters.
            var vm = ToastContentBuilder.BuildCompleted(
                toolName: "capture_view_image",
                paramsJson: null,
                resultJson: "{\"saved_path\":\"<local_path>\",\"pixel_size\":800}",
                success: true,
                errorMessage: null,
                durationMs: 150,
                toolDescription: null
            );

            Assert.Equal("MCP · Snapshot", vm.CategoryLabel);
            Assert.Contains("image", vm.Summary); // Fallback filename "image"
            Assert.Null(vm.ThumbnailPath); // Invalid path is not safe, so null
        }

        [Fact]
        public void BuildCompleted_export_pdf_handles_local_path_placeholder_without_throwing()
        {
            var vm = ToastContentBuilder.BuildCompleted(
                toolName: "export_pdf",
                paramsJson: null,
                resultJson: "{\"saved_path\":\"<local_path>\"}",
                success: true,
                errorMessage: null,
                durationMs: 500,
                toolDescription: null
            );

            Assert.Equal("MCP · Export", vm.CategoryLabel);
            Assert.Equal("file", vm.Summary);
            Assert.Null(vm.ThumbnailPath);
        }

        [Fact]
        public void BuildCompleted_null_result_uses_generic_success_copy()
        {
            var vm = ToastContentBuilder.BuildCompleted(
                toolName: "list_levels",
                paramsJson: null,
                resultJson: null,
                success: true,
                errorMessage: null,
                durationMs: 8,
                toolDescription: null
            );

            Assert.Equal("MCP · Query", vm.CategoryLabel);
            Assert.Equal("Completed successfully", vm.Summary);
            Assert.Equal("List Levels", vm.Detail);
        }

        [Theory]
        [InlineData(@"C:\Windows\System32\cmd.exe")]
        [InlineData(null)]
        [InlineData("<local_path>")]
        public void IsSafeImagePath_rejects_unsafe_paths(string path)
        {
            Assert.False(ToastContentBuilder.IsSafeImagePath(path));
        }

        [Fact]
        public void IsSafeImagePath_rejects_sibling_of_captures_directory()
        {
            var siblingDir = System.IO.Path.Combine(_root, "capturesEvil");
            System.IO.Directory.CreateDirectory(siblingDir);
            var path = System.IO.Path.Combine(siblingDir, "evil.png");
            System.IO.File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

            try
            {
                Assert.False(ToastContentBuilder.IsSafeImagePath(path, System.IO.Path.Combine(_root, "temp"), System.IO.Path.Combine(_root, "captures")));
            }
            finally
            {
                if (System.IO.File.Exists(path))
                    System.IO.File.Delete(path);
                try { System.IO.Directory.Delete(siblingDir); } catch { }
            }
        }

        [Fact]
        public void BuildCompleted_send_code_shows_result_line()
        {
            var vm = ToastContentBuilder.BuildCompleted(
                toolName: "send_code_to_revit",
                paramsJson: null,
                resultJson: "{\"result\":\"Hello from script\\nsecond line\"}",
                success: true,
                errorMessage: null,
                durationMs: 10,
                toolDescription: null
            );

            Assert.Equal("MCP · Script", vm.CategoryLabel);
            Assert.Equal("Hello from script", vm.Summary);
        }

        [Theory]
        [InlineData("{\"result\":{\"marker\":\"probe\"}}", "{\"marker\":\"probe\"}")]
        [InlineData("{\"result\":{\"items\":[1,null,true]}}", "{\"items\":[1,null,true]}")]
        [InlineData("{\"result\":[1,2]}", "[1,2]")]
        [InlineData("{\"result\":[{\"name\":\"Cột\"},null]}", "[{\"name\":\"Cột\"},null]")]
        [InlineData("{\"result\":{}}", "{}")]
        [InlineData("{\"result\":[]}", "[]")]
        [InlineData("{\"result\":42}", "42")]
        [InlineData("{\"result\":true}", "True")]
        [InlineData("{\"result\":null}", "Script finished")]
        [InlineData("{}", "Script finished")]
        [InlineData("{\"result\":\"\"}", "Script finished")]
        public void BuildCompleted_send_code_accepts_json_result_shapes(string resultJson, string expectedSummary)
        {
            var vm = ToastContentBuilder.BuildCompleted(
                "send_code_to_revit", null, resultJson, true, null, 10, null);

            Assert.True(vm.Success);
            Assert.Equal("MCP · Script", vm.CategoryLabel);
            Assert.Equal(expectedSummary, vm.Summary);
            Assert.Equal("Custom C# executed in Revit", vm.Detail);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void BuildCompleted_send_code_bounds_structured_result_preview(bool array)
        {
            var value = new string('界', 200);
            var json = array ? "[\"" + value + "\"]" : "{\"text\":\"" + value + "\"}";
            var vm = ToastContentBuilder.BuildCompleted(
                "send_code_to_revit", null, "{\"result\":" + json + "}", true, null, 10, null);

            Assert.True(vm.Success);
            Assert.InRange(vm.Summary.Length, 1, 100);
            Assert.StartsWith(array ? "[\"界" : "{\"text\":\"界", vm.Summary);
            Assert.DoesNotContain("\n", vm.Summary);
            Assert.DoesNotContain("\r", vm.Summary);
        }

        [Fact]
        public void BuildCompleted_uses_active_string_table_but_keeps_data_verbatim()
        {
            var table = StringTable.Build("tt", new System.Collections.Generic.Dictionary<string, string>
            {
                ["toast.category.query"] = "TRUY VẤN",
                ["toast.rooms.found"] = "Tìm thấy phòng: {count:n}",
                ["toast.filters.level"] = "Tầng: {level}",
                ["toast.filters.allPhases"] = "Mọi pha",
            }, null, null);
            L.InitializeForTests(table);

            var vm = ToastContentBuilder.BuildCompleted(
                toolName: "list_rooms",
                paramsJson: "{\"level_name\":\"L1\"}",
                resultJson: "{\"total\":3,\"returned\":3}",
                success: true,
                errorMessage: null,
                durationMs: 40,
                toolDescription: null
            );

            Assert.Equal("TRUY VẤN", vm.CategoryLabel);
            Assert.Equal("Tìm thấy phòng: 3", vm.Summary);
            Assert.Equal("Tầng: L1 · Mọi pha", vm.Detail);
        }

        [Fact]
        public void BuildCompleted_failure_keeps_error_message_verbatim_under_localization()
        {
            var table = StringTable.Build("tt", new System.Collections.Generic.Dictionary<string, string>
            {
                ["toast.category.failed"] = "THẤT BẠI",
                ["toast.failed.default"] = "Lỗi không rõ",
            }, null, null);
            L.InitializeForTests(table);

            var vm = ToastContentBuilder.BuildCompleted(
                toolName: "capture_view_image",
                paramsJson: null,
                resultJson: null,
                success: false,
                errorMessage: "output_path is required.",
                durationMs: 0,
                toolDescription: "Export a view to a raster image."
            );

            Assert.Equal("THẤT BẠI", vm.CategoryLabel);
            Assert.Equal("output_path is required.", vm.Summary);
            Assert.Contains("raster image", vm.Detail);
        }
    }
}
