namespace Astra.Api.Knowledge;

/// <summary>A piece of evidence as the gate sees it: what it is, how far it can be trusted, and its text.</summary>
/// <param name="TrustTier">"structured" (parsed from code, SQL, a scheduler export, configuration) or "narrative" (what people said or wrote).</param>
/// <param name="Source">Where it is, for a person or a model to read (<c>db/ddl.sql L10-18</c>); not part of what a statement is checked against.</param>
public sealed record GateChunk(string ShortId, string Kind, string TrustTier, string Label, string Text, string? Source = null)
{
    public bool IsStructured => string.Equals(TrustTier, "structured", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Looks evidence up by short id, within one version of one project: an id from another version, or invented, is simply not found.</summary>
public interface IEvidenceLookup
{
    GateChunk? Find(string shortId);
}

public sealed class DictionaryEvidence : IEvidenceLookup
{
    private readonly IReadOnlyDictionary<string, GateChunk> _chunks;

    public DictionaryEvidence(IEnumerable<GateChunk> chunks)
    {
        _chunks = chunks.GroupBy(c => c.ShortId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public GateChunk? Find(string shortId) => _chunks.GetValueOrDefault(shortId);
}

public static class GateRules
{
    public const string NoCitation = "no-citation";
    public const string UnknownCitation = "unknown-citation";
    public const string IdentifierNotInEvidence = "identifier-not-in-evidence";
    public const string SupportNo = "support-no";
    public const string SupportNotChecked = "support-not-checked";
    public const string ConflictNotEvidenced = "conflict-not-evidenced";
}

public sealed record GateIssue(string ItemId, string Rule, string Message);

public sealed record DroppedStatement(StatementItem Item, IReadOnlyList<GateIssue> Issues);

public sealed record GateCounts(int Supported, int Unverified, int Conflict, int Dropped);

/// <param name="Document">What passed, each statement with its status; a section with nothing left is gone.</param>
/// <param name="Dropped">What did not pass, and why.</param>
/// <param name="Issues">Everything the gate said, including what it only noted (a citation removed, a check not run).</param>
public sealed record GateResult(StatementDocument Document, IReadOnlyList<DroppedStatement> Dropped, IReadOnlyList<GateIssue> Issues, GateCounts Counts)
{
    public bool AnythingDropped => Dropped.Count > 0;
}

/// <summary>
/// The rule that keeps a written deliverable honest. Pure: a document and the evidence of one version in, a
/// document in which every statement is grounded or gone out. Four checks, in this order:
/// <list type="number">
/// <item>every statement cites something;</item>
/// <item>every citation exists in this version (one that does not is removed, and a statement left with none is dropped);</item>
/// <item>every identifier the statement uses — a name, a code, a path, a URL, a setting — appears verbatim in a chunk it cites;</item>
/// <item>the cited evidence says what the statement says (a verdict from a separate check: <c>no</c> drops it, <c>partly</c> keeps it unverified).</item>
/// </list>
/// A statement that passes is then given its status by a rule, not by the writer: <b>Supported</b> when the claims
/// pipeline verified it or when structured evidence is cited and the support check said yes; <b>Conflict</b> only
/// when the conflict names two different pieces of evidence that exist (otherwise it is downgraded); everything
/// else <b>Unverified</b>. The first three checks need no model (<see cref="Check"/>); the fourth and the status
/// need the support verdicts (<see cref="Finish"/>); <see cref="Evaluate"/> runs both.
/// </summary>
public static class GroundingGate
{
    public static GateResult Evaluate(
        StatementDocument document,
        IEvidenceLookup evidence,
        IReadOnlyDictionary<string, string>? support,
        IReadOnlySet<string>? verifiedItemIds = null)
    {
        var first = Check(document, evidence);
        var second = Finish(first.Document, evidence, support, verifiedItemIds);
        return new GateResult(
            second.Document,
            first.Dropped.Concat(second.Dropped).ToList(),
            first.Issues.Concat(second.Issues).ToList(),
            new GateCounts(second.Counts.Supported, second.Counts.Unverified, second.Counts.Conflict, first.Dropped.Count + second.Dropped.Count));
    }

    // ── checks 1–3: no model needed ─────────────────────────────────────

    public static GateResult Check(StatementDocument document, IEvidenceLookup evidence)
    {
        var dropped = new List<DroppedStatement>();
        var issues = new List<GateIssue>();
        var sections = new List<StatementSection>();

        foreach (var section in document.Sections)
        {
            var kept = new List<StatementItem>();
            foreach (var item in section.Items)
            {
                var problems = new List<GateIssue>();
                var notes = new List<string>(item.Notes ?? Array.Empty<string>());

                if (item.Citations.Count == 0)
                {
                    problems.Add(new GateIssue(item.Id, GateRules.NoCitation, "It cites no evidence."));
                    Drop(item, problems, dropped, issues);
                    continue;
                }

                var cited = new List<GateChunk>();
                var valid = new List<string>();
                foreach (var id in item.Citations)
                {
                    if (evidence.Find(id) is { } chunk) { cited.Add(chunk); valid.Add(id); }
                    else
                    {
                        var message = $"The citation {id} is not evidence in this version of the project.";
                        issues.Add(new GateIssue(item.Id, GateRules.UnknownCitation, message));
                        notes.Add(message + " It was removed.");
                    }
                }
                if (valid.Count == 0)
                {
                    problems.Add(new GateIssue(item.Id, GateRules.UnknownCitation, "None of the evidence it cites exists in this version of the project."));
                    Drop(item, problems, dropped, issues);
                    continue;
                }

                var texts = cited.Select(c => IdentifierExtractor.Normalise(c.Label + "\n" + c.Text)).ToList();
                var missing = IdentifierExtractor.Extract(item.Text).Where(id => !IdentifierExtractor.IsIn(id, texts)).Select(id => id.Text).ToList();
                if (missing.Count > 0)
                {
                    problems.Add(new GateIssue(item.Id, GateRules.IdentifierNotInEvidence,
                        $"{string.Join(", ", missing.Select(m => $"'{m}'"))} {(missing.Count == 1 ? "does" : "do")} not appear in the evidence it cites."));
                    Drop(item, problems, dropped, issues);
                    continue;
                }

                kept.Add(item with { Citations = valid, Notes = notes.Count == 0 ? null : notes });
            }
            if (kept.Count > 0) sections.Add(new StatementSection(section.Heading, kept));
        }

        var result = document with { Sections = sections };
        return new GateResult(result, dropped, issues, new GateCounts(0, 0, 0, dropped.Count));
    }

    private static void Drop(StatementItem item, List<GateIssue> problems, List<DroppedStatement> dropped, List<GateIssue> issues)
    {
        dropped.Add(new DroppedStatement(item, problems));
        issues.AddRange(problems);
    }

    // ── check 4 and the status ──────────────────────────────────────────

    /// <param name="support">Item id → <c>yes</c>/<c>partly</c>/<c>no</c>; null when the support check was not run, in which case nothing can be Supported on its own evidence.</param>
    public static GateResult Finish(
        StatementDocument document,
        IEvidenceLookup evidence,
        IReadOnlyDictionary<string, string>? support,
        IReadOnlySet<string>? verifiedItemIds = null)
    {
        var dropped = new List<DroppedStatement>();
        var issues = new List<GateIssue>();
        var sections = new List<StatementSection>();
        int supported = 0, unverified = 0, conflicted = 0;

        var conflictsByItem = document.Conflicts.GroupBy(c => c.ItemId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var validConflicts = new List<StatementConflict>();

        foreach (var section in document.Sections)
        {
            var kept = new List<StatementItem>();
            foreach (var item in section.Items)
            {
                var notes = new List<string>(item.Notes ?? Array.Empty<string>());
                string? verdict = null;
                if (support is not null && support.TryGetValue(item.Id, out var v) && SupportVerdict.IsKnown(v)) verdict = v;

                if (verdict == SupportVerdict.No)
                {
                    var problem = new GateIssue(item.Id, GateRules.SupportNo, "The evidence it cites does not say this.");
                    Drop(item, new List<GateIssue> { problem }, dropped, issues);
                    continue;
                }

                var cited = item.Citations.Select(evidence.Find).Where(c => c is not null).Select(c => c!).ToList();
                var structured = cited.Any(c => c.IsStructured);

                string status;
                var conflictClaimed = conflictsByItem.TryGetValue(item.Id, out var conflict);
                var conflictReal = conflictClaimed && conflict!.EvidenceIds.Distinct(StringComparer.Ordinal).Count(id => evidence.Find(id) is not null) >= 2;

                if (conflictReal)
                {
                    status = StatementStatus.Conflict;
                    validConflicts.Add(conflict! with { EvidenceIds = conflict.EvidenceIds.Distinct(StringComparer.Ordinal).Where(id => evidence.Find(id) is not null).ToList() });
                    conflicted++;
                }
                else
                {
                    if (conflictClaimed)
                    {
                        var message = "A conflict was claimed but it does not name two pieces of evidence that exist, so it was not recorded.";
                        issues.Add(new GateIssue(item.Id, GateRules.ConflictNotEvidenced, message));
                        notes.Add(message);
                    }

                    var verified = verifiedItemIds?.Contains(item.Id) == true;
                    if (!conflictClaimed && (verified || (structured && verdict == SupportVerdict.Yes)))
                    {
                        status = StatementStatus.Supported;
                        supported++;
                    }
                    else
                    {
                        status = StatementStatus.Unverified;
                        unverified++;
                        if (!structured) notes.Add("Only what people said or wrote shows this; nothing structured does.");
                        if (verdict == SupportVerdict.Partly) notes.Add("The evidence says part of this, not all of it.");
                        if (verdict is null)
                        {
                            var message = "Whether the evidence says this was not checked.";
                            issues.Add(new GateIssue(item.Id, GateRules.SupportNotChecked, message));
                            notes.Add(message);
                        }
                    }
                }

                kept.Add(item with { Status = status, Support = verdict, Notes = notes.Count == 0 ? null : notes });
            }
            if (kept.Count > 0) sections.Add(new StatementSection(section.Heading, kept));
        }

        // A conflict that names a statement that is not there is not carried.
        var result = document with { Sections = sections, Conflicts = validConflicts };
        return new GateResult(result, dropped, issues, new GateCounts(supported, unverified, conflicted, dropped.Count));
    }

    // ── what is left over ───────────────────────────────────────────────

    /// <summary>
    /// After the last attempt, a statement that still cannot be grounded is not lost and not asserted: it becomes an
    /// open question ("Not confirmed by the evidence: …") for a person to settle.
    /// </summary>
    public static StatementDocument DemoteDropped(StatementDocument document, IReadOnlyList<DroppedStatement> dropped)
    {
        if (dropped.Count == 0) return document;
        var questions = new List<OpenQuestion>(document.OpenQuestions);
        foreach (var d in dropped)
        {
            var text = "Not confirmed by the evidence: " + d.Item.Text;
            var id = StatementJson.QuestionId(d.Item.SlotKey, text);
            if (questions.Any(q => q.Id == id)) continue;
            questions.Add(new OpenQuestion(id, text, d.Item.SlotKey));
        }
        return document with { OpenQuestions = questions };
    }
}
