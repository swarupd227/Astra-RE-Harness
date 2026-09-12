using System.Text.Json;
using Astra.Api.Conversations;
using Astra.Api.Endpoints;
using Astra.Api.Llm.Schemas;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Copilot;

/// <summary>
/// Builds the artifact cards (contract §3) from persisted state. Shared by
/// the tool registry (a tool result) and the Narrator (a run finished) so a
/// card looks the same however it reached the thread.
/// </summary>
public static class ArtifactBuilders
{
    public const int SourceLineCap = 400;

    // ── funnel ───────────────────────────────────────────────────────────

    public static async Task<(ArtifactDto Artifact, object Payload)> FunnelAsync(
        AppDbContext db, ConversationService conversations, Corpus corpus, CancellationToken ct)
    {
        var counts = await conversations.FunnelAsync(corpus, ct);
        var (_, _, language) = await conversations.ProgrammeStatsAsync(corpus, ct);

        object? digests = null;
        int? clusters = null;
        LatestRunDto? latestRun = null;
        if (corpus.LatestVersionId is { } vid)
        {
            var digestRows = await db.RoutineDigests.AsNoTracking()
                .Where(d => d.SourceVersionId == vid)
                .GroupBy(d => d.Source)
                .Select(g => new { Source = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            if (digestRows.Count > 0)
            {
                int of(string s) => digestRows.FirstOrDefault(r => r.Source == s)?.Count ?? 0;
                digests = new
                {
                    total = digestRows.Sum(r => r.Count),
                    surveyed = of("survey") + of("spec"),
                    propagated = of("propagated"),
                    trivial = of("trivial"),
                };
            }
        }

        var run = await db.PatternAnalysisRuns.AsNoTracking()
            .Where(r => r.CorpusId == corpus.Id)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (run is not null)
        {
            latestRun = new LatestRunDto(run.Id, "pattern-analysis", run.State, run.StartedAt, run.Summary);
            if (run.State is "SUCCEEDED" or "PARTIAL")
                clusters = await db.PatternClusters.AsNoTracking().CountAsync(c => c.PatternAnalysisRunId == run.Id, ct);
        }

        var props = new
        {
            corpusName = corpus.Name,
            sourceLanguage = language,
            counts,
            digests,
            clusters,
            latestRun,
        };
        var payload = new
        {
            corpusId = corpus.Id,
            corpusName = corpus.Name,
            sourceLanguage = language,
            languageLabel = ConversationService.LanguageLabel(language),
            counts,
            digests,
            clusters,
            latestRun,
        };
        return (ToolResult.Artifact("funnel", corpus.Id.ToString(), props), payload);
    }

    // ── routine ──────────────────────────────────────────────────────────

    public static async Task<(ArtifactDto Artifact, object Payload)> RoutineAsync(
        AppDbContext db, IBlobClient blob, Subroutine sub, bool includeSource, CancellationToken ct)
    {
        var file = sub.SourceFile ?? await db.SourceFiles.AsNoTracking().FirstAsync(f => f.Id == sub.SourceFileId, ct);
        var version = await db.SourceVersions.AsNoTracking().FirstAsync(v => v.Id == file.SourceVersionId, ct);
        var spec = await db.Specs.AsNoTracking()
            .Where(s => s.SubroutineId == sub.Id)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var callees = ReadStringArray(sub.CalledSubroutines);
        // Callers = routines in the same version whose called_subroutines jsonb
        // contains this name (parsers store either ["Name"] or [{"name":"Name"}]).
        var asString = JsonSerializer.Serialize(new[] { sub.Name });
        var asObject = JsonSerializer.Serialize(new[] { new { name = sub.Name } });
        var callerCount = await db.Subroutines.AsNoTracking()
            .Where(s => s.SourceFile!.SourceVersionId == file.SourceVersionId && s.Id != sub.Id
                        && s.CalledSubroutines != null
                        && (EF.Functions.JsonContains(s.CalledSubroutines!, asString)
                            || EF.Functions.JsonContains(s.CalledSubroutines!, asObject)))
            .CountAsync(ct);

        string? source = null;
        var truncated = false;
        if (includeSource)
        {
            try
            {
                var text = await blob.GetTextAsync(file.BlobUri, ct);
                var lines = text.Replace("\r\n", "\n").Split('\n');
                var start = Math.Max(0, sub.LineStart - 1);
                var end = Math.Min(lines.Length, Math.Max(sub.LineEnd, sub.LineStart));
                var slice = lines.Skip(start).Take(Math.Max(0, end - start)).ToList();
                if (slice.Count > SourceLineCap)
                {
                    truncated = true;
                    slice = slice.Take(300).Append($"… ({slice.Count - 400} lines omitted) …").Concat(slice.TakeLast(100)).ToList();
                }
                source = string.Join("\n", slice);
            }
            catch (Exception)
            {
                source = null;
            }
        }

        var props = new
        {
            name = sub.Name,
            signature = sub.Signature,
            path = file.RelativePath,
            lineStart = sub.LineStart,
            lineEnd = sub.LineEnd,
            sourceLanguage = sub.SourceLanguage,
            state = sub.State,
            callees,
            callerCount,
            corpusId = version.CorpusId,
            specId = spec?.Id,
            source,
            truncated,
        };
        var payload = new
        {
            subroutineId = sub.Id,
            name = sub.Name,
            signature = sub.Signature,
            path = file.RelativePath,
            lines = $"{sub.LineStart}-{sub.LineEnd}",
            sourceLanguage = sub.SourceLanguage,
            state = sub.State,
            callees,
            callerCount,
            corpusId = version.CorpusId,
            specId = spec?.Id,
            specState = spec?.State,
            source,
        };
        return (ToolResult.Artifact("routine", sub.Id.ToString(), props), payload);
    }

    // ── specSummary ──────────────────────────────────────────────────────

    public sealed record ClaimRow(string Section, string Id, string Text, string? Review, string? Citation);

    public static async Task<(ArtifactDto Artifact, object Payload, IReadOnlyList<ClaimRow> Claims)> SpecSummaryAsync(
        AppDbContext db, SpecSchemaProvider schemas, Spec spec, CancellationToken ct)
    {
        var sub = spec.Subroutine ?? await db.Subroutines.AsNoTracking().FirstAsync(s => s.Id == spec.SubroutineId, ct);
        var reviews = await db.ClaimReviews.AsNoTracking()
            .Where(r => r.SpecId == spec.Id)
            .ToDictionaryAsync(r => r.ClaimPath, r => r.Action, ct);
        var signature = await db.Signatures.AsNoTracking().FirstOrDefaultAsync(s => s.SpecId == spec.Id, ct);

        var schema = schemas.GetById(sub.SourceLanguage);
        var claims = ExtractClaims(spec.SpecJson.RootElement, schema, reviews);
        int count(string section) => claims.Count(c => c.Section == section);
        var counts = new
        {
            invariants = count("invariants"),
            sideEffects = count("side_effects"),
            edgeCases = count("edge_cases"),
            openQuestions = count("open_questions"),
            total = claims.Count,
            reviewed = claims.Count(c => c.Review is not null),
        };
        var summary = spec.SpecJson.RootElement.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;

        // Section → human label from the schema (falls back to Title Case of the field).
        var sectionLabels = claims.Select(c => c.Section).Distinct().ToDictionary(
            sec => sec,
            sec => schema?.ClaimKinds.FirstOrDefault(k => k.SpecJsonField == sec)?.Label
                   ?? string.Join(' ', sec.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..])));
        var sourceFile = sub.SourceFile ?? await db.SourceFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == sub.SourceFileId, ct);
        var allReviewed = claims.Count > 0 && claims.All(c => c.Review is not null && !(c.Section == "open_questions" && c.Review == "question"));

