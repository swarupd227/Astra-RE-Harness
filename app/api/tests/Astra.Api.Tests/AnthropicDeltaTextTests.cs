using System.Text.Json;
using Astra.Api.Llm;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// The extraction stream reads both delta shapes Anthropic sends: `text`
/// for a text block and `partial_json` for the input of a forced tool
/// call. Missing either one would silently produce an empty spec.
/// </summary>
public class AnthropicDeltaTextTests
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Text_delta_is_read()
        => Assert.Equal("{\"purpose\":", AnthropicLlmProvider.DeltaText(El("{\"type\":\"text_delta\",\"text\":\"{\\\"purpose\\\":\"}")));

    [Fact]
    public void Tool_input_delta_is_read()
        => Assert.Equal("\"sets \\\"Content-Type\\\"\"", AnthropicLlmProvider.DeltaText(El("{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"sets \\\\\\\"Content-Type\\\\\\\"\\\"\"}")));

    [Fact]
    public void Other_deltas_yield_nothing()
    {
        Assert.Null(AnthropicLlmProvider.DeltaText(El("{\"type\":\"signature_delta\",\"signature\":\"abc\"}")));
        Assert.Null(AnthropicLlmProvider.DeltaText(El("{\"text\":null}")));
    }
}
