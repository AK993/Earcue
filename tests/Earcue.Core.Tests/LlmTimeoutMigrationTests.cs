using Earcue.Core.Configuration;

namespace Earcue.Core.Tests;

public class LlmTimeoutMigrationTests
{
    [Theory]
    [InlineData(5000, 20000, 15000, 60000)]
    [InlineData(8000, 40000, 8000, 40000)]
    [InlineData(30, 2000, 30, 2000)]
    public void OldDefaultsMigrateWhileCustomTimeoutsRemainUnchanged(int first, int total, int expectedFirst, int expectedTotal)
    {
        var path = Path.Combine(Path.GetTempPath(), "earcue_timeout_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, $"{{\"llm\":{{\"firstTokenTimeoutMs\":{first},\"timeoutMs\":{total}}}}}");
            var config = EarcueConfig.LoadFromFile(path);
            Assert.Equal(expectedFirst, config.Llm.FirstTokenTimeoutMs);
            Assert.Equal(expectedTotal, config.Llm.TimeoutMs);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
