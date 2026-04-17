using Bimwright.Rvt.Plugin;
using Xunit;

namespace Bimwright.Rvt.Tests
{
    public class SchemaValidatorTests
    {
        // Schema used by CreateLevelHandler — exercised as the canonical "real" schema
        private const string LevelSchema = @"{
            ""type"":""object"",
            ""properties"":{
                ""elevation"":{""type"":""number""},
                ""name"":{""type"":""string""}
            },
            ""required"":[""elevation""]
        }";

        // Schema with enum + array.items — models OperateElementHandler
        private const string OperateSchema = @"{
            ""type"":""object"",
            ""properties"":{
                ""operation"":{""type"":""string"",""enum"":[""move"",""rotate"",""mirror"",""copy"",""color""]},
                ""elementIds"":{""type"":""array"",""items"":{""type"":""integer""}}
            },
            ""required"":[""operation"",""elementIds""]
        }";

        // --- P2-004 required cases (aspect #5 §S6) -----------------------------

        [Fact]
        public void Validate_ValidSchema_PassesThrough()
        {
            var result = SchemaValidator.Validate(LevelSchema, @"{""elevation"":3000,""name"":""L1""}");
            Assert.True(result.IsValid);
            Assert.Null(result.Error);
        }

        [Fact]
        public void Validate_WrongType_FailsWithErrorAsTeacher()
        {
            var result = SchemaValidator.Validate(LevelSchema, @"{""elevation"":""tall""}");
            Assert.False(result.IsValid);
            Assert.Contains("'elevation'", result.Error);
            Assert.Contains("number", result.Error);
            Assert.Contains("string", result.Error);
            Assert.NotNull(result.Suggestion);
            Assert.NotNull(result.Hint);
        }

        [Fact]
        public void Validate_MissingRequired_Fails()
        {
            var result = SchemaValidator.Validate(LevelSchema, @"{""name"":""L1""}");
            Assert.False(result.IsValid);
            Assert.Contains("required", result.Error);
            Assert.Contains("'elevation'", result.Error);
            Assert.Contains("elevation", result.Suggestion);
        }

        [Fact]
        public void Validate_ExtraField_IsIgnored()
        {
            // Handler schemas don't set additionalProperties:false → validator tolerates unknowns.
            var result = SchemaValidator.Validate(LevelSchema, @"{""elevation"":3000,""unknown"":""junk""}");
            Assert.True(result.IsValid);
        }

        // --- Supplementary coverage --------------------------------------------

        [Fact]
        public void Validate_EmptySchema_AcceptsAnyParams()
        {
            Assert.True(SchemaValidator.Validate("{}", @"{""anything"":true}").IsValid);
            Assert.True(SchemaValidator.Validate("{}", null).IsValid);
            Assert.True(SchemaValidator.Validate("", @"{""x"":1}").IsValid);
        }

        [Fact]
        public void Validate_EnumMismatch_FailsWithAllowedList()
        {
            var result = SchemaValidator.Validate(OperateSchema,
                @"{""operation"":""teleport"",""elementIds"":[1,2]}");
            Assert.False(result.IsValid);
            Assert.Contains("'operation'", result.Error);
            Assert.Contains("teleport", result.Error);
            Assert.Contains("move", result.Error);
        }

        [Fact]
        public void Validate_ArrayItemsTypeMismatch_Fails()
        {
            var result = SchemaValidator.Validate(OperateSchema,
                @"{""operation"":""move"",""elementIds"":[1,""two"",3]}");
            Assert.False(result.IsValid);
            Assert.Contains("elementIds[1]", result.Error);
            Assert.Contains("integer", result.Error);
        }

        [Fact]
        public void Validate_IntegerAcceptedAsNumber()
        {
            // JSON number "3000" (integer token) should satisfy type:number — avoid false rejects
            var result = SchemaValidator.Validate(LevelSchema, @"{""elevation"":3000}");
            Assert.True(result.IsValid);
        }

        [Fact]
        public void Validate_MalformedParams_FailsWithGuidance()
        {
            var result = SchemaValidator.Validate(LevelSchema, "not-json");
            Assert.False(result.IsValid);
            Assert.Contains("JSON object", result.Error);
            Assert.NotNull(result.Suggestion);
        }

        [Fact]
        public void Validate_MalformedSchema_FailsOpen()
        {
            // Internal bug — don't punish the user. Accept params.
            var result = SchemaValidator.Validate("{not-valid-json", @"{""x"":1}");
            Assert.True(result.IsValid);
        }
    }
}
