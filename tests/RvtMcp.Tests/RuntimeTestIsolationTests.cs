using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace RvtMcp.Tests
{
    public class RuntimeTestIsolationTests
    {
        [Theory]
        [InlineData(typeof(RuntimeControlsTests))]
        [InlineData(typeof(RuntimeProtocolTests))]
        [InlineData(typeof(PromptBodyTests))]
        [InlineData(typeof(RevitPromptsTests))]
        public void Tests_that_change_server_config_cannot_run_alongside_shell_captures(Type testClass)
        {
            var collection = Assert.Single(testClass.CustomAttributes,
                attribute => attribute.AttributeType == typeof(CollectionAttribute));
            var name = collection.ConstructorArguments[0].Value;
            var definition = Assert.Single(testClass.Assembly.GetTypes(), type => type.CustomAttributes.Any(
                attribute => attribute.AttributeType == typeof(CollectionDefinitionAttribute)
                    && Equals(attribute.ConstructorArguments[0].Value, name)));
            Assert.True(definition.GetCustomAttribute<CollectionDefinitionAttribute>().DisableParallelization);
        }
    }
}
