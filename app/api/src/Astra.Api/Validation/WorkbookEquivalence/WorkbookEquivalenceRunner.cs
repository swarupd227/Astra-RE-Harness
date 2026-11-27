using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Office;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Validation.WorkbookEquivalence;

/// <summary>
/// EQUIVALENCE for a VBA routine inside a workbook converted to React + Python: the workbook and the generated
/// package are run on the same cases, built from the signed spec's cell contract and the workbook's own saved values,
/// and compared case by case.
/// <list type="bullet">
/// <item>Outputs a formula computes: the package's value against the workbook's formulas, recalculated by a formula
/// engine for each case (no Excel, no macros run).</item>
/// <item>Outputs the routine itself computes: against the value saved in the workbook, for the saved case.</item>
/// <item>A request to an outside service: what the package sends, captured, against the signed outside_requests
/// template rendered for the case. Offline (the default) the service is answered by the gate; live, the real call goes
/// through and an output marked compare_with is checked against the workbook's own formula for it.</item>
/// </list>
/// </summary>
public sealed class WorkbookEquivalenceRunner
{
    private readonly AppDbContext _db;
    private readonly IBlobClient _blob;
    private readonly ILogger _log;

    public WorkbookEquivalenceRunner(AppDbContext db, IBlobClient blob, ILogger log)
    {
        _db = db; _blob = blob; _log = log;
    }

    public sealed record Outcome(string Status, string? ErrorCode, string Summary, object Metrics, string Transcript);

    public sealed record Check(string Kind, string What, bool Passed, string Detail);

    /// <summary>Whether this gate applies: VBA inside an Office document, generated for React + Python.</summary>
    public static bool Applies(Scaffold scaffold) =>
        string.Equals(scaffold.Spec?.Subroutine?.SourceLanguage, "vb6", StringComparison.OrdinalIgnoreCase)
        && string.Equals(scaffold.TargetPlatform, CompileValidator.ReactPythonStack, StringComparison.OrdinalIgnoreCase)
        && scaffold.Spec?.Subroutine?.SourceFile?.RelativePath is { } p && OfficeHostContext.SplitPath(p) is not null;

