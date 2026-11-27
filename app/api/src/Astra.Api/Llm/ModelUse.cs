namespace Astra.Api.Llm;

/// <summary>
/// Which job a model call served, named the way a user would (the LLM Provider page lists every model in use and
/// where, so "Model: claude-sonnet-…" is not mistaken for the whole story). Pure: derived from the call's prompt id.
/// </summary>
public static class ModelUse
{
    public static string PurposeOf(string? promptTemplateId)
    {
        var p = (promptTemplateId ?? "").ToLowerInvariant();
        if (p.Length == 0) return "Other";
        if (p.Contains("copilot") || p.Contains("orchestrat") || p.Contains("narrat")) return "Conversation (agents)";
        if (p.Contains("survey") || p.Contains("pattern") || p.Contains("archetype") || p.Contains("cluster")) return "Pattern analysis";
        if (p.Contains("quality") || p.Contains("triage") || p.Contains("code-review")) return "Code review";
        if (p.Contains("statement") || p.Contains("knowledge") || p.Contains("grounding") || p.Contains("support")) return "Knowledge capture";
        if (p.Contains("assess")) return "Assessment";
        if (p.Contains("doc") || p.Contains("critic") || p.Contains("overview") || p.Contains("catalog")) return "Documents";
        if (p.Contains("extract") || p.Contains("spec")) return "Spec extraction";
        if (p.Contains("scaffold") || p.Contains("faithful") || p.Contains("transform") || p.Contains("generat")
            || p.Contains("repair") || p.Contains("fix")) return "Code generation";
        return "Other";
    }
}
