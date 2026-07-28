using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Astra.Api.Llm.Archetypes;
using Astra.Api.Llm.Prompts;
using Microsoft.Extensions.Options;

namespace Astra.Api.Llm;

/// <summary>
/// Phase 13.0 — real per-routine scaffold generation. Unlike
/// <see cref="MockScaffoldProvider"/> (which streams a matched archetype's
/// static files unchanged, ignoring which routine triggered it), this
/// provider sends the archetype's verified reference files PLUS the
/// specific routine's signed spec to Claude in one call, and asks for a
/// customized package — same file layout and class structure (so it still
/// compiles against the reference's pom.xml/tests), but reflecting the
/// real field names, literals, and specifics from the actual routine.
///
/// <see cref="ScaffoldRequest.SignedSpecJson"/> already carried the full
/// spec through the pipeline before this class existed — the interface
/// was built for this from the start (see the doc comment on
/// <see cref="IScaffoldProvider"/>); only the real implementation was
/// missing.
/// </summary>
public sealed class AnthropicScaffoldProvider : IScaffoldProvider
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ArchetypeRegistry _archetypes;
    private readonly PromptLibrary _prompts;
    private readonly AnthropicOptions _opts;
    private readonly HttpClient _http;
    private readonly ILogger<AnthropicScaffoldProvider> _logger;

    public AnthropicScaffoldProvider(
        ArchetypeRegistry archetypes,
        PromptLibrary prompts,
        IOptions<AnthropicOptions> opts,
        IHttpClientFactory httpFactory,
        ILogger<AnthropicScaffoldProvider> logger)
    {
        _archetypes = archetypes;
        _prompts = prompts;
        _opts = opts.Value;
        _http = httpFactory.CreateClient("anthropic-scaffold-generate");
        _http.Timeout = TimeSpan.FromMinutes(10);
        _logger = logger;
    }

    public ProviderInfo Info => new(
        Name: "anthropic",
        Model: _opts.Model,
        ConfigVersion: _opts.ConfigVersion);

    public async IAsyncEnumerable<ExtractionEvent> GenerateAsync(
        ScaffoldRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        yield return Stage("priming", 1, "Loading matched archetype");

        var archetype = _archetypes.PickForSubroutine(request.TargetPlatform, request.SubroutineName, request.SourceSchema)
            ?? throw new InvalidOperationException(
                $"No archetype compatible with source schema '{request.SourceSchema}' is registered for target stack '{request.TargetPlatform}'. " +
                $"Check Llm/Archetypes/{request.TargetPlatform}/.");

        yield return new("provider_info", new
        {
            name = Info.Name,
            model = Info.Model,
            configVersion = Info.ConfigVersion,
            promptTemplateId = "scaffold-generate",
            targetPlatform = request.TargetPlatform,
            archetypeId = archetype.Manifest.Id,
        });

        yield return Stage("streaming", 2,
            $"Customizing {request.TargetPlatform} package from {archetype.Manifest.Id} for {request.SubroutineName}");

        var loaded = _prompts.GetLatest("common", request.TargetPlatform, "scaffold-generate")
            ?? throw new InvalidOperationException(
                $"No scaffold-generate prompt registered (common/{request.TargetPlatform}/scaffold-generate).");

        var referenceFilesJson = JsonSerializer.Serialize(
            archetype.Files.Select(f => new { path = f.Path, language = f.Language, content = f.Content }),
            JsonOpts);

        var rendered = _prompts.Render(loaded, new Dictionary<string, string?>
        {
            ["subroutineName"] = request.SubroutineName,
            ["sourcePath"] = request.SourcePath,
            ["archetypeId"] = archetype.Manifest.Id,
            ["archetypeDescription"] = archetype.Manifest.Description,
            ["referenceFilesJson"] = referenceFilesJson,
            ["signedSpecJson"] = request.SignedSpecJson,
        });

        string? rawJson = null;
        int inputTokens = 0, outputTokens = 0;
        string? transportError = null;
        try
        {
            (rawJson, inputTokens, outputTokens) = await CallAnthropicAsync(rendered.System, rendered.User, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scaffold generation failed for spec {Spec}", request.SpecId);
            transportError = ex.Message;
        }

        if (transportError is not null || rawJson is null)
        {
            yield return new("error", new
            {
                code = "provider.scaffold_generation_failed",
                message = transportError ?? "No response",
                retryable = true,
            });
            yield break;
        }

        yield return Stage("validating", 3, "Parsing generated package");

        var generatedFiles = ParseFiles(rawJson, archetype);
        if (generatedFiles.Count == 0)
        {
            yield return new("error", new
            {
                code = "provider.no_files_generated",
                message = "The model's response did not contain a parseable files array.",
                retryable = true,
            });
            yield break;
        }

        yield return Stage("committing", 4, "Persisting package + commit metadata");

        var totalChars = 0;
        foreach (var file in generatedFiles)
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
                yield return new("file_chunk", new { path = file.Path, content = chunk });
            }

            yield return new("file_done", new
            {
                path = file.Path,
                lineCount = file.LineCount,
                todoCount = file.TodoCount,
                derivedFrom = file.DerivedFromClaimIds,
            });
            totalChars += file.Content.Length;
        }

        var fileObjects = generatedFiles.Select(f => (object?)new Dictionary<string, object?>
        {
            ["path"] = f.Path,
            ["language"] = f.Language,
            ["content"] = f.Content,
            ["lineCount"] = f.LineCount,
            ["todoCount"] = f.TodoCount,
            ["derivedFromClaimIds"] = f.DerivedFromClaimIds,
        }).ToList();

        sw.Stop();
        yield return new("__final__", new Dictionary<string, object?>
        {
            ["files"] = fileObjects,
            ["inputTokens"] = inputTokens,
            ["outputTokens"] = outputTokens,
            ["latencyMs"] = sw.ElapsedMilliseconds,
            ["archetypeId"] = archetype.Manifest.Id,
        });

        _logger.LogInformation(
            "Real scaffold generation for spec {Spec}: {Files} files, archetype {Archetype}, {In}/{Out} tokens",
            request.SpecId, generatedFiles.Count, archetype.Manifest.Id, inputTokens, outputTokens);
    }

    private async Task<(string RawJson, int InputTokens, int OutputTokens)> CallAnthropicAsync(
        string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = _opts.Model,
            ["max_tokens"] = _opts.MaxOutputTokens,
            ["system"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "text",
                    ["text"] = systemPrompt,
                    ["cache_control"] = new { type = "ephemeral" },
                },
            },
            ["messages"] = new[]
            {
                new Dictionary<string, object?> { ["role"] = "user", ["content"] = userPrompt },
            },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_opts.BaseUrl}/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", _opts.ApiKey);
        req.Headers.Add("anthropic-version", _opts.ApiVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Anthropic returned {(int)resp.StatusCode}: {Truncate(body, 400)}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        int inT = 0, outT = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var i)) inT = i.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var o)) outT = o.GetInt32();
        }

        var sb = new StringBuilder();
        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var txt))
                {
                    sb.Append(txt.GetString());
                }
            }
        }
        return (sb.ToString(), inT, outT);
    }

    private sealed record GeneratedFile(string Path, string Language, string Content, string[] DerivedFromClaimIds)
    {
        public int LineCount => Content.Count(c => c == '\n') + 1;
        public int TodoCount
        {
            get
            {
                int n = 0, idx = 0;
                while ((idx = Content.IndexOf("TODO", idx, StringComparison.Ordinal)) >= 0) { n++; idx += 4; }
                return n;
            }
        }
    }

    /// <summary>
    /// Parse the model's files array, defensively (markdown fences / prose
    /// wrapping tolerated via brace-depth scan, same pattern as
    /// HarmonisationPipeline / PatternAnalysisOrchestrator). Falls back to
    /// the reference archetype's own claim-id mapping per path so a
    /// customized file keeps its provenance even if the model omits it.
    /// </summary>
    private static List<GeneratedFile> ParseFiles(string rawJson, ArchetypeRegistry.LoadedArchetype archetype)
    {
        var claimsByPath = archetype.Files.ToDictionary(f => f.Path, f => f.DerivedFromClaimIds);
        var result = new List<GeneratedFile>();

        var json = ExtractFirstJsonObject(rawJson);
        if (json is null) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("files", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var path = ReadString(item, "path", "");
                if (string.IsNullOrWhiteSpace(path)) continue;
                var language = ReadString(item, "language", "java");
                var content = ReadString(item, "content", "");
                var claims = claimsByPath.TryGetValue(path, out var c) ? c : Array.Empty<string>();
                result.Add(new GeneratedFile(path, language, content, claims));
            }
        }
        catch
        {
            // Leave whatever parsed successfully before the failure.
        }
        return result;
    }

    private static string? ExtractFirstJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int start = text.IndexOf('{');
        if (start < 0) return null;
        int depth = 0;
        bool inString = false;
        bool escape = false;
        for (int i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    private static string ReadString(JsonElement e, string prop, string fallback) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? fallback)
            : fallback;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static ExtractionEvent Stage(string stage, int step, string label) =>
        new("stage", new { stage, step, of = 4, label });

    private static async IAsyncEnumerable<string> StreamFileChunks(
        string content, [EnumeratorCancellation] CancellationToken ct)
    {
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
            }
        }
        if (buf.Length > 0) yield return buf;
        await Task.CompletedTask;
    }
}