    public async Task<Outcome> RunAsync(Scaffold scaffold, bool live, CancellationToken ct)
    {
        var t = new StringBuilder();
        var sub = scaffold.Spec!.Subroutine!;
        var routine = sub.Name;
        t.AppendLine($"workbook equivalence · {routine} · {(live ? "live (outside calls go through)" : "offline (outside calls answered by the gate)")}");
        Outcome Skip(string code, string why) =>
            new("SKIPPED", code, why, new { mode = "workbook", routine, live, skipped = why }, t.AppendLine(why).ToString());

        var contract = WorkbookContract.FromSpec(scaffold.Spec.SpecJson.RootElement);
        if (contract.IsEmpty)
            return Skip("equivalence.no_cell_contract",
                $"The signed spec of `{routine}` has no workbook cell contract, so there is nothing to compare cell by cell. " +
                "Extract it again (the workbook prompt v2 states the cells and any outside request), review and sign it, then run equivalence.");
        t.AppendLine($"contract {string.Join(", ", contract.ClaimIds)}: {contract.Inputs.Count} inputs, {contract.Selections.Count} selection lists, " +
                     $"{contract.Outputs.Count} outputs, {contract.Requests.Count} outside requests");

        // The workbook kept as evidence beside the routine's module.
        var split = OfficeHostContext.SplitPath(sub.SourceFile!.RelativePath)!.Value;
        var doc = await _db.SourceFiles.AsNoTracking()
            .Where(f => f.SourceVersionId == sub.SourceFile.SourceVersionId && f.RelativePath == split.Document)
            .Select(f => new { f.BlobUri, f.Kind })
            .FirstOrDefaultAsync(ct);
        if (doc is null || doc.Kind != Artefacts.ArtefactKinds.Workbook)
            return Skip("equivalence.no_workbook", $"The workbook {split.Document} is not kept with this version, so its values and formulas cannot be read.");
        var bytes = await _blob.GetBytesAsync(doc.BlobUri, ct);
        var ext = Path.GetExtension(split.Document).ToLowerInvariant();

        var sheetNames = contract.Inputs.Select(i => i.Sheet)
            .Concat(contract.Selections.Select(s => s.Sheet))
            .Concat(contract.Outputs.Select(o => o.Sheet))
            .Concat(contract.Outputs.Where(o => o.CompareWith is not null).Select(o => o.CompareWith!.Value.Sheet))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sheets = WorkbookInputs.ReadSheets(bytes, sheetNames);
        var unreadable = sheetNames.Where(n => !sheets.ContainsKey(n)).ToList();
        if (unreadable.Count == sheetNames.Count)
            return Skip("equivalence.sheets_unreadable", $"None of the contract's sheets ({string.Join(", ", sheetNames)}) could be read from {split.Document}.");
        if (unreadable.Count > 0) t.AppendLine($"note: sheets not found in the workbook: {string.Join(", ", unreadable)}");

        var vectors = WorkbookEquivalenceVectors.Build(contract, sheets);
        t.AppendLine($"cases: {vectors.Count}");

        var tempDir = Path.Combine(Path.GetTempPath(), $"astra-equivalence-{Guid.NewGuid():N}");
        try
        {
            // ── package side ──
            var manifest = JsonDocument.Parse(await _blob.GetTextAsync(scaffold.PackageBlobUri, ct));
            var root = Path.GetFullPath(tempDir) + Path.DirectorySeparatorChar;
            foreach (var f in manifest.RootElement.GetProperty("files").EnumerateArray())
            {
                var abs = Path.GetFullPath(Path.Combine(tempDir, f.GetProperty("path").GetString()!));
                if (!abs.StartsWith(root, StringComparison.Ordinal)) throw new InvalidOperationException("Manifest path points outside the package.");
                Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                await File.WriteAllTextAsync(abs, f.GetProperty("content").GetString() ?? "", ct);
            }
            var backend = Path.Combine(tempDir, "backend");
            if (!File.Exists(Path.Combine(backend, "app", "contract.py")))
                return Skip("equivalence.no_contract_entry",
                    "The package has no backend/app/contract.py, the entry point the gate calls with the contract's fields. " +
                    "Regenerate the package (the React + Python prompt adds it when the signed spec has a cell contract).");
            var (prepExit, prepLog) = await PythonRunner.PrepareAsync(backend, ct);
            if (prepExit != 0)
                return new("ERRORED", "equivalence.package_install_failed", "The package's Python requirements could not be installed.",
                    new { mode = "workbook", routine, live }, t.AppendLine(prepLog).ToString());
            var job = new JsonObject
            {
                ["live"] = live,
                ["canned"] = Canned(contract.Requests.FirstOrDefault()),
                ["vectors"] = new JsonArray(vectors.Select(v => (JsonNode)new JsonObject { ["id"] = v.Id, ["inputs"] = v.Inputs.DeepClone() }).ToArray()),
            };
            await File.WriteAllTextAsync(Path.Combine(backend, ".astra_eq_job.json"), job.ToJsonString(), ct);
            await File.WriteAllTextAsync(Path.Combine(backend, ".astra_equivalence.py"), WorkbookEquivalenceScripts.Package, ct);
            var (pkgExit, pkgLog) = await PythonRunner.RunInVenvAsync(backend, ".astra_equivalence.py .astra_eq_job.json .astra_eq_out.json", ct);
            var pkgOut = Path.Combine(backend, ".astra_eq_out.json");
            if (!File.Exists(pkgOut))
                return new("ERRORED", "equivalence.package_run_failed", "The package could not be run for the cases.",
                    new { mode = "workbook", routine, live }, t.AppendLine(pkgLog).ToString());
            var pkg = JsonNode.Parse(await File.ReadAllTextAsync(pkgOut, ct))!.AsObject();
            if (pkg["fatal"]?.ToString() is { } fatal)
                return new("FAILED", "equivalence.contract_entry_failed", fatal,
                    new { mode = "workbook", routine, live }, t.AppendLine(fatal).AppendLine(pkg["trace"]?.ToString()).ToString());
            var pkgById = (pkg["results"] as JsonArray ?? new()).OfType<JsonObject>().ToDictionary(r => r["id"]!.ToString());

            // ── workbook side: formulas recalculated per case ──
            var formulaCells = contract.Outputs.Where(o => o.ComputedBy == "formula").Select(o => (o.Sheet, o.Cell))
                .Concat(contract.Outputs.Where(o => o.CompareWith is not null).Select(o => o.CompareWith!.Value))
                .Distinct().ToList();
            Dictionary<string, JsonObject> wbById = new();
            string? formulaNote = null;
            if (formulaCells.Count > 0)
            {
                if (ext is not (".xlsx" or ".xlsm" or ".xltx" or ".xltm"))
                    formulaNote = $"{ext} workbooks cannot be recalculated by the formula engine; formula outputs are not compared.";
                else
                {
                    var (python, toolLog) = await PythonRunner.EnsureToolVenvAsync("formulas", WorkbookEquivalenceScripts.FormulaRequirements, ct);
                    if (python is null) formulaNote = "The formula engine could not be installed; formula outputs are not compared.";
                    else
                    {
                        var wbDir = Path.Combine(tempDir, ".workbook");
                        Directory.CreateDirectory(wbDir);
                        var book = "workbook" + ext;
                        await File.WriteAllBytesAsync(Path.Combine(wbDir, book), bytes, ct);
                        var wbJob = new JsonObject
                        {
                            ["path"] = book,
                            ["book"] = book,
                            ["outputs"] = new JsonArray(formulaCells.Select(c => (JsonNode)new JsonObject { ["sheet"] = c.Item1, ["cell"] = c.Item2 }).ToArray()),
                            ["vectors"] = new JsonArray(vectors.Select(v => (JsonNode)new JsonObject
                            {
                                ["id"] = v.Id,
                                ["cells"] = new JsonArray(v.Cells.Concat(WrittenByPackage(contract, pkgById.GetValueOrDefault(v.Id)))
                                    .Select(c => (JsonNode)new JsonObject { ["sheet"] = c.Sheet, ["cell"] = c.Cell, ["value"] = c.Value?.DeepClone() }).ToArray()),
                            }).ToArray()),
                        };
                        await File.WriteAllTextAsync(Path.Combine(wbDir, "job.json"), wbJob.ToJsonString(), ct);
                        await File.WriteAllTextAsync(Path.Combine(wbDir, "formulas_eval.py"), WorkbookEquivalenceScripts.Formulas, ct);
                        var (_, wbLog) = await PythonRunner.RunWithAsync(python, "formulas_eval.py job.json out.json", wbDir, ct);
                        var wbOut = Path.Combine(wbDir, "out.json");
                        if (!File.Exists(wbOut)) formulaNote = "The formula engine did not answer; formula outputs are not compared.\n" + wbLog;
                        else
                        {
                            var wb = JsonNode.Parse(await File.ReadAllTextAsync(wbOut, ct))!.AsObject();
                            if (wb["fatal"]?.ToString() is { } wf) formulaNote = $"The formula engine could not load the workbook ({wf}); formula outputs are not compared.";
                            wbById = (wb["results"] as JsonArray ?? new()).OfType<JsonObject>().ToDictionary(r => r["id"]!.ToString());
                        }
                    }
                }
                if (formulaNote is not null) t.AppendLine("note: " + formulaNote.Split('\n')[0]);
            }

            // ── compare, case by case ──
            object? Saved(string sheet, string cell) =>
                sheets.TryGetValue(sheet, out var cs) ? cs.FirstOrDefault(c => c.Address.Equals(cell, StringComparison.OrdinalIgnoreCase))?.Value : null;
            var rows = new List<object>();
            int passed = 0, failed = 0, skipped = 0;
            t.AppendLine().AppendLine("=== case by case ===");
            foreach (var v in vectors)
            {
                pkgById.TryGetValue(v.Id, out var p);
                wbById.TryGetValue(v.Id, out var w);
                var checks = Compare(contract, v, p, w, Saved, live);
                var status = checks.Any(c => !c.Passed) ? "FAILED" : checks.Count > 0 ? "PASSED" : "SKIPPED";
                if (status == "FAILED") failed++; else if (status == "PASSED") passed++; else skipped++;
                t.AppendLine($"{v.Id} [{status}] {v.Label} · inputs {v.Inputs.ToJsonString()}");
                foreach (var c in checks) t.AppendLine($"    {(c.Passed ? "ok  " : "DIFF")} {c.Kind} {c.What}: {c.Detail}");
                if (p?["error"]?.ToString() is { } err) t.AppendLine($"    package error: {err}");
                rows.Add(new
                {
                    id = v.Id,
                    label = v.Label,
                    asSaved = v.AsSaved,
                    inputs = JsonSerializer.Deserialize<JsonElement>(v.Inputs.ToJsonString()),
                    status,
                    reason = checks.FirstOrDefault(c => !c.Passed)?.Detail ?? (checks.Count == 0 ? "nothing in the contract could be compared for this case" : $"{checks.Count} checks agree"),
                    checks = checks.Select(c => new { kind = c.Kind, what = c.What, passed = c.Passed, detail = c.Detail }),
                });
            }

            var counts = $"{passed} passed, {failed} failed, {skipped} skipped";
            string verdict, summary; string? code = null;
            if (failed > 0) { verdict = "FAILED"; code = "equivalence.outputs_differ"; summary = $"`{routine}`: the package and the workbook differ on {failed} of {vectors.Count} cases ({counts})."; }
            else if (passed > 0) { verdict = "PASSED"; summary = $"`{routine}`: the package and the workbook agree on {passed} of {vectors.Count} cases ({counts})."; }
            else { verdict = "SKIPPED"; code = "equivalence.nothing_compared"; summary = $"`{routine}`: none of the {vectors.Count} cases had anything the gate could compare ({counts})."; }
            if (formulaNote is not null) summary += " " + formulaNote.Split('\n')[0];
            t.AppendLine().AppendLine($"=== verdict === {verdict} · {counts}");
            var metrics = new
            {
                mode = "workbook",
                sourceLanguage = "vb6",
                routine,
                live,
                workbook = split.Document,
                contract = new { claims = contract.ClaimIds, inputs = contract.Inputs.Count, selections = contract.Selections.Count, outputs = contract.Outputs.Count, requests = contract.Requests.Count },
                vectorCount = vectors.Count,
                passed, failed, skipped, errored = 0,
                vectors = rows,
            };
            return new Outcome(verdict, code, summary, metrics, t.ToString());
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to clean up {Dir}", tempDir); }
        }
    }

