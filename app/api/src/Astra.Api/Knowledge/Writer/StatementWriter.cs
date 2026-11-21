using System.Text;
using System.Text.Json;
using Astra.Api.Docs;
using Microsoft.Extensions.Options;

namespace Astra.Api.Knowledge;

/// <summary>The two prompts the knowledge pipeline sends, read once at startup (<c>Llm/Prompts/knowledge/</c>).</summary>
public sealed class StatementPrompts
{
    public string Writer { get; }
    public string Support { get; }

    public StatementPrompts(string writer, string support)
    {
        Writer = writer;
        Support = support;
    }

    public static StatementPrompts Load(string contentRoot) => new(
        DocPromptAssets.ReadPrompt(contentRoot, "knowledge", "statement-writer.v1.md"),
        DocPromptAssets.ReadPrompt(contentRoot, "knowledge", "support-check.v1.md"));
}

/// <param name="DeliverableKind">The deliverable the statements are for (<c>runbook</c>).</param>
/// <param name="SlotKey">The slot they fill; every statement written carries it.</param>
/// <param name="Instruction">What to write about, in a sentence or two (the slot's brief).</param>
/// <param name="Evidence">The pieces of evidence the writer may cite, and the only things it is shown.</param>
/// <param name="ResolveMore">Finds, within this version of the project, evidence the writer cited that it was not shown; an id it cannot find is then known to be invented.</param>
/// <param name="VerifiedItemIds">Statements the claims pipeline has already verified.</param>
/// <param name="ReplayBase">Names this run's calls for replay (<c>step:item</c>).</param>
public sealed record StatementRequest(
    string DeliverableKind,
    string SlotKey,
    string SlotLabel,
    string Instruction,
    IReadOnlyList<GateChunk> Evidence,
    Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyList<GateChunk>>>? ResolveMore = null,
    IReadOnlySet<string>? VerifiedItemIds = null,
    string ReplayBase = "");

/// <param name="Document">What passed the gate, each statement with its status; what could not be grounded is in its open questions.</param>
/// <param name="Dropped">Every statement the gate threw away over the attempts, and why.</param>
/// <param name="Notes">What happened that a person should know (a statement cut, a check that could not run, a retry).</param>
public sealed record StatementOutcome(
    StatementDocument Document,
    IReadOnlyList<DroppedStatement> Dropped,
    IReadOnlyList<GateIssue> Issues,
    GateCounts Counts,
    int Attempts,
    IReadOnlyList<DocCallRecord> Calls,
    IReadOnlyList<string> Notes);

/// <summary>
/// Writes the statements for one slot. The model writes; a program decides what stands. The steps: one forced
/// tool-use call at temperature 0 with only the given evidence in front of it; the document is read; the gate's
/// first three checks (cited, cited evidence exists, names found verbatim) throw out what fails them; what is left
/// goes to a separate, smaller model that says whether the cited evidence says what each statement says (in batches);
/// the gate then gives every survivor its status. Whatever was thrown away gets one retry that rewrites only that
/// part; what still cannot be grounded becomes an open question rather than a claim. Every call is returned so it can
/// be recorded and priced like any other model call.
/// </summary>
public sealed class StatementWriter
{
    public const string PromptId = "knowledge-statement-writer";
    public const string PromptVersion = "v1.0";
    public const string ToolName = "write_statements";

