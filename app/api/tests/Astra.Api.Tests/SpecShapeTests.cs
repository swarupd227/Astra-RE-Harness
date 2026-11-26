using System.Text.Json;
using Astra.Api.Llm;
using Astra.Api.Llm.Schemas;
using Xunit;

namespace Astra.Api.Tests;

public class SpecShapeTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private const string Body = """
        "routine": "get_customer", "summary": "Reads one customer.",
        "invariants": [ { "id": "INV-1", "claim": "The id is cast with intval." } ],
        "edge_cases": [ { "id": "EDGE-1", "description": "A missing id reads row 0." } ]
        """;

    private static int Count(JsonElement spec, string section) => spec.GetProperty(section).GetArrayLength();

    [Fact]
    public void TheWholeSpecNestedUnderAdditionalProperties_IsLiftedToTheTop()
    {
        var reply = Json($$"""{ "additionalProperties": { "$schema": "spec/v1", {{Body}} } }""");
        Assert.False(SpecShape.HasClaims(reply)); // what was stored before: nothing to review

        var spec = SpecShape.Unwrap(reply);

        Assert.True(SpecShape.HasClaims(spec));
        Assert.Equal("get_customer", spec.GetProperty("routine").GetString());
        Assert.Equal(1, Count(spec, "invariants"));
        Assert.False(spec.TryGetProperty("additionalProperties", out _));
    }

    [Theory]
    [InlineData("""{ "spec": { BODY } }""")]
    [InlineData("""{ "additionalProperties": { "additionalProperties": { BODY } } }""")]
    [InlineData("""{ "$schema": "spec/v1", "additionalProperties": { BODY } }""")]
    [InlineData("""{ "$schema": "spec/v1", "spec": { BODY } }""")]
    [InlineData("""{ "routine": "get_customer", "summary": "Reads one customer.", "additionalProperties": { BODY } }""")]
    public void OtherWaysOfNesting_AreLiftedToo(string shape)
    {
        var spec = SpecShape.Unwrap(Json(shape.Replace("BODY", Body)));

        Assert.Equal("get_customer", spec.GetProperty("routine").GetString());
        Assert.Equal(1, Count(spec, "invariants"));
        Assert.Equal(1, Count(spec, "edge_cases"));
    }

    [Theory]
    [InlineData("spec")]
    [InlineData("additionalProperties")]
    [InlineData("result")]
    public void ASpecWrittenOutAsTextUnderAWrapper_IsReadAndLifted(string key)
    {
        var reply = JsonSerializer.SerializeToElement(new Dictionary<string, object> { [key] = "\n{ " + Body + " }\n" });

        var spec = SpecShape.Unwrap(reply);

        Assert.True(SpecShape.HasClaims(spec));
        Assert.Equal("get_customer", spec.GetProperty("routine").GetString());
        Assert.False(spec.TryGetProperty(key, out _));
    }

    [Fact]
    public void TextThatIsNotValidJson_IsNotRepaired_AndCountsAsNoClaims()
    {
        // as seen: a citation written {"lines": "1964", "1980"}
        var text = """{ "routine": "x", "invariants": [ { "id": "INV-1", "citations": [{"lines": "1964", "1980"}] } ] }""";
        var reply = JsonSerializer.SerializeToElement(new { spec = text });

        var spec = SpecShape.Unwrap(reply);

        Assert.Equal(reply.GetRawText(), spec.GetRawText());
        Assert.False(SpecShape.HasClaims(spec));
    }

    [Theory]
    // a spec with claims is never rewritten, whatever else it carries
    [InlineData("""{ "invariants": [ { "id": "INV-1" } ], "additionalProperties": { "invariants": [] } }""")]
    // an object that is not a spec is not mistaken for one
    [InlineData("""{ "error": { "message": "I cannot read this file." } }""")]
    [InlineData("""{ "routine": "x", "summary": "y", "invariants": [] }""")]
    [InlineData("""{ "additionalProperties": true }""")]
    public void AnythingElse_IsLeftAsItIs(string text)
    {
        Assert.Equal(Json(text).GetRawText(), SpecShape.Unwrap(Json(text)).GetRawText());
    }

    [Theory]
    [InlineData("""{ "routine": "x", "summary": "y" }""", false)]
    [InlineData("""{ "invariants": [], "side_effects": [], "edge_cases": [], "open_questions": [] }""", false)]
    [InlineData("""{ "inputs": [ { "id": "in.x" } ], "outputs": [ { "id": "out" } ] }""", false)]
    [InlineData("""{ "invariants": "none" }""", false)]
    [InlineData("""[ { "id": "INV-1" } ]""", false)]
    [InlineData("""{ "open_questions": [ { "id": "Q-1" } ] }""", true)]
    [InlineData("""{ "edgeCases": [ { "id": "EC-1" } ] }""", true)]
    public void AClaimIsAnEntryInAClaimSection(string text, bool expected)
    {
        Assert.Equal(expected, SpecShape.HasClaims(Json(text)));
    }

    [Fact]
    public void ASectionTheLanguageDeclares_CountsAsClaims()
    {
        var spec = Json("""{ "http_contracts": [ { "id": "HTTP-1" } ] }""");
        var php = new SpecSchema { Id = "php", ClaimKinds = { new ClaimKindDefinition { Id = "httpContract", SpecJsonField = "http_contracts" } } };

        Assert.False(SpecShape.HasClaims(spec));
        Assert.True(SpecShape.HasClaims(spec, php));
    }
}