    /// <summary>Every check for one case. Pure, so the rules are unit-tested without Python.</summary>
    public static List<Check> Compare(WorkbookContract c, WorkbookEquivalenceVectors.Vector v, JsonObject? pkg, JsonObject? wb,
        Func<string, string, object?> saved, bool live)
    {
        var checks = new List<Check>();
        if (pkg is null) { checks.Add(new("package", "run", false, "the package returned no result for this case")); return checks; }
        if (pkg["error"]?.ToString() is { } err) { checks.Add(new("package", "run", false, $"the package raised {err}")); return checks; }
        var outputs = pkg["outputs"] as JsonObject;
        var sent = (pkg["requests"] as JsonArray ?? new()).OfType<JsonObject>().ToList();

        // Outside requests: what was sent against the signed template for this case.
        foreach (var r in c.Requests)
        {
            var values = v.Inputs.ToDictionary(kv => kv.Key, kv => kv.Value);
            var missing = new List<string>();
            var expected = RequestTemplate.Render(r.Body, values, missing);
            if (missing.Count > 0)
            {
                checks.Add(new("request", r.Id, false, $"the signed template names fields the cell contract does not have: {string.Join(", ", missing.Distinct())}"));
                continue;
            }
            var match = sent.FirstOrDefault(s => SameUrl(s["url"]?.ToString(), r.Url)) ?? (sent.Count == 1 ? sent[0] : null);
            if (match is null)
            {
                checks.Add(new("request", r.Id, false, sent.Count == 0
                    ? $"the package sent no request (the source sends {r.Method} {r.Url})"
                    : $"the package sent {sent.Count} requests, none to {r.Url} ({string.Join(", ", sent.Select(s => s["url"]?.ToString()))})"));
                continue;
            }
            var method = match["method"]?.ToString() ?? "";
            if (!method.Equals(r.Method, StringComparison.OrdinalIgnoreCase))
            {
                checks.Add(new("request", r.Id, false, $"method: the source sends {r.Method}, the package sent {method}"));
                continue;
            }
            var diff = RequestTemplate.FirstDifference(expected, match["body"]);
            checks.Add(new("request", r.Id, diff is null, diff ?? "the body is the source's, key for key"));
        }

        // Outputs.
        foreach (var o in c.Outputs)
        {
            var got = Value(outputs, o.Field);
            double? want = null; string from = "";
            if (o.ComputedBy == "formula")
            {
                want = Number(wb?["values"]?[$"{o.Sheet}!{o.Cell}"]);
                from = $"the workbook's formula in {o.Sheet}!{o.Cell}";
                if (want is null) continue;      // engine unavailable, failed, or the cell has no value: noted in the transcript, not a difference
                // A cell the workbook computes, that the package does not return (a display formula over what the routine
                // wrote, like =F17-F15): the workbook keeps computing it, so there is nothing of the package's to compare.
                if (got is null) continue;
            }
            else if (o.ComputedBy == "service")
            {
                if (!live || o.CompareWith is not { } cw) continue;                       // offline the service's answer is the gate's own
                want = Number(wb?["values"]?[$"{cw.Sheet}!{cw.Cell}"]);
                from = $"the workbook's own formula in {cw.Sheet}!{cw.Cell}";
                if (want is null) continue;
            }
            else
            {
                if (!v.AsSaved) continue;                                                 // the routine's own result exists only for the saved state
                want = Number(saved(o.Sheet, o.Cell));
                from = $"the value saved in {o.Sheet}!{o.Cell}";
                if (want is null) continue;
            }
            if (got is null) { checks.Add(new("output", o.Field, false, $"the package returned no {o.Field} (expected {Show(want)} from {from})")); continue; }
            var ok = want is not null && Math.Abs(got.Value - want.Value) <= o.Tolerance + 1e-9;
            checks.Add(new("output", o.Field, ok, $"package {Show(got)} vs {Show(want)} from {from}{(ok ? "" : $" (off by {Show(got - want)}, tolerance {Show(o.Tolerance)})")}"));
        }
        return checks;
    }