    public static readonly object WriterToolSchema = new
    {
        type = "object",
        properties = new
        {
            title = new { type = "string" },
            sections = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        heading = new { type = "string" },
                        items = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                properties = new
                                {
                                    text = new { type = "string", description = "One claim, one or two sentences. Names, codes and paths exactly as in the evidence, in backticks." },
                                    citations = new { type = "array", items = new { type = "string" }, description = "Evidence ids this rests on, exactly as given." },
                                },
                                required = new[] { "text", "citations" },
                            },
                        },
                    },
                    required = new[] { "heading", "items" },
                },
            },
            openQuestions = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        text = new { type = "string" },
                        citations = new { type = "array", items = new { type = "string" } },
                    },
                    required = new[] { "text" },
                },
            },
        },
        required = new[] { "sections" },
    };

    private readonly IDocWriter _writer;
    private readonly DocsOptions _docs;
    private readonly KnowledgeOptions _options;
    private readonly StatementPrompts _prompts;
    private readonly IStatementFixtures _fixtures;
    private readonly ILogger<StatementWriter> _logger;

    public StatementWriter(
        IDocWriter writer,
        IOptions<DocsOptions> docs,
        IOptions<KnowledgeOptions> options,
        StatementPrompts prompts,
        IStatementFixtures fixtures,
        ILogger<StatementWriter> logger)
    {
        _writer = writer;
        _docs = docs.Value;
        _options = options.Value;
        _prompts = prompts;
        _fixtures = fixtures;
        _logger = logger;
    }

    public async Task<StatementOutcome> WriteAsync(StatementRequest request, CancellationToken ct)
    {
        var calls = new List<DocCallRecord>();
        var notes = new List<string>();
        var droppedAll = new List<DroppedStatement>();
        var issuesAll = new List<GateIssue>();
        var accepted = StatementDocument.Empty(request.DeliverableKind, request.SlotLabel);
        var known = new List<GateChunk>(request.Evidence);
        IReadOnlyList<DroppedStatement> toFix = Array.Empty<DroppedStatement>();
        // What the last answer that could be read still failed to ground: those, and only those, become open questions.
        IReadOnlyList<DroppedStatement> stillDropped = Array.Empty<DroppedStatement>();
        var attempts = 0;
        var maxAttempts = Math.Max(1, _options.MaxAttempts);

        while (attempts < maxAttempts)
        {
            attempts++;
            var write = BuildWriteRequest(request, attempts, accepted, toFix);
            var result = await _writer.WriteAsync(write, ct);
            calls.Add(new DocCallRecord(write, result));

            var parsed = StatementJson.Parse(result.ToolInputJson, request.DeliverableKind, request.SlotKey);
            if (parsed.Document is null)
            {
                notes.Add($"Attempt {attempts}: the answer could not be read ({string.Join(" ", parsed.Problems)}).");
                if (attempts < maxAttempts) { toFix = Array.Empty<DroppedStatement>(); continue; }
                break;
            }
            notes.AddRange(parsed.Problems.Select(p => $"Attempt {attempts}: {p}"));

            // Every statement of this call is about the slot it was asked about, whatever the model put.
            var document = parsed.Document with
            {
                Sections = parsed.Document.Sections
                    .Select(s => s with { Items = s.Items.Select(i => i with { SlotKey = request.SlotKey }).ToList() }).ToList(),
                OpenQuestions = parsed.Document.OpenQuestions.Select(q => q with { SlotKey = request.SlotKey }).ToList(),
            };

            // Evidence the writer cited without being shown it is looked up in the version; whatever is still missing does not exist.
            var cited = document.Items.SelectMany(i => i.Citations).Distinct(StringComparer.Ordinal)
                .Where(id => !known.Any(k => k.ShortId == id)).ToList();
            if (cited.Count > 0 && request.ResolveMore is not null)
                known.AddRange(await request.ResolveMore(cited, ct));
            var lookup = new DictionaryEvidence(known);

            var check = GroundingGate.Check(document, lookup);
            var support = await CheckSupportAsync(request, lookup, check.Document, attempts, calls, notes, ct);
            var final = GroundingGate.Finish(check.Document, lookup, support, request.VerifiedItemIds);

            var dropped = check.Dropped.Concat(final.Dropped).ToList();
            droppedAll.AddRange(dropped);
            issuesAll.AddRange(check.Issues.Concat(final.Issues));
            accepted = Merge(accepted, final.Document);
            stillDropped = dropped;

            if (dropped.Count == 0) break;
            if (attempts < maxAttempts)
            {
                notes.Add($"Attempt {attempts}: {dropped.Count} statement{(dropped.Count == 1 ? " was" : "s were")} thrown away; writing {(dropped.Count == 1 ? "it" : "them")} again.");
                toFix = dropped;
            }
        }

        // What still could not be grounded is not asserted and not lost: it becomes a question for a person.
        // (If the last attempt could not be read at all, what the one before it left is what there is.)
        var document2 = GroundingGate.DemoteDropped(accepted, stillDropped);
        var counts = new GateCounts(
            document2.Items.Count(i => i.Status == StatementStatus.Supported),
            document2.Items.Count(i => i.Status == StatementStatus.Unverified),
            document2.Items.Count(i => i.Status == StatementStatus.Conflict),
            droppedAll.Count);
        return new StatementOutcome(document2, droppedAll, issuesAll, counts, attempts, calls, notes);
    }

    // ── the write call ──────────────────────────────────────────────────

    private DocWriteRequest BuildWriteRequest(StatementRequest request, int attempt, StatementDocument accepted, IReadOnlyList<DroppedStatement> toFix)
    {
        var replayKey = ReplayKeyFor(request, "write", attempt);
        return new DocWriteRequest(
            PromptId, PromptVersion,
            new[] { _prompts.Writer },
            BuildUserMessage(request, accepted, toFix),
            ToolName,
            "Write the statements for this slot, each citing the evidence it rests on.",
            WriterToolSchema,
            string.IsNullOrWhiteSpace(_options.WriterModel) ? _docs.SonnetModel : _options.WriterModel!,
            _options.WriterMaxTokens,
            "knowledge-writer",
            () => _fixtures.Find(replayKey) ?? MockAnswer(request),
            ReplayKey: replayKey,
            Temperature: 0);
    }

    public static string ReplayKeyFor(StatementRequest request, string purpose, int attempt)
    {
        var baseKey = string.IsNullOrWhiteSpace(request.ReplayBase) ? $"{request.DeliverableKind}:{request.SlotKey}" : request.ReplayBase;
        return $"{baseKey}:{purpose}:{attempt}";
    }

    public string BuildUserMessage(StatementRequest request, StatementDocument accepted, IReadOnlyList<DroppedStatement> toFix)
    {
        var sb = new StringBuilder();
        sb.Append("Deliverable: ").Append(request.DeliverableKind).Append('\n');
        sb.Append("Slot: ").Append(request.SlotKey).Append(" — ").Append(request.SlotLabel).Append('\n');
        sb.Append("Write: ").Append(request.Instruction.Trim()).Append("\n\n");
        sb.Append("Evidence (cite by the id in square brackets; this is all you may rely on):\n");

        foreach (var chunk in request.Evidence.Take(Math.Max(1, _options.MaxEvidenceChunks)))
        {
            sb.Append("\n[").Append(chunk.ShortId).Append("] ").Append(chunk.Kind).Append(" · ").Append(chunk.TrustTier);
            if (!string.IsNullOrWhiteSpace(chunk.Source)) sb.Append(" · ").Append(chunk.Source);
            else if (!string.IsNullOrWhiteSpace(chunk.Label)) sb.Append(" · ").Append(chunk.Label);
            sb.Append('\n').Append(Clip(chunk.Text, _options.MaxChunkChars)).Append("\n---\n");
        }

        if (accepted.Items.Any())
        {
            sb.Append("\nAlready accepted — do not repeat these:\n");
            foreach (var item in accepted.Items) sb.Append("- ").Append(item.Text).Append('\n');
        }
        if (toFix.Count > 0)
        {
            sb.Append("\nThese statements from your earlier answer were thrown away. Rewrite each one so that it rests on the evidence above and nothing else, or turn it into an open question if the evidence does not settle it:\n");
            foreach (var d in toFix)
                sb.Append("- \"").Append(d.Item.Text).Append("\" — ").Append(string.Join(" ", d.Issues.Select(i => i.Message))).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The offline answer when no fixture exists for the call: one statement per piece of evidence, taken from its
    /// own first line, so everything downstream (the gate, the support check, the statuses) has real input.
    /// </summary>
    internal static string MockAnswer(StatementRequest request)
    {
        var items = request.Evidence.Take(8)
            .Select(c => new { text = FirstLine(c), citations = new[] { c.ShortId } })
            .Where(i => i.text.Length > 0)
            .ToList();
        return JsonSerializer.Serialize(new
        {
            title = request.SlotLabel,
            sections = items.Count == 0 ? Array.Empty<object>() : new object[] { new { heading = request.SlotLabel, items } },
            openQuestions = Array.Empty<object>(),
        });
    }

    private static string FirstLine(GateChunk chunk)
    {
        foreach (var raw in chunk.Text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 3) return line.Length <= 200 ? line : line[..199] + "…";
        }
        return chunk.Label.Trim();
    }

    // ── the support check ───────────────────────────────────────────────

    private async Task<IReadOnlyDictionary<string, string>?> CheckSupportAsync(
        StatementRequest request, IEvidenceLookup lookup, StatementDocument survivors, int attempt, List<DocCallRecord> calls, List<string> notes, CancellationToken ct)
    {
        var claims = survivors.Items
            .Select(i => new SupportClaim(
                i.Id, i.Text,
                i.Citations.Select(lookup.Find).Where(c => c is not null)
                    .Select(c => new SupportEvidence(c!.ShortId, c.Text)).ToList()))
            .ToList();
        if (claims.Count == 0) return new Dictionary<string, string>();

        var verdicts = new Dictionary<string, string>(StringComparer.Ordinal);
        var requests = SupportCheck.BuildRequests(claims, _prompts.Support, _options.SupportModel, $"{ReplayKeyFor(request, "write", attempt)}");
        foreach (var call in requests)
        {
            try
            {
                var result = await _writer.WriteAsync(call, ct);
                calls.Add(new DocCallRecord(call, result));
                foreach (var (id, verdict) in SupportCheck.Parse(result.ToolInputJson)) verdicts[id] = verdict;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Reported, not hidden: the statements in this batch are left unchecked, so none of them can be Supported.
                _logger.LogWarning(ex, "The support check for {Slot} failed", request.SlotKey);
                notes.Add($"The check of whether the evidence says what the statements say failed ({ex.Message}); those statements are unverified.");
            }
        }
        return verdicts;
    }

    // ── merging attempts ────────────────────────────────────────────────

    private static StatementDocument Merge(StatementDocument kept, StatementDocument next)
    {
        if (!kept.Items.Any() && kept.Sections.Count == 0 && kept.OpenQuestions.Count == 0) return next;

        var have = kept.Items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var sections = kept.Sections.ToList();
        foreach (var s in next.Sections)
        {
            var fresh = s.Items.Where(i => !have.Contains(i.Id)).ToList();
            if (fresh.Count == 0) continue;
            var existing = sections.FindIndex(x => string.Equals(x.Heading, s.Heading, StringComparison.Ordinal));
            if (existing >= 0) sections[existing] = sections[existing] with { Items = sections[existing].Items.Concat(fresh).ToList() };
            else sections.Add(s with { Items = fresh });
        }

        var questions = kept.OpenQuestions.ToList();
        foreach (var q in next.OpenQuestions)
            if (!questions.Any(x => x.Id == q.Id)) questions.Add(q);

        return kept with
        {
            Title = kept.Title.Length > 0 ? kept.Title : next.Title,
            Sections = sections,
            Conflicts = kept.Conflicts.Concat(next.Conflicts.Where(c => !kept.Conflicts.Any(k => k.ItemId == c.ItemId))).ToList(),
            OpenQuestions = questions,
        };
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
