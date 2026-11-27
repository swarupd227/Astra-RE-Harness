using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Office;
using Astra.Api.Validation;
using Astra.Api.Validation.WorkbookEquivalence;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>A workbook routine and its React + Python package are compared on the signed cell contract, case by case.</summary>
public class WorkbookEquivalenceTests
{
    private const string Dash = "1. Rating Dashboard";
    private const string Cov = "2. Coverage Selector";

    private static readonly string Spec = """
        {
          "routine": "CalculateApiPremium",
          "workbook_cell_contracts": [{
            "id": "WCC-1", "description": "The rating dashboard",
            "inputs": [
              { "field": "trip_cost", "label": "Trip Cost ($)", "sheet": "1. Rating Dashboard", "cell": "C5", "type": "number", "min": 1 },
              { "field": "age", "sheet": "1. Rating Dashboard", "cell": "$C$6", "type": "integer", "min": 0, "max": 99 },
              { "field": "month", "sheet": "1. Rating Dashboard", "cell": "C7", "type": "choice", "allowed": ["June", "July", "August"] }
            ],
            "selections": [
              { "field": "coverages", "label": "coverages", "sheet": "2. Coverage Selector", "range": "D6:D9", "marked": "Yes", "unmarked": "No",
                "id_from": "row - 1", "always_included": [4] }
            ],
            "outputs": [
              { "field": "total", "sheet": "1. Rating Dashboard", "cell": "F15", "computed_by": "formula" },
              { "field": "api_premium", "sheet": "1. Rating Dashboard", "cell": "F17", "computed_by": "service", "compare_with": "'1. Rating Dashboard'!F15" },
              { "field": "status", "sheet": "1. Rating Dashboard", "cell": "F21", "computed_by": "routine" }
            ],
            "citations": [{ "lines": "39-89" }]
          }],
          "outside_requests": [{
            "id": "OSR-1", "description": "rate the trip", "method": "POST", "url": "https://rater.example/api/v1/travel/rate",
            "body": { "tripCost": "{trip_cost}", "age": "{age}", "month": "{month}", "selected": [4, "...{coverages}"], "advanced": { "perils": [1, 2] } },
            "response": [{ "path": "premium", "type": "number", "writes": "F17" }, { "path": "raterVersion", "type": "text" }],
            "citations": [{ "lines": "60-70" }]
          }]
        }
        """;

    private static WorkbookContract Contract() => WorkbookContract.FromSpec(JsonDocument.Parse(Spec).RootElement);

    private static Dictionary<string, List<WorkbookInputs.Cell>> Sheets() => new(StringComparer.OrdinalIgnoreCase)
    {
        [Dash] = new()
        {
            new("C5", 6666.0, false), new("C6", 26.0, false), new("C7", "July", false),
            new("F15", 136.275606, true), new("F17", 136.28, false), new("F21", 7.0, false),
        },
        [Cov] = new() { new("D6", "Yes", false), new("D7", "Yes", false), new("D8", "No", false), new("D9", "Yes", false) },
    };

    [Fact]
    public void The_contract_is_read_from_the_signed_spec()
    {
        var c = Contract();
        Assert.Equal(new[] { "WCC-1", "OSR-1" }, c.ClaimIds);
        Assert.Equal(3, c.Inputs.Count);
        Assert.Equal("C6", c.Inputs[1].Cell);                                   // $C$6 normalised
        Assert.Equal(new[] { "June", "July", "August" }, c.Inputs[2].Allowed);
        var s = Assert.Single(c.Selections);
        Assert.Equal(("D", 6, 9), (s.Column, s.FirstRow, s.LastRow));
        Assert.Equal(-1, s.IdOffset);
        Assert.Equal(new[] { 4 }, s.AlwaysIncluded);
        Assert.Equal((Dash, "F15"), c.Outputs[1].CompareWith!.Value);
        var r = Assert.Single(c.Requests);
        Assert.Equal("POST", r.Method);
        Assert.Equal(2, r.Response.Count);
        Assert.True(WorkbookContract.FromSpec(JsonDocument.Parse("{\"invariants\":[]}").RootElement).IsEmpty);
    }

