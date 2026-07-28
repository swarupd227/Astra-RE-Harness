using System.Diagnostics;
using System.Runtime.CompilerServices;
using Astra.Api.Llm.Archetypes;

namespace Astra.Api.Llm;

/// <summary>
/// Deterministic offline scaffold provider. Picks the right archetype
/// from the <see cref="ArchetypeRegistry"/> (target stack + subroutine
/// match) and streams its files in file_started → file_chunk → file_done
/// bursts so the demo Live Scaffold surface plays the same sequence the
/// Azure OpenAI adapter will produce in Phase B.4.x.
///
/// Phase #3c refactor: previously hard-coded against
/// <c>CanonicalScaffold</c>. Now archetype-agnostic — adding a new target
/// stack is a directory drop in <c>Llm/Archetypes/</c>.
/// </summary>
public sealed class MockScaffoldProvider : IScaffoldProvider
{
    private readonly ArchetypeRegistry _archetypes;
    private readonly ILogger<MockScaffoldProvider> _log;

    public MockScaffoldProvider(ArchetypeRegistry archetypes, ILogger<MockScaffoldProvider> log)
    {
        _archetypes = archetypes;
        _log = log;
    }

    public ProviderInfo Info { get; } = new(
        Name: "mock",
        Model: "mock-dotnet-scaffold-1",
        ConfigVersion: "mock:offline:no-network");

    public async IAsyncEnumerable<ExtractionEvent> GenerateAsync(
        ScaffoldRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        yield return Stage("priming", 1, "Loading scaffold template");
        await Task.Delay(220, ct);

        // ── Pick the archetype keyed by target stack + subroutine name ──
        var archetype = _archetypes.PickForSubroutine(request.TargetPlatform, request.SubroutineName, request.SourceSchema)
            ?? throw new InvalidOperationException(
                $"No archetype compatible with source schema '{request.SourceSchema}' is registered for target stack '{request.TargetPlatform}'. " +
                $"Check Llm/Archetypes/{request.TargetPlatform}/.");
        _log.LogInformation(
            "Mock scaffold using archetype {Target}/{Id} for subroutine {Sub}",
            archetype.Manifest.TargetStack, archetype.Manifest.Id, request.SubroutineName);

        yield return Stage("streaming", 2, $"Generating {request.TargetPlatform} package ({archetype.Manifest.Id})");
        await Task.Delay(150, ct);

        var emitted = new List<ArchetypeRegistry.LoadedFile>();
        var totalChars = 0;

        foreach (var file in archetype.Files)
        {
            ct.ThrowIfCancellationRequested();

            yield return new("file_started", new
            {
                path = file.Path,
                language = file.Language,
                derivedFrom = file.DerivedFromClaimIds,
            });

            await foreach (var chunk in StreamFileChunks(file.Content, ct))
            {
                yield return new("file_chunk", new
                {
                    path = file.Path,
                    content = chunk,
                });
            }

            yield return new("file_done", new
            {
                path = file.Path,
                lineCount = file.LineCount,
                todoCount = file.TodoCount,
                derivedFrom = file.DerivedFromClaimIds,
            });

            emitted.Add(file);
            totalChars += file.Content.Length;
            await Task.Delay(380, ct);
        }

        yield return Stage("validating", 3, "Static validation");
        await Task.Delay(280, ct);

        yield return Stage("committing", 4, "Persisting package + commit metadata");
        sw.Stop();

        // Manifest payload uses explicit lowerCamelCase keys so the persisted
        // JSON matches the spec/v1 conventions.
        var fileObjects = emitted.Select(f => (object?)new Dictionary<string, object?>
        {
            ["path"] = f.Path,
            ["language"] = f.Language,
            ["content"] = f.Content,
            ["lineCount"] = f.LineCount,
            ["todoCount"] = f.TodoCount,
            ["derivedFromClaimIds"] = f.DerivedFromClaimIds,
        }).ToList();

        yield return new("__final__", new Dictionary<string, object?>
        {
            ["files"] = fileObjects,
            ["inputTokens"] = (request.SignedSpecJson.Length / 4) + 256,
            ["outputTokens"] = totalChars / 4,
            ["latencyMs"] = sw.ElapsedMilliseconds,
            ["archetypeId"] = archetype.Manifest.Id,
        });
    }

    private static ExtractionEvent Stage(string stage, int step, string label) =>
        new("stage", new { stage, step, of = 4, label });

    private static async IAsyncEnumerable<string> StreamFileChunks(
        string content,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Stream by lines so the live surface gets a satisfying typewriter
        // effect while the file builds up — matches what an LLM streamed
        // from Azure OpenAI would feel like.
        var lines = content.Split('\n');
        var buf = "";
        var rng = new Random(7);
        for (int i = 0; i < lines.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            buf += lines[i] + (i < lines.Length - 1 ? "\n" : "");
            if (buf.Length >= rng.Next(40, 110) || i == lines.Length - 1)
            {
                yield return buf;
                buf = "";
                await Task.Delay(rng.Next(25, 70), ct);
            }
        }
        if (buf.Length > 0) yield return buf;
    }
}
