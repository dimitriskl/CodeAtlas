using Xunit;

namespace CodeAtlas.Tests;

public sealed class DescriptionEnvironmentTests
{
    [Fact]
    public void ConfiguringDescriptionsDoesNotInvalidateTheSemanticEnvironment()
    {
        lock (SnapshotEnvironment.Gate)
        {
            // Arrange
            string[] names = ["CODEATLAS_AI_API_KEY", "CODEATLAS_AI_MODEL", "CODEATLAS_AI_ENDPOINT", "CODEATLAS_AI_PROVIDER", "CODEATLAS_AI_EFFORT", "CODEATLAS_AI_OPENAI_API_KEY", "CODEATLAS_AI_ANTHROPIC_API_KEY"];
            var original = names.Select(Environment.GetEnvironmentVariable).ToArray();
            foreach (var name in names) Environment.SetEnvironmentVariable(name, null);
            var baseline = SnapshotInputModel.EnvironmentDigest(SnapshotEnvironment.Capture());
            try
            {
                foreach (var name in names) Environment.SetEnvironmentVariable(name, "fixture-provider-setting");

                // Act
                var current = SnapshotEnvironment.Capture();
                var configured = SnapshotInputModel.EnvironmentDigest(current);

                // Assert
                Assert.Equal(baseline, configured);
                Assert.All(names, name => Assert.False(current.ContainsKey(name)));
            }
            finally
            {
                for (var i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], original[i]);
            }
        }
    }
}