    [Fact]
    public void An_id_rule_is_followed_from_a_row_or_from_a_column_and_an_unknown_rule_leaves_the_list_out()
    {
        var byColumn = WorkbookContract.FromSpec(JsonDocument.Parse("""
            {"workbook_cell_contracts":[{"selections":[
              {"field":"perils","sheet":"P","range":"D5:D9","id_from":"column B"},
              {"field":"odd","sheet":"P","range":"D5:D9","id_from":"the third one"}]}]}
            """).RootElement);
        var s = Assert.Single(byColumn.Selections);
        Assert.Equal("B", s.IdColumn);
        Assert.Equal(12, WorkbookContract.IdFor(s, 5, a => a == "B5" ? 12.0 : null));
        Assert.Null(WorkbookContract.IdFor(s, 6, _ => "not a number"));
        Assert.Equal(5, WorkbookContract.IdFor(Contract().Selections[0], 6, _ => null));
    }

    [Fact]
    public void The_cases_start_from_the_saved_workbook_and_change_one_thing_at_a_time()
    {
        var v = WorkbookEquivalenceVectors.Build(Contract(), Sheets());
        var saved = v[0];
        Assert.True(saved.AsSaved);
        Assert.Equal(6666.0, saved.Inputs["trip_cost"]!.GetValue<double>());
        Assert.Equal(26L, saved.Inputs["age"]!.GetValue<long>());
        Assert.Equal("[5,6,8]", saved.Inputs["coverages"]!.ToJsonString());      // rows 6,7,9 marked → ids row-1; 4 is always sent, not marked
        Assert.Contains(saved.Cells, c => c.Sheet == Cov && c.Cell == "D8" && c.Value!.ToString() == "No");
        Assert.Contains(v, x => x.Label.StartsWith("Trip Cost ($) doubled") && x.Inputs["trip_cost"]!.GetValue<double>() == 13332);
        Assert.Contains(v, x => x.Label.StartsWith("age doubled") && x.Inputs["age"]!.GetValue<long>() == 52);
        Assert.Contains(v, x => x.Inputs["month"]!.ToString() == "June");
        Assert.Contains(v, x => x.Label == "no coverages marked" && x.Inputs["coverages"]!.AsArray().Count == 0
                                && x.Cells.Where(c => c.Sheet == Cov).All(c => c.Value!.ToString() == "No"));
        Assert.Contains(v, x => x.Label == "only coverages 5 marked");
        Assert.True(v.Count <= WorkbookEquivalenceVectors.MaxVectors);
        Assert.Equal(v.Count, v.Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public void A_number_within_bounds_stays_within_them()
    {
        var c = WorkbookContract.FromSpec(JsonDocument.Parse("""
            {"workbook_cell_contracts":[{"inputs":[{"field":"age","sheet":"S","cell":"A1","type":"integer","min":18,"max":60}]}]}
            """).RootElement);
        var v = WorkbookEquivalenceVectors.Build(c, new Dictionary<string, List<WorkbookInputs.Cell>> { ["S"] = new() { new("A1", 40.0, false) } });
        Assert.Equal(new long[] { 40, 60, 20 }, v.Select(x => x.Inputs["age"]!.GetValue<long>()));
    }

    [Fact]
    public void The_request_template_renders_fields_and_spreads_selections_and_differences_are_named()
    {
        var c = Contract();
        var values = new Dictionary<string, JsonNode?>
        {
            ["trip_cost"] = 6666.0, ["age"] = 26L, ["month"] = "July", ["coverages"] = new JsonArray(5, 6, 8),
        };
        var missing = new List<string>();
        var body = RequestTemplate.Render(c.Requests[0].Body, values, missing)!;
        Assert.Empty(missing);
        Assert.Equal("""{"tripCost":6666,"age":26,"month":"July","selected":[4,5,6,8],"advanced":{"perils":[1,2]}}""", body.ToJsonString());

        Assert.Null(RequestTemplate.FirstDifference(body, JsonNode.Parse("""{"age":26.0,"tripCost":6666,"month":"July","selected":[4,5,6,8],"advanced":{"perils":[1,2]}}""")));
        Assert.Contains("$.advanced.exclusion: sent but not in", RequestTemplate.FirstDifference(
            JsonNode.Parse("""{"advanced":{"perils":[1]}}"""), JsonNode.Parse("""{"advanced":{"perils":[1],"exclusion":"x"}}""")));
        Assert.Contains("$.penaltyDays: missing", RequestTemplate.FirstDifference(
            JsonNode.Parse("""{"penaltyDays":30}"""), JsonNode.Parse("""{}""")));
        Assert.Contains("$.selected[1]", RequestTemplate.FirstDifference(JsonNode.Parse("[4,5]"), JsonNode.Parse("[4,6]"), "$.selected"));
        Assert.NotNull(RequestTemplate.FirstDifference(JsonNode.Parse("""{"age":26}"""), JsonNode.Parse("""{"age":"26"}""")));   // text is not a number

        RequestTemplate.Render(JsonNode.Parse("""{"x":"{nope}"}"""), values, missing);
        Assert.Equal(new[] { "nope" }, missing);
    }

    private static JsonObject Pkg(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void A_case_passes_when_the_request_is_the_sources_and_the_outputs_match_the_workbook()
    {
        var c = Contract();
        var saved = WorkbookEquivalenceVectors.Build(c, Sheets())[0];
        var good = Pkg("""
            {"outputs":{"total":136.28,"api_premium":1.0,"status":7},
             "requests":[{"method":"POST","url":"https://rater.example/api/v1/travel/rate",
                          "body":{"tripCost":6666,"age":26,"month":"July","selected":[4,5,6,8],"advanced":{"perils":[1,2]}}}]}
            """);
        var wb = Pkg("""{"values":{"1. Rating Dashboard!F15":136.275606}}""");
        object? SavedCell(string s, string cell) => Sheets()[s].FirstOrDefault(x => x.Address == cell)?.Value;

        var offline = WorkbookEquivalenceRunner.Compare(c, saved, good, wb, SavedCell, live: false);
        Assert.All(offline, x => Assert.True(x.Passed, x.Detail));
        Assert.Contains(offline, x => x.Kind == "request");
        Assert.Contains(offline, x => x.What == "total");                        // formula output, within 0.005
        Assert.Contains(offline, x => x.What == "status");                       // the routine's own value, saved case
        Assert.DoesNotContain(offline, x => x.What == "api_premium");            // offline the service answer is the gate's own

        var live = WorkbookEquivalenceRunner.Compare(c, saved, good, wb, SavedCell, live: true);
        Assert.Contains(live, x => x.What == "api_premium" && !x.Passed);        // 1.0 vs the workbook's 136.28
    }

    [Fact]
    public void A_case_fails_on_a_wrongly_nested_body_a_missing_request_or_a_package_error()
    {
        var c = Contract();
        var saved = WorkbookEquivalenceVectors.Build(c, Sheets())[0];
        object? None(string s, string cell) => null;
        var nested = WorkbookEquivalenceRunner.Compare(c, saved, Pkg("""
            {"outputs":{},"requests":[{"method":"POST","url":"https://rater.example/api/v1/travel/rate",
              "body":{"tripCost":6666,"age":26,"month":"July","selected":[4,5,6,8],"advanced":{"perils":[1,2]},"exclusion":"x"}}]}
            """), null, None, false);
        Assert.Contains(nested, x => x.Kind == "request" && !x.Passed && x.Detail.Contains("$.exclusion"));

        var none = WorkbookEquivalenceRunner.Compare(c, saved, Pkg("""{"outputs":{},"requests":[]}"""), null, None, false);
        Assert.Contains(none, x => x.Kind == "request" && !x.Passed && x.Detail.Contains("sent no request"));

        var raised = WorkbookEquivalenceRunner.Compare(c, saved, Pkg("""{"error":"ValueError: boom","requests":[]}"""), null, None, false);
        Assert.False(Assert.Single(raised).Passed);
    }

    [Fact]
    public void The_workbook_is_recalculated_over_what_the_routine_wrote_and_a_formula_the_package_does_not_return_is_not_a_difference()
    {
        var c = Contract();
        var written = WorkbookEquivalenceRunner.WrittenByPackage(c, Pkg("""{"outputs":{"api_premium":142.29,"status":"OK","total":1}}""")).ToList();
        var cell = Assert.Single(written);                                       // the service's value; 'status' is not a number, 'total' is the workbook's
        Assert.Equal((Dash, "F17", 142.29), (cell.Sheet, cell.Cell, cell.Value!.GetValue<double>()));

        var saved = WorkbookEquivalenceVectors.Build(c, Sheets())[0];
        var checks = WorkbookEquivalenceRunner.Compare(c, saved, Pkg("""{"outputs":{"api_premium":1.0},"requests":[]}"""),
            Pkg("""{"values":{"1. Rating Dashboard!F15":136.275606}}"""), (_, _) => null, live: false);
        Assert.DoesNotContain(checks, x => x.What == "total");                   // not returned by the package: the workbook keeps it
    }

    [Fact]
    public void A_package_for_a_signed_cell_contract_must_offer_its_entry_point_and_the_repair_digest_carries_why()
    {
        var spec = JsonDocument.Parse(Spec).RootElement;
        var missing = WorkbookContract.EntryPointProblem(spec, _ => null);
        Assert.Contains("backend/app/contract.py is missing", missing);
        Assert.Contains("is missing", WorkbookContract.EntryPointProblem(spec, p => p == "backend/app/other.py" ? "x" : null));
        Assert.Contains("no top-level", WorkbookContract.EntryPointProblem(spec, _ => "class C:\n    def run(self, inputs): pass\n"));
        Assert.Null(WorkbookContract.EntryPointProblem(spec, _ => "import json\n\ndef run(inputs: dict) -> dict:\n    return {}\n"));
        Assert.Null(WorkbookContract.EntryPointProblem(JsonDocument.Parse("{\"invariants\":[]}").RootElement, _ => null));

        var digest = GateFailureDigest.Parse("COMPILE", "=== cell contract entry point ===\nerror WCC001: " + missing + "\n");
        var e = Assert.Single(digest.Errors);
        Assert.Equal("WCC001", e.Code);
        Assert.Contains("def run(inputs: dict) -> dict", digest.ToRepairHint());
    }

    [Fact]
    public void The_canned_answer_has_every_response_field()
    {
        var canned = WorkbookEquivalenceRunner.Canned(Contract().Requests[0]);
        Assert.Equal("""{"premium":1,"raterVersion":"OK"}""", canned.ToJsonString());
    }

    [Fact]
    public void The_vb6_schema_declares_the_workbook_kinds_and_the_workbook_prompt_asks_for_them()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Astra.Api", "Llm"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var llm = Path.Combine(dir!.FullName, "src", "Astra.Api", "Llm");
        using var vb6 = JsonDocument.Parse(File.ReadAllText(Path.Combine(llm, "Schemas", "vb6.schema.json")));
        Assert.Equal("1.1", vb6.RootElement.GetProperty("schemaVersion").GetString());
        var kinds = vb6.RootElement.GetProperty("claimKinds").EnumerateArray()
            .Select(k => (Id: k.GetProperty("id").GetString()!, Field: k.GetProperty("specJsonField").GetString()!)).ToList();
        Assert.Contains(("workbookCellContract", "workbook_cell_contracts"), kinds);
        Assert.Contains(("outsideRequest", "outside_requests"), kinds);
        var hints = vb6.RootElement.GetProperty("promptHints").GetProperty("preferredClaimTypes").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(kinds.Select(k => k.Id).OrderBy(x => x), hints.OrderBy(x => x));
        var prompt = File.ReadAllText(Path.Combine(llm, "Prompts", "vb6", "office-vba", "extract.v2.md"));
        Assert.Contains("version: v2.0", prompt);
        foreach (var (_, field) in kinds)
            Assert.True(prompt.Contains($"\"{field}\""), $"the workbook prompt does not ask for {field}");
        Assert.True(WorkbookEquivalenceRunner.Applies(new Astra.Api.Persistence.Entities.Scaffold
        {
            TargetPlatform = CompileValidator.ReactPythonStack,
            Spec = new Astra.Api.Persistence.Entities.Spec
            {
                Subroutine = new Astra.Api.Persistence.Entities.Subroutine
                {
                    SourceLanguage = "vb6",
                    SourceFile = new Astra.Api.Persistence.Entities.SourceFile { RelativePath = "Rater.xlsm/TravelRaterApi.bas" },
                },
            },
        }));
    }
}