        var props = new
        {
            subroutineId = sub.Id,
            routineName = sub.Name,
            state = spec.State,
            summary,
            counts,
            sectionLabels,
            sourceFilePath = sourceFile?.RelativePath,
            lineStart = sub.LineStart,
            lineEnd = sub.LineEnd,
            canRoute = spec.State == "DRAFT",
            canReview = spec.State == "IN_REVIEW",
            canSign = spec.State == "IN_REVIEW" && allReviewed,
            claims = claims.Select(c => new
            {
                section = c.Section,
                id = c.Id,
                text = c.Text,
                review = c.Review,
                citation = c.Citation,
                citationLines = c.Citation is null ? null : c.Citation.Replace("L", ""),
            }),
            signedAt = signature?.SignedAt,
            signerDisplay = signature?.SignerDisplay,
        };
        var payload = new
        {
            specId = spec.Id,
            subroutineId = sub.Id,
            routineName = sub.Name,
            state = spec.State,
            summary,
            counts,
            claims = claims.Select(c => new
            {
                section = c.Section,
                id = c.Id,
                text = Truncate(c.Text, 220),
                review = c.Review ?? "untouched",
                citation = c.Citation,
            }),
            signedAt = signature?.SignedAt,
            signerDisplay = signature?.SignerDisplay,
        };
        return (ToolResult.Artifact("specSummary", spec.Id.ToString(), props), payload, claims);
    }

    private static List<ClaimRow> ExtractClaims(JsonElement root, SpecSchema? schema, Dictionary<string, string> reviews)
    {
        var rows = new List<ClaimRow>();
        if (root.ValueKind != JsonValueKind.Object) return rows;

        var textFieldBySection = new Dictionary<string, string>(StringComparer.Ordinal);
        if (schema is not null)
            foreach (var k in schema.ClaimKinds)
                if (!string.IsNullOrEmpty(k.SpecJsonField)) textFieldBySection[k.SpecJsonField] = k.TextField;

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            if (prop.Name is "inputs" or "outputs") continue;
            foreach (var item in prop.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                var id = idEl.GetString() ?? "";
                var text = ReadText(item, textFieldBySection.TryGetValue(prop.Name, out var tf) ? tf : null);
                if (text is null) continue;
                var path = ClaimPath.For(prop.Name, id);
                reviews.TryGetValue(path, out var review);
                rows.Add(new ClaimRow(prop.Name, id, text, review, ReadCitation(item)));
            }
        }
        return rows;
    }

    private static string? ReadText(JsonElement item, string? preferred)
    {
        var candidates = new List<string>();
        if (preferred is not null) candidates.Add(preferred);
        candidates.AddRange(new[] { "claim", "description", "question", "policy", "contract", "statement", "text", "summary", "behavior" });
        foreach (var c in candidates)
            if (item.TryGetProperty(c, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                return s;
        return null;
    }

    private static string? ReadCitation(JsonElement item)
    {
        if (!item.TryGetProperty("citations", out var cits)) return null;
        if (cits.ValueKind == JsonValueKind.String) return cits.GetString();
        if (cits.ValueKind != JsonValueKind.Array) return null;
        var parts = new List<string>();
        foreach (var c in cits.EnumerateArray())
        {
            if (c.ValueKind == JsonValueKind.String) { parts.Add(c.GetString() ?? ""); continue; }
            if (c.ValueKind != JsonValueKind.Object) continue;
            foreach (var key in new[] { "lines", "line_range", "lineRange", "range", "line" })
                if (c.TryGetProperty(key, out var v))
                {
                    parts.Add(v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText());
                    break;
                }
        }
        var joined = string.Join(", ", parts.Where(p => p.Length > 0));
        return joined.Length == 0 ? null : "L" + joined.Replace(", ", ", L");
    }

    // ── clusterGrid ──────────────────────────────────────────────────────

    public static async Task<(ArtifactDto Artifact, object Payload)?> ClusterGridAsync(
        AppDbContext db, Guid corpusId, CancellationToken ct)
    {
        var run = await db.PatternAnalysisRuns.AsNoTracking()
            .Where(r => r.CorpusId == corpusId && (r.State == "SUCCEEDED" || r.State == "PARTIAL"))
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync(ct);
        if (run is null) return null;

        var clusters = await db.PatternClusters.AsNoTracking()
            .Where(c => c.PatternAnalysisRunId == run.Id)
            .OrderByDescending(c => c.MemberCount)
            .ThenBy(c => c.Label)
            .ToListAsync(ct);

        var rendered = clusters.Select(c =>
        {
            var members = new List<object>();
            try
            {
                using var doc = JsonDocument.Parse(c.MemberSubroutineIdsJson);
                foreach (var m in doc.RootElement.EnumerateArray().Take(8))
                    members.Add(new
                    {
                        subroutineId = m.TryGetProperty("subroutineId", out var id) ? id.GetString() : null,
                        subroutineName = m.TryGetProperty("subroutineName", out var n) ? n.GetString() : null,
                    });
            }
            catch { /* tolerate */ }
            return new
            {
                id = c.Id,
                label = c.Label,
                suggestedArchetypeName = c.SuggestedArchetypeName,
                memberCount = c.MemberCount,
                rationale = c.Rationale,
                members,
            };
        }).ToList();

        var routineCount = clusters.Sum(c => c.MemberCount);
        var props = new
        {
            runId = run.Id,
            clusterCount = clusters.Count,
            routineCount,
            clusters = rendered,
        };
        var payload = new
        {
            corpusId,
            runId = run.Id,
            runState = run.State,
            completedAt = run.CompletedAt,
            clusterCount = clusters.Count,
            routineCount,
            singletons = clusters.Count(c => c.MemberCount <= 1),
            top = rendered.Take(12).Select(c => new { c.id, c.label, c.suggestedArchetypeName, c.memberCount, rationale = Truncate(c.rationale, 200) }),
        };
        return (ToolResult.Artifact("clusterGrid", corpusId.ToString(), props), payload);
    }

    // ── scaffoldTree / gateResults ───────────────────────────────────────

    public static async Task<(ArtifactDto Artifact, object Payload)?> ScaffoldTreeAsync(
        AppDbContext db, IBlobClient blob, Guid scaffoldId, CancellationToken ct)
    {
        var scaffold = await db.Scaffolds.AsNoTracking()
            .Include(s => s.Spec).ThenInclude(sp => sp!.Subroutine)
            .FirstOrDefaultAsync(s => s.Id == scaffoldId, ct);
        if (scaffold is null) return null;

        var files = new List<object>();
        try
        {
            var manifest = await blob.GetTextAsync(scaffold.PackageBlobUri, ct);
            using var doc = JsonDocument.Parse(manifest);
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.TryGetProperty("files", out var f) ? f : default;
            if (arr.ValueKind == JsonValueKind.Array)
                foreach (var file in arr.EnumerateArray())
                {
                    var path = file.TryGetProperty("path", out var p) ? p.GetString() : null;
                    var language = file.TryGetProperty("language", out var l) ? l.GetString() : null;
                    var content = file.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    files.Add(new { path, language, lines = content is null ? 0 : content.Count(ch => ch == '\n') + 1 });
                }
        }
        catch { /* manifest unreadable — card still renders counts */ }

        var routineName = scaffold.Spec?.Subroutine?.Name ?? "";
        var props = new
        {
            specId = scaffold.SpecId,
            routineName,
            targetPlatform = scaffold.TargetPlatform,
            state = scaffold.State,
            fileCount = scaffold.FileCount,
            totalLines = scaffold.TotalLines,
            todoCount = scaffold.TodoCount,
            files,
        };
        var payload = new
        {
            scaffoldId = scaffold.Id,
            specId = scaffold.SpecId,
            routineName,
            targetPlatform = scaffold.TargetPlatform,
            state = scaffold.State,
            fileCount = scaffold.FileCount,
            totalLines = scaffold.TotalLines,
            todoCount = scaffold.TodoCount,
            files = files.Take(30),
            gitCommitUrl = scaffold.GitCommitUrl,
        };
        return (ToolResult.Artifact("scaffoldTree", scaffold.Id.ToString(), props), payload);
    }

    public static async Task<(ArtifactDto Artifact, object Payload)?> GateResultsAsync(
        AppDbContext db, Guid scaffoldId, CancellationToken ct)
    {
        var scaffold = await db.Scaffolds.AsNoTracking()
            .Include(s => s.Spec).ThenInclude(sp => sp!.Subroutine)
            .FirstOrDefaultAsync(s => s.Id == scaffoldId, ct);
        if (scaffold is null) return null;

        var runs = await db.ValidationRuns.AsNoTracking()
            .Where(v => v.ScaffoldId == scaffoldId)
            .OrderByDescending(v => v.StartedAt)
            .ToListAsync(ct);
        var latestByStage = runs.GroupBy(r => r.Stage).Select(g => g.First()).ToList();
        var order = new[] { "COMPILE", "TEST_PACK", "EQUIVALENCE", "FALSIFYING" };
        var gates = order.Select(stage =>
        {
            var r = latestByStage.FirstOrDefault(x => x.Stage == stage);
            return new
            {
                stage,
                status = r?.Status ?? "NOT_RUN",
                summary = r?.Summary ?? "",
                completedAt = r?.CompletedAt,
                errorCode = r?.ErrorCode,
            };
        }).ToList();

        var routineName = scaffold.Spec?.Subroutine?.Name ?? "";
        var props = new { specId = scaffold.SpecId, routineName, targetPlatform = scaffold.TargetPlatform, gates };
        var payload = new { scaffoldId, specId = scaffold.SpecId, routineName, targetPlatform = scaffold.TargetPlatform, gates };
        return (ToolResult.Artifact("gateResults", scaffoldId.ToString(), props), payload);
    }

    // ── planWaves / docSection ───────────────────────────────────────────

    public static async Task<(ArtifactDto Artifact, object Payload)> PlanWavesAsync(
        AppDbContext db, MigrationPlan plan, CancellationToken ct)
    {
        var waves = await db.MigrationWaves.AsNoTracking()
            .Where(w => w.MigrationPlanId == plan.Id).OrderBy(w => w.WaveNumber).ToListAsync(ct);
        var rendered = waves.Select(w => new { w.WaveNumber, name = w.Name, w.RoutineCount, status = w.Status }).ToList();
        var props = new
        {
            corpusId = plan.CorpusId,
            status = plan.Status,
            strategyName = plan.StrategyName,
            totalWaves = plan.TotalWaves,
            totalRoutines = plan.TotalRoutines,
            summary = plan.Summary,
            waves = rendered.Select(w => new { waveNumber = w.WaveNumber, w.name, routineCount = w.RoutineCount, w.status }),
        };
        var payload = new
        {
            planId = plan.Id,
            plan.Status,
            plan.StrategyName,
            plan.TotalWaves,
            plan.TotalRoutines,
            plan.Summary,
            plan.CreatedAt,
            plan.ApprovedAt,
            waves = rendered.Select(w => new { w.WaveNumber, w.name, w.RoutineCount, w.status }),
        };
        return (ToolResult.Artifact("planWaves", plan.Id.ToString(), props), payload);
    }

    public const int DocMarkdownCap = 8_000;

    public static ArtifactDto DocSection(DocSection section, string title, string href)
    {
        var md = section.RenderedMarkdown ?? "";
        if (md.Length > DocMarkdownCap) md = md[..DocMarkdownCap] + "\n\n…";
        return ToolResult.Artifact("docSection", section.Id.ToString(), new
        {
            title,
            kind = section.SectionKind,
            markdown = md,
            corpusId = section.CorpusId,
            href,
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────

    public static IReadOnlyList<string> ReadStringArray(JsonDocument? doc)
    {
        if (doc is null) return Array.Empty<string>();
        try
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            return root.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    public static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "…";
    }
}
