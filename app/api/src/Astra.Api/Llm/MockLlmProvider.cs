using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Astra.Api.Llm;

/// <summary>
/// Deterministic, offline LLM provider that streams the canonical CONSUME_ROLL
/// spec from <see cref="CanonicalSpec"/>. Used as the default in the local
/// Docker dev stack so the demo flow works without an external API key.
/// </summary>
public sealed class MockLlmProvider : ILlmProvider
{
    private const int SectionPauseMs = 220;
    private const int InvariantPauseMs = 700;
    private const int CitationPulsePauseMs = 280;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
    };

    public ProviderInfo Info { get; } = new(
        Name: "mock",
        Model: "mock-fortran-extract-1",
        ConfigVersion: "mock:offline:no-network");

    public async IAsyncEnumerable<ExtractionEvent> ExtractAsync(
        ExtractionRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        // Stage 1 — priming
        yield return Stage("priming", 1, "Loading prompt template");
        await Task.Delay(220, ct);

        // Stage 2 — loading source
        yield return Stage("loading_source", 2, $"Reading {request.SourcePath}");
        await Task.Delay(360, ct);

        // Stage 3 — streaming
        yield return Stage("streaming", 3, "Drafting behavioural specification");
        await Task.Delay(150, ct);

        // Header fields
        yield return Patch("/$schema", "https://nous.dev/schemas/re-harness/spec/v1.json");
        yield return Patch("/routine", request.SubroutineName);
        yield return Patch("/source_path", request.SourcePath);
        yield return Patch("/source_lines", $"1-{request.LineCount}");
        await Task.Delay(SectionPauseMs, ct);

        // Summary — streamed by characters so the UX feels like the model is
        // composing prose. Each token batch updates the same JSON path.
        yield return Patch("/summary", "");
        await foreach (var chunk in StreamTextChunks(CanonicalSpec.Summary, 7, 35, ct))
        {
            yield return Token(chunk, "/summary");
        }
        yield return Patch("/summary", CanonicalSpec.Summary);
        await Task.Delay(SectionPauseMs, ct);

        // Inputs / Outputs
        yield return Patch("/inputs", CanonicalSpec.Inputs);
        await Task.Delay(SectionPauseMs, ct);
        yield return Patch("/outputs", CanonicalSpec.Outputs);
        await Task.Delay(SectionPauseMs, ct);

        // Invariants — emitted one at a time, each followed by a citation
        // pulse on the source pane.
        yield return Patch("/invariants", Array.Empty<object>());
        for (int i = 0; i < CanonicalSpec.Invariants.Length; i++)
        {
            var inv = CanonicalSpec.Invariants[i];
            await Task.Delay(InvariantPauseMs, ct);
            yield return Patch($"/invariants/{i}", inv);
            yield return CitationPulse($"$.invariants[{i}]", inv.Citations[0].Lines);
            await Task.Delay(CitationPulsePauseMs, ct);
        }

        // Side effects + edge cases + open questions
        yield return Patch("/side_effects", Array.Empty<object>());
        for (int i = 0; i < CanonicalSpec.SideEffects.Length; i++)
        {
            var se = CanonicalSpec.SideEffects[i];
            await Task.Delay(420, ct);
            yield return Patch($"/side_effects/{i}", se);
            yield return CitationPulse($"$.side_effects[{i}]", se.Citations[0].Lines);
        }

        yield return Patch("/edge_cases", Array.Empty<object>());
        for (int i = 0; i < CanonicalSpec.EdgeCases.Length; i++)
        {
            var ec = CanonicalSpec.EdgeCases[i];
            await Task.Delay(420, ct);
            yield return Patch($"/edge_cases/{i}", ec);
            yield return CitationPulse($"$.edge_cases[{i}]", ec.Citations[0].Lines);
        }

        yield return Patch("/open_questions", Array.Empty<object>());
        for (int i = 0; i < CanonicalSpec.OpenQuestions.Length; i++)
        {
            await Task.Delay(420, ct);
            yield return Patch($"/open_questions/{i}", CanonicalSpec.OpenQuestions[i]);
        }

        // Stage 4 — validating
        await Task.Delay(420, ct);
        yield return Stage("validating", 4, "Schema + citation post-validation");
        await Task.Delay(280, ct);

        // Stage 5 — persisting
        yield return Stage("persisting", 5, "Writing draft spec to Postgres");
        // The pipeline owner emits `done` after persisting.
        stopwatch.Stop();

        // Provide the final payload so the pipeline can persist the spec.
        var finalSpec = BuildFinalSpec(request);
        yield return new ExtractionEvent("__final__", new Dictionary<string, object?>
        {
            ["specJson"] = finalSpec,
            ["inputTokens"] = EstimateInputTokens(request),
            ["outputTokens"] = EstimateOutputTokens(),
            ["latencyMs"] = stopwatch.ElapsedMilliseconds,
        });
    }

    private static ExtractionEvent Stage(string stage, int step, string label) =>
        new("stage", new { stage, step, of = 5, label });

    private static ExtractionEvent Patch(string path, object? value) =>
        new("patch", new { op = "add", path, value });

    private static ExtractionEvent Token(string text, string path) =>
        new("token", new { path, text });

    private static ExtractionEvent CitationPulse(string claimPath, string lines) =>
        new("citation_pulse", new { claimPath, lines });

    private static async IAsyncEnumerable<string> StreamTextChunks(
        string text,
        int minSize,
        int maxSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var rng = new Random(42);
        var i = 0;
        while (i < text.Length)
        {
            ct.ThrowIfCancellationRequested();
            var size = rng.Next(minSize, maxSize + 1);
            var end = Math.Min(i + size, text.Length);
            yield return text[i..end];
            i = end;
            await Task.Delay(rng.Next(20, 60), ct);
        }
    }

    private static object BuildFinalSpec(ExtractionRequest req) =>
        // Use a Dictionary so we get exact, canonical key casing in the persisted
        // spec_json. Anonymous-type property names normalize to PascalCase under
        // System.Text.Json's default options, which would break clients that
        // follow the spec/v1 schema (snake_case).
        new Dictionary<string, object?>
        {
            ["$schema"] = "https://nous.dev/schemas/re-harness/spec/v1.json",
            ["routine"] = req.SubroutineName,
            ["source_path"] = req.SourcePath,
            ["source_lines"] = $"1-{req.LineCount}",
            ["summary"] = CanonicalSpec.Summary,
            ["inputs"] = CanonicalSpec.Inputs.Select(i => new Dictionary<string, object?>
            {
                ["id"] = i.Id, ["name"] = i.Name, ["type"] = i.Type, ["semantic"] = i.Semantic,
                ["citations"] = i.Citations.Select(c => new Dictionary<string, object?> { ["lines"] = c.Lines }),
            }),
            ["outputs"] = CanonicalSpec.Outputs.Select(o => new Dictionary<string, object?>
            {
                ["id"] = o.Id, ["name"] = o.Name, ["type"] = o.Type, ["semantic"] = o.Semantic,
                ["citations"] = o.Citations.Select(c => new Dictionary<string, object?> { ["lines"] = c.Lines }),
            }),
            ["invariants"] = CanonicalSpec.Invariants.Select(inv => new Dictionary<string, object?>
            {
                ["id"] = inv.Id, ["claim"] = inv.Claim,
                ["citations"] = inv.Citations.Select(c => new Dictionary<string, object?> { ["lines"] = c.Lines }),
                ["confidence"] = inv.Confidence,
            }),
            ["side_effects"] = CanonicalSpec.SideEffects.Select(se => new Dictionary<string, object?>
            {
                ["id"] = se.Id,
                ["description"] = se.Description,
                ["citations"] = se.Citations.Select(c => new Dictionary<string, object?> { ["lines"] = c.Lines }),
            }),
            ["edge_cases"] = CanonicalSpec.EdgeCases.Select(ec => new Dictionary<string, object?>
            {
                ["id"] = ec.Id,
                ["description"] = ec.Description,
                ["citations"] = ec.Citations.Select(c => new Dictionary<string, object?> { ["lines"] = c.Lines }),
                ["behavior"] = ec.Behavior,
                ["confidence"] = ec.Confidence,
            }),
            ["open_questions"] = CanonicalSpec.OpenQuestions.Select(q => new Dictionary<string, object?>
            {
                ["id"] = q.Id, ["question"] = q.Question, ["status"] = q.Status,
            }),
            ["metadata"] = new Dictionary<string, object?>
            {
                ["prompt_template"] = $"{req.PromptTemplateId}@{req.PromptTemplateVersion}",
                ["provider"] = "mock",
            },
        };

    private static int EstimateInputTokens(ExtractionRequest req) =>
        // ~4 chars per token; rounded up. The mock isn't actually tokenising —
        // this is just a plausible figure for the audit row.
        (req.SourceText.Length / 4) + 320;

    private static int EstimateOutputTokens() =>
        (CanonicalSpec.Summary.Length
         + CanonicalSpec.Invariants.Sum(i => i.Claim.Length)
         + CanonicalSpec.SideEffects.Sum(s => s.Description.Length)
         + CanonicalSpec.EdgeCases.Sum(e => e.Description.Length + e.Behavior.Length)
         + CanonicalSpec.OpenQuestions.Sum(q => q.Question.Length)) / 4;

    public static string SerializeForHttp(object obj) => JsonSerializer.Serialize(obj, JsonOpts);
}
