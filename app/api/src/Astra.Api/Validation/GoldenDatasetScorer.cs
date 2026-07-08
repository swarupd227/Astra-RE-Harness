using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Api.Auth;
using Astra.Api.Llm;
using Astra.Api.Llm.Prompts;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Validation;

/// <summary>
/// Phase 6.0 — Runs the extract pipeline against one golden-dataset
/// entry's snippet and scores claim coverage. The score is a fraction:
/// <c>matched / total</c> where total is the number of expected claims
/// authored on the entry and matched is the number of those whose regex
/// pattern is satisfied by any claim of the same kind in the extract
/// output. A <see cref="GoldenDatasetRun"/> row is persisted per run so
/// the dashboard can chart score over time per (prompt, version).
///
/// The scorer is deliberately strict on KIND and permissive on TEXT:
/// an invariant pattern only matches invariant output claims (cross-kind
/// matches don't count). The text comparison is a regex match over the
/// claim's text fields concatenated (claim/description/behavior/question/
/// citations). Regex is the right compromise — robust to phrasing
/// variation, deterministic, cheap to author.
/// </summary>
public sealed class GoldenDatasetScorer
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AppDbContext _db;
    private readonly ILlmProvider _provider;
    private readonly PromptLibrary _prompts;
    private readonly ILogger<GoldenDatasetScorer> _log;

    public GoldenDatasetScorer(
        AppDbContext db,
        ILlmProvider provider,
        PromptLibrary prompts,
        ILogger<GoldenDatasetScorer> log)
    {
        _db = db;
        _provider = provider;
        _prompts = prompts;
        _log = log;
    }

    public sealed record ScoreOutcome(
        GoldenDatasetRun Run,
        IReadOnlyList<ClaimMatch> Detail);

    public sealed record ClaimMatch(
        string ExpectedClaimId,
        string Kind,
        string Pattern,
        bool Matched,
        string? MatchedAgainst);

    public async Task<ScoreOutcome> ScoreAsync(
        Guid entryDbId,
        DevPersonaContext? actor,
        CancellationToken ct = default)
    {
        var entry = await _db.GoldenDatasetEntries.FirstOrDefaultAsync(e => e.Id == entryDbId, ct)
            ?? throw new InvalidOperationException($"Golden dataset entry {entryDbId} not found.");

        // 1. Resolve the active extract prompt for this schema. Claim quality
        //    is a property of the EXTRACT pass, which is target-stack agnostic —
        //    but the prompt only EXISTS under specific target stacks (the
        //    vbnet/csharp extract prompts live under dotnet10, not dotnet8), so
        //    probe the known stacks and score against whichever one the schema
        //    actually ships an extract prompt for.
        string[] candidateStacks = { "dotnet8", "dotnet10", "java-spring" };
        var prompt = _prompts.GetLatest(entry.SchemaId, "dotnet8", "extract");
        var targetStack = "dotnet8";
        foreach (var ts in candidateStacks)
        {
            var p = _prompts.GetLatest(entry.SchemaId, ts, "extract");
            if (p is not null) { prompt = p; targetStack = ts; break; }
        }
        var promptId = prompt?.PromptId ?? $"{entry.SchemaId}-extract";
        var promptVersion = prompt?.Version ?? "v0";

        var startedAt = DateTimeOffset.UtcNow;
        var lineCount = entry.SourceContent.Count(c => c == '\n') + 1;

        // 2. Drive the provider with this entry's snippet inline.
        var request = new ExtractionRequest(
            SubroutineId: Guid.Empty, // pseudo — this run is not scaffold-bound
            SubroutineName: entry.EntryId,
            SourcePath: entry.SourcePath,
            SourceText: entry.SourceContent,
            LineCount: lineCount,
            PromptTemplateId: promptId,
            PromptTemplateVersion: promptVersion,
            SourceLanguage: entry.SchemaId,
            TargetStack: targetStack);

        string? finalSpecJson = null;
        await foreach (var ev in _provider.ExtractAsync(request, ct))
        {
            if (ev.Type == "__final__" && ev.Data is IDictionary<string, object?> dict)
            {
                if (dict.TryGetValue("specJson", out var specObj) && specObj is not null)
                {
                    // specJson can land as either a string or as an already-
                    // structured object depending on the provider.
                    finalSpecJson = specObj is string s
                        ? s
                        : JsonSerializer.Serialize(specObj, JsonOpts);
                }
                break;
            }
        }

        if (finalSpecJson is null)
        {
            throw new InvalidOperationException(
                "LLM provider did not emit a __final__ event with specJson — cannot score.");
        }

        // 3. Parse the entry's expected claims + the provider's output and
        //    do the kind-bucketed regex match.
        var expected = JsonSerializer.Deserialize<List<ExpectedClaim>>(entry.ExpectedClaimsJson, JsonOpts)
            ?? new List<ExpectedClaim>();

        using var specDoc = JsonDocument.Parse(finalSpecJson);
        var outputBuckets = BucketClaimsByKind(specDoc.RootElement);

        var detail = new List<ClaimMatch>();
        foreach (var exp in expected)
        {
            var regex = new Regex(exp.Pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var bucket = outputBuckets.TryGetValue(exp.Kind, out var list) ? list : new List<string>();
            string? matchedAgainst = null;
            foreach (var text in bucket)
            {
                if (regex.IsMatch(text))
                {
                    matchedAgainst = text.Length > 240 ? text[..240] + "…" : text;
                    break;
                }
            }
            detail.Add(new ClaimMatch(exp.Id, exp.Kind, exp.Pattern, matchedAgainst is not null, matchedAgainst));
        }

        int total = detail.Count;
        int matched = detail.Count(d => d.Matched);
        double score = total == 0 ? 1.0 : (double)matched / total;

        // 4. Persist a run row.
        var run = new GoldenDatasetRun
        {
            Id = Guid.NewGuid(),
            EntryId = entry.Id,
            LlmCallId = null, // scorer bypasses the LlmCall persistence path
            PromptId = promptId,
            PromptVersion = promptVersion,
            ModelName = _provider.Info.Model,
            Matched = matched,
            Total = total,
            Score = score,
            DetailJson = JsonSerializer.Serialize(detail, JsonOpts),
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            TriggeredBy = actor?.DisplayName,
        };
        _db.GoldenDatasetRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "Golden dataset run for {EntryId}: {Matched}/{Total} = {Score:P0} (prompt {Prompt}@{Version})",
            entry.EntryId, matched, total, score, promptId, promptVersion);

        return new ScoreOutcome(run, detail);
    }

    /// <summary>
    /// Walk the spec/v1 output and bucket every claim's text-as-string by
    /// kind. We concatenate the readable fields of each claim so the
    /// regex doesn't have to know which property carries the prose.
    /// </summary>
    private static Dictionary<string, List<string>> BucketClaimsByKind(JsonElement root)
    {
        var buckets = new Dictionary<string, List<string>>();
        // Fortran-f77 + cobol kinds (Phase 6.0).
        TryAddKind(buckets, root, "invariants", "invariant");
        TryAddKind(buckets, root, "section_contracts", "section_contract");
        TryAddKind(buckets, root, "io_side_effects", "io_side_effect");
        TryAddKind(buckets, root, "edge_cases", "edge_case");
        TryAddKind(buckets, root, "open_questions", "open_question");
        // Delphi-specific kinds (Phase 9.0). The bucket dictionary is keyed by
        // kindKey, so the additional registrations below don't collide with
        // the kinds above — an extract that has both `side_effects` (delphi)
        // and `io_side_effects` (fortran) is legal even though it'll never
        // happen in practice.
        TryAddKind(buckets, root, "object_lifetimes", "object_lifetime");
        TryAddKind(buckets, root, "interface_implementations", "interface_implementation");
        TryAddKind(buckets, root, "property_accessors", "property_accessor");
        TryAddKind(buckets, root, "event_handler_contracts", "event_handler_contract");
        TryAddKind(buckets, root, "rtti_usages", "rtti_usage");
        TryAddKind(buckets, root, "side_effects", "side_effect");
        // C++-specific kinds (Phase 9.1). object_lifetime / rtti_usage /
        // side_effect / edge_case / open_question / invariant are shared
        // with the Delphi taxonomy and already registered above; only the
        // three net-new properties need a new bucket entry.
        TryAddKind(buckets, root, "template_instantiations", "template_instantiation");
        TryAddKind(buckets, root, "undefined_behaviors", "undefined_behavior");
        TryAddKind(buckets, root, "exception_contracts", "exception_contract");
        // VB.NET / VB6 kinds (Phase 12.0 + golden-set expansion). These schemas
        // name their claim kinds in camelCase (matching the schema's claimKinds
        // list) while the extract prompts emit snake_case top-level arrays, so
        // register the net-new VB fields AND camelCase aliases for the shared
        // kinds so VB golden entries score. The bucket dict is keyed by kindKey,
        // so registering `edge_cases` under both "edge_case" and "edgeCase" is fine.
        TryAddKind(buckets, root, "module_to_static_class", "moduleToStaticClass");
        TryAddKind(buckets, root, "implicit_conversion_risks", "implicitConversionRisk");
        TryAddKind(buckets, root, "with_block_usages", "withBlockUsage");
        TryAddKind(buckets, root, "string_comparison_semantics", "stringComparisonSemantics");
        TryAddKind(buckets, root, "error_handling_contracts", "errorHandlingContract");
        TryAddKind(buckets, root, "invariants", "invariant");
        TryAddKind(buckets, root, "side_effects", "sideEffect");
        TryAddKind(buckets, root, "edge_cases", "edgeCase");
        TryAddKind(buckets, root, "open_questions", "openQuestion");
        // OpenEdge ABL kinds (Tier-2 migration). errorHandlingContract /
        // invariant / sideEffect / edgeCase / openQuestion are shared with the
        // VB taxonomy and already registered above (camelCase); only the four
        // net-new ABL fields need a bucket entry. There is no source-execution
        // equivalence gate for ABL (the Progress AVM is proprietary), so this
        // golden scorer is the primary extraction-quality gate for openedge.
        TryAddKind(buckets, root, "temp_table_usages", "tempTableUsage");
        TryAddKind(buckets, root, "shared_variable_scopes", "sharedVariableScope");
        TryAddKind(buckets, root, "record_phrase_semantics", "recordPhraseSemantics");
        TryAddKind(buckets, root, "transaction_scopes", "transactionScope");
        // PHP kinds (Tier-4 migration, dual target java-spring + dotnet8).
        // errorHandlingContract / invariant / sideEffect / edgeCase /
        // openQuestion are shared with the VB/ABL taxonomy and already
        // registered above (camelCase); only the four net-new PHP fields need
        // a bucket entry. PHP has a free runtime so source-execution
        // equivalence is FEASIBLE via a future php-sidecar, but until that
        // exists this golden scorer is the primary extraction-quality gate.
        TryAddKind(buckets, root, "loose_type_coercions", "looseTypeCoercion");
        TryAddKind(buckets, root, "array_shape_semantics", "arrayShapeSemantics");
        TryAddKind(buckets, root, "null_safety_contracts", "nullSafetyContract");
        TryAddKind(buckets, root, "superglobal_usages", "superglobalUsage");
        // Java 11→21 modernization kinds (Tier-3, in-place upgrade). invariant /
        // edgeCase / openQuestion are shared and already registered above
        // (camelCase); only the six net-new modernization fields need a bucket.
        TryAddKind(buckets, root, "jakarta_namespace_migrations", "jakartaNamespaceMigration");
        TryAddKind(buckets, root, "removed_api_usages", "removedApiUsage");
        TryAddKind(buckets, root, "deprecated_api_usages", "deprecatedApiUsage");
        TryAddKind(buckets, root, "spring_boot_upgrades", "springBootUpgrade");
        TryAddKind(buckets, root, "library_major_bumps", "libraryMajorBump");
        TryAddKind(buckets, root, "modernization_opportunities", "modernizationOpportunity");
        return buckets;
    }

    private static void TryAddKind(
        Dictionary<string, List<string>> buckets,
        JsonElement root,
        string property,
        string kindKey)
    {
        if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        var list = new List<string>();
        foreach (var claim in arr.EnumerateArray())
        {
            list.Add(ClaimToText(claim));
        }
        buckets[kindKey] = list;
    }

    private static string ClaimToText(JsonElement claim)
    {
        // Concatenate every string property of the claim — the regex
        // patterns are authored against "any of these readable fields".
        // JsonDocument.Parse is cheap; we already paid for it once above.
        var parts = new List<string>();
        if (claim.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in claim.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    parts.Add(prop.Value.GetString() ?? "");
                }
                else if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    // citations: [{"lines": "..."}] — flatten one level.
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var sub in item.EnumerateObject())
                            {
                                if (sub.Value.ValueKind == JsonValueKind.String)
                                    parts.Add(sub.Value.GetString() ?? "");
                            }
                        }
                    }
                }
            }
        }
        return string.Join(" | ", parts);
    }

    private sealed class ExpectedClaim
    {
        public string Kind { get; set; } = "";
        public string Id { get; set; } = "";
        public string Pattern { get; set; } = "";
    }
}
