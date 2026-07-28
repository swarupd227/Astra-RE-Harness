using System.Text.Json;

namespace Astra.Api.Validation;

/// <summary>
/// Shared claim-kind bucketing logic, extracted from
/// <see cref="GoldenDatasetScorer"/> (Phase 6.0) so Phase 12.0's pattern-
/// clustering pass can reuse the exact same kind-taxonomy without
/// duplicating ~40 registration lines that must otherwise be kept in sync
/// by hand across two files every time a new source language ships.
/// </summary>
public static class ClaimKindBucketer
{
    /// <summary>
    /// Walk a spec/v1 JSON root and bucket every claim's text-as-string by
    /// kind. Concatenates the readable fields of each claim so callers
    /// don't need to know which property carries the prose.
    /// </summary>
    public static Dictionary<string, List<string>> Bucket(JsonElement root)
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
        // VB.NET / VB6 kinds. These schemas name their claim kinds in
        // camelCase (matching the schema's claimKinds list) while the
        // extract prompts emit snake_case top-level arrays, so register the
        // net-new VB fields AND camelCase aliases for the shared kinds so VB
        // golden entries score. The bucket dict is keyed by kindKey, so
        // registering `edge_cases` under both "edge_case" and "edgeCase" is
        // fine.
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
        // net-new ABL fields need a bucket entry.
        TryAddKind(buckets, root, "temp_table_usages", "tempTableUsage");
        TryAddKind(buckets, root, "shared_variable_scopes", "sharedVariableScope");
        TryAddKind(buckets, root, "record_phrase_semantics", "recordPhraseSemantics");
        TryAddKind(buckets, root, "transaction_scopes", "transactionScope");
        // PHP kinds (Tier-4 migration, dual target java-spring + dotnet8).
        // errorHandlingContract / invariant / sideEffect / edgeCase /
        // openQuestion are shared with the VB/ABL taxonomy and already
        // registered above (camelCase); only the four net-new PHP fields need
        // a bucket entry.
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
        // UniBasic (Rocket UniData/UniVerse) kinds — feasibility POC for the
        // ILS equipment-finance engagement. invariant / sideEffect / edgeCase
        // / openQuestion are shared and already registered above (camelCase);
        // only the five net-new UniBasic fields need a bucket. No free/
        // scriptable UniData or UniVerse runtime exists, so this golden
        // scorer is the primary extraction-quality gate (same posture as ABL).
        TryAddKind(buckets, root, "dynamic_array_usages", "dynamicArrayUsage");
        TryAddKind(buckets, root, "field_position_accesses", "fieldPositionAccess");
        TryAddKind(buckets, root, "record_access_semantics", "recordAccessSemantics");
        TryAddKind(buckets, root, "dynamic_call_targets", "dynamicCallTarget");
        TryAddKind(buckets, root, "dynamic_query_executions", "dynamicQueryExecution");
        return buckets;
    }

    /// <summary>The non-empty kind keys present in a bucketed spec, sorted
    /// and comma-joined — the deterministic "claim-kind signature" used as
    /// the coarse clustering hint before the LLM judging pass.</summary>
    public static string Signature(Dictionary<string, List<string>> buckets) =>
        string.Join(",", buckets.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal));

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
        // Concatenate every string property of the claim — callers compare
        // against "any of these readable fields" (regex match for golden
        // scoring, free-text summary for clustering).
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
}
