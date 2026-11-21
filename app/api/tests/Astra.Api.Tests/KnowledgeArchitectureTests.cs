using System.Text.Json;
using Astra.Api.Docs;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>Rules about how the knowledge-capture code is written, and the one change it made to the shared writer seam.</summary>
public class KnowledgeArchitectureTests
{
    [Fact]
    public void NothingUnderKnowledge_TalksToAModelDirectly_EveryCallGoesThroughTheWriterSeam()
    {
        var folder = Path.Combine(SourceTree.ApiRoot(), "Knowledge");
        Assert.True(Directory.Exists(folder), "The Knowledge folder was not found, so this rule would pass by checking nothing.");

        var files = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        var forbidden = new[] { "HttpClient", "IHttpClientFactory", "AnthropicHttp", "SendWithRetryAsync", "api.anthropic.com" };
        var offenders = files
            .SelectMany(f => forbidden.Where(word => File.ReadAllText(f).Contains(word, StringComparison.Ordinal)).Select(word => $"{Path.GetFileName(f)}: {word}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Knowledge code must call models only through IDocWriter (so recording, replay, pricing and the rate limiter apply): " + string.Join("; ", offenders));
    }

    private static DocWriteRequest Request(double? temperature = null, string? replayKey = null, string? runMode = null) => new(
        "p", "v1", new[] { "system one", "system two" }, "hello", "tool", "does a thing",
        new { type = "object" }, "claude-test", 100, "cache", () => "{}",
        ReplayKey: replayKey, Temperature: temperature, RunMode: runMode);

    [Fact]
    public void AnAnthropicCall_SendsATemperatureOnlyWhenOneWasAskedFor()
    {
        using var without = JsonDocument.Parse(AnthropicDocWriter.BuildBodyJson(Request()));
        Assert.False(without.RootElement.TryGetProperty("temperature", out _));   // every other pipeline keeps the model's default

        using var zero = JsonDocument.Parse(AnthropicDocWriter.BuildBodyJson(Request(temperature: 0)));
        Assert.Equal(0d, zero.RootElement.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public void AnAnthropicCall_StillForcesTheToolAndCachesTheLastSystemBlock()
    {
        using var body = JsonDocument.Parse(AnthropicDocWriter.BuildBodyJson(Request(temperature: 0)));
        var root = body.RootElement;
        Assert.Equal("claude-test", root.GetProperty("model").GetString());
        Assert.Equal("tool", root.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal("tool", root.GetProperty("tool_choice").GetProperty("name").GetString());
        var system = root.GetProperty("system");
        Assert.False(system[0].TryGetProperty("cache_control", out _));
        Assert.Equal("ephemeral", system[1].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("hello", root.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void TheNewFieldsOfAWriteRequest_AreOptional_SoEveryExistingCallSiteIsUnchanged()
    {
        var old = new DocWriteRequest("p", "v1", new[] { "s" }, "m", "t", "d", new { }, "model", 1, "k", () => "{}");
        Assert.Null(old.ReplayKey);
        Assert.Null(old.Temperature);
        Assert.Null(old.RunMode);

        var set = Request(0, "step:item:write:1", "cached");
        Assert.Equal(("step:item:write:1", 0d, "cached"), (set.ReplayKey, set.Temperature, set.RunMode));
    }
}
