using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RvtMcp.Plugin.Localization;
using Xunit;

namespace RvtMcp.Tests
{
    public class OverrideValidatorTests : IDisposable
    {
        private readonly string _dir =
            Path.Combine(Path.GetTempPath(), "rvt-l10n-val-" + Guid.NewGuid().ToString("N"));

        public OverrideValidatorTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static Dictionary<string, string> En() => new Dictionary<string, string>
        {
            ["plain"] = "Hello",
            ["with.count"] = "Rooms found: {count:n}",
            ["with.name"] = "Saved: {fileName}",
            ["security.rerun.confirm"] = "Execute anyway?",
        };

        private static List<string> Reasons(OverrideValidator.Result r)
            => r.Rejected.Select(x => x.Value).ToList();

        [Fact]
        public void ValidEntries_AreAccepted()
        {
            var json = "{ \"plain\": \"Hallo\", \"with.count\": \"Räume: {count:n}\" }";
            var r = OverrideValidator.ValidateJson(json, En());
            Assert.Null(r.FileError);
            Assert.Empty(r.Rejected);
            Assert.Equal("Hallo", r.Accepted["plain"]);
            Assert.Equal("Räume: {count:n}", r.Accepted["with.count"]);
        }

        [Fact]
        public void InvalidJson_RejectsWholeFile()
        {
            var r = OverrideValidator.ValidateJson("{ not json", En());
            Assert.Equal("invalid_json", r.FileError);
            Assert.Empty(r.Accepted);
        }

        [Fact]
        public void Meta_IsIgnored_NotUnknownKey()
        {
            var json = "{ \"_meta\": { \"locale\": \"de\" }, \"plain\": \"Hallo\" }";
            var r = OverrideValidator.ValidateJson(json, En());
            Assert.Empty(r.Rejected);
            Assert.Single(r.Accepted);
        }

        [Fact]
        public void UnknownKey_Rejected()
        {
            var r = OverrideValidator.ValidateJson("{ \"typo.key\": \"x\" }", En());
            Assert.Equal("unknown_key", Assert.Single(r.Rejected).Value);
        }

        [Fact]
        public void LockedKey_SecurityPrefix_Rejected()
        {
            var r = OverrideValidator.ValidateJson(
                "{ \"security.rerun.confirm\": \"just do it\" }", En());
            Assert.Equal("locked_key", Assert.Single(r.Rejected).Value);
            Assert.Empty(r.Accepted);
        }

        [Fact]
        public void PlaceholderMismatch_Rejected()
        {
            // missing {count:n}
            var r = OverrideValidator.ValidateJson("{ \"with.count\": \"Räume\" }", En());
            Assert.Equal("placeholder_mismatch", Assert.Single(r.Rejected).Value);

            // extra token
            r = OverrideValidator.ValidateJson(
                "{ \"with.count\": \"{count:n} {extra}\" }", En());
            Assert.Equal("placeholder_mismatch", Assert.Single(r.Rejected).Value);

            // wrong token form ({count} ≠ {count:n})
            r = OverrideValidator.ValidateJson(
                "{ \"with.count\": \"{count}\" }", En());
            Assert.Equal("placeholder_mismatch", Assert.Single(r.Rejected).Value);
        }

        [Fact]
        public void ValueTooLong_Rejected()
        {
            var longValue = new string('x', OverrideValidator.MaxValueLength + 1);
            var r = OverrideValidator.ValidateJson(
                "{ \"plain\": \"" + longValue + "\" }", En());
            Assert.Equal("value_too_long", Assert.Single(r.Rejected).Value);
        }

        [Fact]
        public void NonStringValue_Rejected()
        {
            var r = OverrideValidator.ValidateJson("{ \"plain\": 42 }", En());
            Assert.Equal("not_a_string", Assert.Single(r.Rejected).Value);
        }

        [Fact]
        public void MissingFile_EmptyResultNoError()
        {
            var r = OverrideValidator.ValidateFile(
                Path.Combine(_dir, "strings.de.json"), En());
            Assert.Null(r.FileError);
            Assert.Empty(r.Accepted);
            Assert.Empty(r.Rejected);
        }

        [Fact]
        public void FileTooLarge_RejectsWholeFile()
        {
            var path = Path.Combine(_dir, "strings.de.json");
            File.WriteAllText(path, new string('x', (int)OverrideValidator.MaxFileBytes + 1));
            var r = OverrideValidator.ValidateFile(path, En());
            Assert.Equal("file_too_large", r.FileError);
            Assert.Empty(r.Accepted);
        }

        [Fact]
        public void HalfWrittenFile_InvalidJson_PreviousTableWouldStay()
        {
            var path = Path.Combine(_dir, "strings.de.json");
            File.WriteAllText(path, "{ \"plain\": \"Hal");   // half-written
            var r = OverrideValidator.ValidateFile(path, En());
            Assert.Equal("invalid_json", r.FileError);
            Assert.Empty(r.Accepted);
        }

        [Fact]
        public void EnOverrideFile_IsAllowed()
        {
            // strings.en.json is a legitimate override target; locked still applies.
            var r = OverrideValidator.ValidateJson("{ \"plain\": \"Hey\" }", En());
            Assert.Equal("Hey", r.Accepted["plain"]);
        }
    }
}
