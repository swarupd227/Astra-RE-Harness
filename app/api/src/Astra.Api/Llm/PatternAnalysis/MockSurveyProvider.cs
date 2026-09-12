using System.Text.RegularExpressions;

namespace Astra.Api.Llm.PatternAnalysis;

/// <summary>
/// Deterministic, offline survey digest from name/shape heuristics, so the
/// mock stack exercises the whole survey → cluster pipeline end to end.
/// Honest about being a stub: purposes say so.
/// </summary>
public sealed class MockSurveyProvider : ISurveyProvider
{
    private static readonly Regex Accessor = new(@"(^|[.:_])(get|set|is|has)[A-Z_]", RegexOptions.Compiled);
    private static readonly Regex Io = new(@"read|write|open|close|load|save|fetch|select|insert|update|delete|query", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Handler = new(@"click|change|handle|on[A-Z]|event|button", RegexOptions.Compiled);
    private static readonly Regex Calc = new(@"calc|compute|sum|total|rate|price|tax", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name => "mock";

    public Task<SurveyResult> SurveyAsync(SurveyRequest request, CancellationToken ct)
    {
        var loc = Math.Max(1, request.LineEnd - request.LineStart + 1);
        var kinds = new List<string> { "invariant" };
        string hint;
        if (Accessor.IsMatch(request.Name) && loc <= 8) { kinds = new List<string> { "propertyAccessor" }; hint = "trivial-accessor"; }
        else if (Io.IsMatch(request.Name)) { kinds.Add("ioSideEffect"); kinds.Add("sideEffect"); hint = "data-access-query"; }
        else if (Handler.IsMatch(request.Name)) { kinds.Add("eventHandlerContract"); kinds.Add("sideEffect"); hint = "ui-event-handler"; }
        else if (Calc.IsMatch(request.Name)) { kinds.Add("edgeCase"); hint = "calculation"; }
        else { kinds.Add("edgeCase"); hint = "other"; }

        var complexity = loc <= 3 ? "trivial" : loc <= 20 ? "simple" : loc <= 80 ? "moderate" : "complex";
        var digest = new SurveyDigest(
            $"Mock survey — {request.Name} ({loc} lines, {request.Callees.Count} callee(s)); no LLM judging performed.",
            kinds, hint, Array.Empty<SurveyDataAccess>(), Array.Empty<string>(), complexity);

        return Task.FromResult(new SurveyResult(digest, 0, 0, 0, 0, "mock", "survey-digest", "mock", 0));
    }
}