    /// <summary>
    /// The cells the routine itself writes (its own results and what the service returned), set to the package's values
    /// for the case before the workbook is recalculated — so a formula over them (=F17-F15) sees what the routine wrote,
    /// as it would in Excel, instead of the value saved from the last time the macro ran.
    /// </summary>
    internal static IEnumerable<WorkbookEquivalenceVectors.CellValue> WrittenByPackage(WorkbookContract c, JsonObject? pkg)
    {
        if (pkg?["outputs"] is not JsonObject outputs) yield break;
        foreach (var o in c.Outputs.Where(o => o.ComputedBy is "service" or "routine"))
            if (Value(outputs, o.Field) is { } v)
                yield return new(o.Sheet, o.Cell, JsonValue.Create(v));
    }

    /// <summary>The answer the gate gives in place of the outside service offline: the response fields, filled with neutral values.</summary>
    internal static JsonObject Canned(WorkbookContract.Request? r)
    {
        var body = new JsonObject();
        if (r is null) return body;
        foreach (var f in r.Response)
        {
            var parts = f.Path.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var at = body;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (at[parts[i]] is not JsonObject next) { next = new JsonObject(); at[parts[i]] = next; }
                at = next;
            }
            at[parts[^1]] = f.Type switch { "text" => "OK", "list" => new JsonArray(), _ => JsonValue.Create(1.0) };
        }
        return body;
    }

    private static bool SameUrl(string? a, string b)
    {
        static string Norm(string u) => u.Split('?')[0].TrimEnd('/').ToLowerInvariant();
        return a is not null && Norm(a) == Norm(b);
    }

    private static double? Value(JsonObject? o, string field)
    {
        if (o is null) return null;
        var node = o[field] ?? o.FirstOrDefault(kv => kv.Key.Equals(field, StringComparison.OrdinalIgnoreCase)).Value;
        return Number(node);
    }

    internal static double? Number(object? n) => n switch
    {
        null => null,
        double d => d,
        JsonValue j when j.GetValueKind() == JsonValueKind.Number => double.Parse(j.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture),
        JsonValue j when j.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    private static string Show(double? v) => v is null ? "nothing" : v.Value.ToString("0.######", CultureInfo.InvariantCulture);
}
