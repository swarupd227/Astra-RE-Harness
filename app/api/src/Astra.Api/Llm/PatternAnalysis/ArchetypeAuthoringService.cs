using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Astra.Api.Llm.Archetypes;
using Astra.Api.Llm.Prompts;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Astra.Api.Llm.PatternAnalysis;

/// <summary>
/// Phase 14.0 — proposes a NEW archetype from a <see cref="PatternCluster"/>
/// that has no matching archetype yet, instead of requiring a human to
/// hand-author one on disk. Grounds the proposal in the cluster's actual
/// member specs plus an existing hand-built archetype as a house-style
/// reference (mirrors <see cref="AnthropicScaffoldProvider"/>'s
/// reference-plus-specifics pattern, one level up: here the "specifics"
/// are a whole cluster's worth of claims, not one routine's).
///
/// Every proposal is auto-verified (real `mvn -o test-compile` + `mvn
/// test` via the maven-sidecar) before a human ever sees it — a proposal
/// that doesn't compile or whose tests fail is never presented as
/// approvable. Only a human's explicit approval moves it to PRODUCTION
/// and registers it live in <see cref="ArchetypeRegistry"/> — no code
/// change, no container restart.
/// </summary>
public sealed class ArchetypeAuthoringService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AppDbContext _db;
    private readonly PromptLibrary _prompts;
    private readonly ArchetypeRegistry _archetypes;
    private readonly AnthropicOptions _opts;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MavenClient _maven;
    private readonly ILogger<ArchetypeAuthoringService> _logger;

    public ArchetypeAuthoringService(
        AppDbContext db,
        PromptLibrary prompts,
        ArchetypeRegistry archetypes,
        IOptions<AnthropicOptions> opts,
        IHttpClientFactory httpFactory,
        MavenClient maven,
        ILogger<ArchetypeAuthoringService> logger)
    {
        _db = db;
        _prompts = prompts;
        _archetypes = archetypes;
        _opts = opts.Value;
        _httpFactory = httpFactory;
        _maven = maven;
        _logger = logger;
    }

    public async Task<ArchetypeProposal> ProposeAsync(Guid patternClusterId, string? triggeredBy, CancellationToken ct)
    {
        var cluster = await _db.PatternClusters.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == patternClusterId, ct)
            ?? throw new InvalidOperationException($"Pattern cluster {patternClusterId} not found.");

        var members = ParseMembers(cluster.MemberSubroutineIdsJson);
        if (members.Count == 0)
            throw new InvalidOperationException($"Pattern cluster {patternClusterId} has no members.");

        var specIds = members.Where(m => m.SpecId is not null).Select(m => m.SpecId!.Value).ToList();
        var specs = await _db.Specs.AsNoTracking()
            .Include(s => s.Subroutine)
            .Where(s => specIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        // Phase 15.0.a — every member of a cluster comes from the same
        // pattern-analysis run, hence the same corpus/source language.
        // Stamped onto the proposal so the live-registered archetype
        // declares the RIGHT compatibleSchemas instead of a hardcoded one.
        var sourceSchema = specs.Values.Select(s => s.Subroutine?.SourceLanguage).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";

        const string targetStack = "java-spring";
        var reference = _archetypes.All()
            .FirstOrDefault(a => string.Equals(a.Manifest.TargetStack, targetStack, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.Manifest.Status, "production", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No production archetype registered for '{targetStack}' to use as a house-style reference.");

        var memberEntries = members.Select(m => new
        {
            subroutineId = m.SubroutineId,
            subroutineName = m.SubroutineName,
            spec = specs.TryGetValue(m.SpecId ?? Guid.Empty, out var sp) ? sp.SpecJson.RootElement : (object?)null,
        }).ToList();
        var memberSpecsJson = JsonSerializer.Serialize(memberEntries, JsonOpts);

        var referenceFilesJson = JsonSerializer.Serialize(
            reference.Files.Select(f => new { path = f.Path, language = f.Language, content = f.Content }),
            JsonOpts);

        var loaded = _prompts.GetLatest("common", targetStack, "propose-archetype")
            ?? throw new InvalidOperationException(
                $"No propose-archetype prompt registered (common/{targetStack}/propose-archetype).");

        var rendered = _prompts.Render(loaded, new Dictionary<string, string?>
        {
            ["clusterLabel"] = cluster.Label,
            ["claimKindSignature"] = cluster.ClaimKindSignature,
            ["clusterRationale"] = cluster.Rationale,
            ["suggestedArchetypeName"] = cluster.SuggestedArchetypeName,
            ["memberSpecsJson"] = memberSpecsJson,
            ["referenceArchetypeId"] = reference.Manifest.Id,
            ["referenceFilesJson"] = referenceFilesJson,
        });

        var (rawJson, inputTokens, outputTokens) = await CallAnthropicAsync(rendered.System, rendered.User, ct);
        var parsed = ParseProposal(rawJson)
            ?? throw new InvalidOperationException("The model's response did not contain a parseable archetype proposal.");

        var now = DateTimeOffset.UtcNow;
        var proposal = new ArchetypeProposal
        {
            Id = Guid.NewGuid(),
            PatternClusterId = cluster.Id,
            CorpusId = cluster.CorpusId,
            TargetStack = targetStack,
            SourceSchema = sourceSchema,
            ProposedArchetypeId = parsed.Id,
            DisplayName = parsed.DisplayName,
            Description = parsed.Description,
            MatchesJson = JsonSerializer.Serialize(parsed.MatchesSubroutineNames),
            FilesJson = JsonSerializer.Serialize(parsed.Files, JsonOpts),
            State = "DRAFT",
            GeneratedBy = triggeredBy,
            CreatedAt = now,
        };
        _db.ArchetypeProposals.Add(proposal);
        await _db.SaveChangesAsync(ct);

        await VerifyAsync(proposal, ct);
        return proposal;
    }

    /// <summary>Runs the real maven-sidecar compile+test pass against a
    /// proposal's files and updates its state in place. Never presents an
    /// unverified proposal as ready for human review.</summary>
    private async Task VerifyAsync(ArchetypeProposal proposal, CancellationToken ct)
    {
        var files = JsonSerializer.Deserialize<List<ProposedFile>>(proposal.FilesJson, JsonOpts) ?? new();
        var sources = files.Select(f => new MavenClient.JavaSource(f.Path, f.Content)).ToList();

        try
        {
            if (!await _maven.PingAsync(ct))
                throw new InvalidOperationException("maven sidecar unreachable (GET /health failed).");

            var result = await _maven.CompileAndTestAsync(new MavenClient.CompileAndTestRequest(sources), ct);
            proposal.CompileLog = Truncate(result.Compile.Log, 8000);
            proposal.CompileErrorCount = result.Compile.ErrorCount;

            if (result.Compile.ExitCode != 0)
            {
                proposal.State = "VERIFICATION_FAILED";
            }
            else if (result.Test is null)
            {
                // Compiles but no test stage ran (shouldn't happen for a
                // proposal that always includes a test file, but don't
                // silently call it verified if it does).
                proposal.State = "VERIFICATION_FAILED";
            }
            else
            {
                proposal.TestCount = result.Test.Tests;
                proposal.TestFailureCount = result.Test.Failures + result.Test.Errors;
                proposal.State = (result.Test.ExitCode == 0 && result.Test.Failures == 0 && result.Test.Errors == 0)
                    ? "VERIFIED"
                    : "VERIFICATION_FAILED";
                proposal.CompileLog = Truncate(
                    proposal.CompileLog + "\n\n=== test stdout ===\n" + result.Test.Stdout, 8000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Archetype proposal {Id} verification crashed", proposal.Id);
            proposal.State = "VERIFICATION_FAILED";
            proposal.CompileLog = Truncate($"Verification runner error: {ex.Message}", 8000);
        }

        proposal.VerifiedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<ArchetypeProposal> ApproveAsync(Guid proposalId, string approvedBy, CancellationToken ct)
    {
        var proposal = await _db.ArchetypeProposals.FirstOrDefaultAsync(p => p.Id == proposalId, ct)
            ?? throw new InvalidOperationException($"Archetype proposal {proposalId} not found.");
        if (proposal.State != "VERIFIED")
            throw new InvalidOperationException(
                $"Cannot approve a proposal in state {proposal.State} — only VERIFIED proposals can be approved.");

        proposal.State = "PRODUCTION";
        proposal.ApprovedBy = approvedBy;
        proposal.DecidedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        _archetypes.RegisterLive(BuildLoadedArchetype(proposal));
        _logger.LogInformation(
            "Archetype proposal {Id} approved by {By} — {ArchetypeId} now live", proposal.Id, approvedBy, proposal.ProposedArchetypeId);
        return proposal;
    }

    public async Task<ArchetypeProposal> RejectAsync(Guid proposalId, string reason, CancellationToken ct)
    {
        var proposal = await _db.ArchetypeProposals.FirstOrDefaultAsync(p => p.Id == proposalId, ct)
            ?? throw new InvalidOperationException($"Archetype proposal {proposalId} not found.");
        proposal.State = "REJECTED";
        proposal.RejectedReason = reason;
        proposal.DecidedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return proposal;
    }

    /// <summary>Rebuilds the in-memory <see cref="ArchetypeRegistry.LoadedArchetype"/>
    /// shape from a persisted PRODUCTION proposal — used both when approving
    /// live and when reloading every PRODUCTION proposal at boot.</summary>
    public static ArchetypeRegistry.LoadedArchetype BuildLoadedArchetype(ArchetypeProposal proposal)
    {
        var files = JsonSerializer.Deserialize<List<ProposedFile>>(proposal.FilesJson, JsonOpts) ?? new();
        var matches = JsonSerializer.Deserialize<List<string>>(proposal.MatchesJson, JsonOpts) ?? new();

        var manifest = new ArchetypeRegistry.ArchetypeManifest
        {
            Id = proposal.ProposedArchetypeId,
            TargetStack = proposal.TargetStack,
            DisplayName = proposal.DisplayName,
            Description = proposal.Description,
            CompatibleSchemas = string.IsNullOrWhiteSpace(proposal.SourceSchema)
                ? new List<string>()
                : new List<string> { proposal.SourceSchema },
            Status = "production",
            Owner = "Nous · live-authored (Phase 14.0)",
            Matches = new ArchetypeRegistry.MatchRule
            {
                AnyOf = matches.Select(m => new ArchetypeRegistry.MatchClause { SubroutineName = m }).ToList(),
                Fallback = $"Live-authored from pattern cluster \"{proposal.ProposedArchetypeId}\".",
            },
        };
        var loadedFiles = files.Select(f => new ArchetypeRegistry.LoadedFile
        {
            Path = f.Path,
            Language = f.Language,
            Content = f.Content,
            DerivedFromClaimIds = Array.Empty<string>(),
        }).ToList();

        return new ArchetypeRegistry.LoadedArchetype
        {
            Manifest = manifest,
            ArchetypeDir = $"(db:archetype_proposals/{proposal.Id})",
            Files = loadedFiles,
        };
    }

    private async Task<(string RawJson, int InputTokens, int OutputTokens)> CallAnthropicAsync(
        string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient("anthropic-propose-archetype");
        http.Timeout = TimeSpan.FromMinutes(10);

        var requestBody = new Dictionary<string, object?>
        {
            ["model"] = _opts.Model,
            // A whole new archetype (pom.xml + several main classes + a full
            // test class) is a bigger ask than customizing one existing
            // archetype (Phase 13.0) or clustering (Phase 12.0) — both of
            // which are fine at the shared default. Give this call more
            // headroom so a real multi-file package doesn't get truncated
            // mid-JSON. claude-sonnet-4-5 allows up to 64k.
            ["max_tokens"] = Math.Max(_opts.MaxOutputTokens, 32000),
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

        using var resp = await http.SendAsync(req, ct);
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

    private sealed record ProposedFile(string Path, string Language, string Content);

    private sealed record ParsedProposal(
        string Id, string DisplayName, string Description,
        List<string> MatchesSubroutineNames, List<ProposedFile> Files);

    private static ParsedProposal? ParseProposal(string rawJson)
    {
        var json = ExtractFirstJsonObject(rawJson);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var id = ReadString(root, "id", "");
            if (string.IsNullOrWhiteSpace(id)) return null;

            var matches = new List<string>();
            if (root.TryGetProperty("matchesSubroutineNames", out var m) && m.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in m.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String) matches.Add(el.GetString() ?? "");
            }

            var files = new List<ProposedFile>();
            if (root.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var path = ReadString(item, "path", "");
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    files.Add(new ProposedFile(path, ReadString(item, "language", "java"), ReadString(item, "content", "")));
                }
            }
            if (files.Count == 0) return null;

            return new ParsedProposal(
                id, ReadString(root, "displayName", id), ReadString(root, "description", ""), matches, files);
        }
        catch
        {
            return null;
        }
    }

    private sealed record MemberEntry(Guid SubroutineId, string SubroutineName, Guid? SpecId);

    private static List<MemberEntry> ParseMembers(string json)
    {
        var result = new List<MemberEntry>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!el.TryGetProperty("subroutineId", out var idEl) || !Guid.TryParse(idEl.GetString(), out var subId))
                    continue;
                var name = el.TryGetProperty("subroutineName", out var n) ? n.GetString() ?? "" : "";
                Guid? specId = el.TryGetProperty("specId", out var sEl) && sEl.ValueKind == JsonValueKind.String
                    && Guid.TryParse(sEl.GetString(), out var sid) ? sid : null;
                result.Add(new MemberEntry(subId, name, specId));
            }
        }
        catch { /* best effort */ }
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
}
