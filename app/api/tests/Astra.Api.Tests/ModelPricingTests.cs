using Astra.Api.Llm;
using Xunit;

namespace Astra.Api.Tests;

public class ModelPricingTests
{
    [Fact]
    public void Haiku_IsBilledAtHaikuRates()
    {
        // 1M in @ $1 + 1M out @ $5 = $6
        var usd = ModelPricing.Estimate("anthropic", "claude-haiku-4-5-20251001", 1_000_000, 1_000_000);
        Assert.Equal(6m, usd);
    }

    [Fact]
    public void Sonnet_IsBilledAtSonnetRates()
    {
        var usd = ModelPricing.Estimate("anthropic", "claude-sonnet-4-5-20250929", 1_000_000, 1_000_000);
        Assert.Equal(18m, usd);
    }

    [Fact]
    public void CacheReads_AreTenPercent_CacheWrites_Are125Percent()
    {
        // Sonnet: 1M cache read = $0.30, 1M cache write = $3.75
        var read = ModelPricing.Estimate("anthropic", "claude-sonnet-4-6", 0, 0, cacheReadTokens: 1_000_000);
        var write = ModelPricing.Estimate("anthropic", "claude-sonnet-4-6", 0, 0, cacheCreationTokens: 1_000_000);
        Assert.Equal(0.3m, read);
        Assert.Equal(3.75m, write);
    }

    [Fact]
    public void Mock_IsFree()
    {
        Assert.Equal(0m, ModelPricing.Estimate("mock", "anything", 10_000, 10_000));
    }

    [Fact]
    public void UnknownModel_FallsBackToSonnetRates()
    {
        var (inRate, outRate) = ModelPricing.Resolve("claude-future-9");
        Assert.Equal(3m, inRate);
        Assert.Equal(15m, outRate);
    }
}
