using System.Text.RegularExpressions;
using Astra.Api.Auth;
using Astra.Api.Docs;
using Astra.Api.Evidence;
using Astra.Api.Knowledge;
using Astra.Api.Persistence;

namespace Astra.Api.Endpoints;

public sealed record SlotVersionBody(bool Approved, List<string>? Statuses);

public sealed record SlotEvidenceBody(string? SlotKey, List<SlotVersionBody>? Versions);

public sealed record CompletenessBody(
    Dictionary<string, List<string>>? Derived,
    Dictionary<string, int>? ExpectedCounts,
    List<SlotEvidenceBody>? Slots);

/// <param name="Deliverable">The deliverable the statements are for (an id from a pack, or any short name).</param>
/// <param name="SlotKey">The slot they fill (<c>runbook:failure-handling</c>).</param>
/// <param name="Instruction">What to write about; when empty, the slot's brief from the pack is used.</param>
/// <param name="Pack">The pack to take the slot's brief from; defaults to the project's own.</param>
/// <param name="EvidenceIds">The evidence the writer may cite; or give <paramref name="Query"/> to take the best matches.</param>
public sealed record StatementPreviewBody(
    string? Deliverable,
    string? SlotKey,
    string? SlotLabel,
    string? Instruction,
    string? Pack,
    List<string>? EvidenceIds,
    string? Query,
    int? Limit);

/// <summary>
/// Packs (read-only: what a kind of programme produces and how it is scored), the completeness score (pure arithmetic
/// on what the caller says has been approved) and a statement preview (what the writer and the grounding gate make of one
/// slot, shown and not saved). Reads are open to every persona; the preview calls a model, so it is for people who can
/// write.
/// </summary>
public static class KnowledgeEndpoints
{
    private const int MaxEvidence = 40;
    private static readonly Regex SlotKeyShape = new(@"^[a-z0-9-]+:[A-Za-z0-9._:\-]+$", RegexOptions.Compiled);

    public static IEndpointRouteBuilder MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/packs", (PackRegistry packs, IDocWriter docWriter) => Results.Ok(new
        {
            // Which writer statements are made with ("mock" answers offline; "anthropic" calls the model).
            writer = new { provider = docWriter.ProviderName, isMock = docWriter.IsMock },
            data = packs.All.Select(p => new
            {
                id = p.Id,
                title = p.Title,
                version = p.Version,
                deliverables = p.Deliverables.Select(d => new
                {
                    id = d.Id,
                    label = d.Label,
                    weight = d.Weight,
                    generator = d.Generator,
                    sectionKind = d.SectionKind,
                    scope = DeliverableKinds.Scope,
                    staticSlots = d.StaticSlots.Count,
                    entitySlots = d.EntitySlots is null ? null : new { keyPrefix = d.EntitySlots.KeyPrefix, countKey = d.EntitySlots.CountKey },
                }).ToList(),
            }).ToList(),
            problems = packs.Problems,
        }));

        app.MapGet("/api/v1/packs/{id}", (string id, PackRegistry packs) =>
            packs.Get(id) is { } pack
                ? Results.Ok(pack)
                : NotFound("pack.not_found", $"No pack named '{id}'."));

        // The score for what a caller says has been approved. Pure: it reads nothing from the database.
        app.MapPost("/api/v1/packs/{id}/completeness", (string id, CompletenessBody body, PackRegistry packs) =>
        {
            if (packs.Get(id) is not { } pack) return NotFound("pack.not_found", $"No pack named '{id}'.");

            var derived = (body.Derived ?? new()).ToDictionary(
                kv => kv.Key, kv => (IReadOnlyList<string>)(kv.Value ?? new List<string>()), StringComparer.Ordinal);
            var slots = (body.Slots ?? new()).Where(s => !string.IsNullOrWhiteSpace(s.SlotKey))
                .Select(s => new SlotEvidence(
                    s.SlotKey!,
                    (s.Versions ?? new()).Select(v => new SlotVersion(v.Approved, (IReadOnlyList<string>)(v.Statuses ?? new List<string>()))).ToList()))
                .ToList();
            var result = Completeness.Compute(new CompletenessInput(
                pack, derived, body.ExpectedCounts ?? new Dictionary<string, int>(), slots));
            return Results.Ok(result);
        });

        // What the writer and the gate make of one slot. Not saved: nothing about the project changes.
        app.MapPost("/api/v1/corpora/{id:guid}/statements/preview", async (
            Guid id,
            StatementPreviewBody body,
            DevPersonaContext actor,
            EvidenceService evidence,
            StatementWriter writer,
            PackRegistry packs,
            AppDbContext db,
            IDocWriter docWriter,
            CancellationToken ct) =>
        {
            if (actor.Persona is not (Persona.Engineer or Persona.Sme or Persona.Admin))
                return Results.Json(
                    new { error = new { code = "auth.writer_required", message = "Writing statements needs an Engineer, SME or Admin." } },
                    statusCode: StatusCodes.Status403Forbidden);

            var slotKey = (body.SlotKey ?? "").Trim();
            if (!SlotKeyShape.IsMatch(slotKey))
                return BadRequest("statements.invalid_slot", "slotKey must look like area:name, for example runbook:failure-handling.");
            var deliverable = string.IsNullOrWhiteSpace(body.Deliverable) ? slotKey.Split(':')[0] : body.Deliverable!.Trim();

            var (found, version) = await evidence.ResolveVersionAsync(id, null, ct);
            if (!found) return NotFound("corpus.not_found", "No such project.");
            if (version is not { } v) return BadRequest("statements.no_version", "This project has no source version yet.");

            var pack = packs.Get(body.Pack) ?? packs.All.FirstOrDefault();
            var definition = pack?.Deliverables.SelectMany(d => d.StaticSlots).FirstOrDefault(s => s.Key == slotKey);
            var instruction = (body.Instruction ?? "").Trim();
            if (instruction.Length == 0) instruction = definition?.Brief ?? "";
            if (instruction.Length == 0)
                return BadRequest("statements.no_instruction", "Say what to write about (instruction), or use a slot that has a brief in its pack.");
            if (instruction.Length > 2000)
                return BadRequest("statements.instruction_too_long", "The instruction is longer than 2,000 characters.");

            await evidence.EnsureIndexedAsync(v, ct);
            var limit = Math.Clamp(body.Limit ?? 20, 1, MaxEvidence);
            var ids = new List<string>();
            foreach (var raw in body.EvidenceIds ?? new List<string>())
                if (EvidenceIds.Normalise(raw) is { } shortId && !ids.Contains(shortId)) ids.Add(shortId);
            if (ids.Count == 0 && !string.IsNullOrWhiteSpace(body.Query))
                ids.AddRange(await evidence.SearchEvidenceIdsAsync(v, body.Query!.Trim(), limit, ct));
            ids = ids.Take(limit).ToList();

            var chunks = await evidence.LoadGateChunksAsync(v, ids, ct);
            if (chunks.Count == 0)
                return BadRequest("statements.no_evidence", "No evidence was found to write from: give evidenceIds that exist in this project, or a query that matches some.");

            StatementOutcome outcome;
            try
            {
                outcome = await writer.WriteAsync(new StatementRequest(
                    deliverable, slotKey, definition?.Label ?? body.SlotLabel ?? slotKey, instruction, chunks,
                    (cited, token) => evidence.LoadGateChunksAsync(v, cited, token)), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The provider's own reason, never a generic failure.
                return Results.Json(
                    new { error = new { code = "statements.writer_failed", message = ex.Message } },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            DocLlmCalls.RecordAll(db, docWriter, outcome.Calls);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                document = outcome.Document,
                markdown = StatementMarkdown.Render(outcome.Document),
                gate = new
                {
                    counts = outcome.Counts,
                    attempts = outcome.Attempts,
                    dropped = outcome.Dropped.Select(d => new
                    {
                        id = d.Item.Id,
                        text = d.Item.Text,
                        citations = d.Item.Citations,
                        issues = d.Issues,
                    }).ToList(),
                    issues = outcome.Issues,
                },
                notes = outcome.Notes,
                evidence = chunks.Select(c => new { id = c.ShortId, kind = c.Kind, trustTier = c.TrustTier, label = c.Label, source = c.Source }).ToList(),
                provider = docWriter.ProviderName,
                calls = outcome.Calls.Select(c => new
                {
                    promptId = c.Request.PromptId,
                    model = c.Result.Model,
                    replayKey = c.Request.ReplayKey,
                    temperature = c.Request.Temperature,
                    inputTokens = c.Result.Usage.InputTokens,
                    outputTokens = c.Result.Usage.OutputTokens,
                }).ToList(),
            });
        });

        // Claims: what people said, checked against what code, SQL, schedulers and configuration state.
        app.MapPost("/api/v1/corpora/{id:guid}/claims/run", async (
            Guid id,
            DevPersonaContext actor,
            EvidenceService evidence,
            ClaimService claims,
            AppDbContext db,
            IDocWriter docWriter,
            CancellationToken ct) =>
        {
            if (actor.Persona is not (Persona.Engineer or Persona.Sme or Persona.Admin))
                return Results.Json(
                    new { error = new { code = "auth.writer_required", message = "Reading claims needs an Engineer, SME or Admin." } },
                    statusCode: StatusCodes.Status403Forbidden);

            var (found, version) = await evidence.ResolveVersionAsync(id, null, ct);
            if (!found) return NotFound("corpus.not_found", "No such project.");
            if (version is not { } v) return BadRequest("claims.no_version", "This project has no source version yet.");

            ClaimRun run;
            try
            {
                run = await claims.RunAsync(v, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Json(
                    new { error = new { code = "claims.run_failed", message = ex.Message } },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            DocLlmCalls.RecordAll(db, docWriter, run.Calls);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                counts = run.Counts,
                modelCalls = run.Calls.Count,
                elapsedMs = run.ElapsedMs,
                notes = run.Notes,
                provider = docWriter.ProviderName,
            });
        });

        app.MapGet("/api/v1/corpora/{id:guid}/claims", async (Guid id, string? status, EvidenceService evidence, ClaimService claims, CancellationToken ct) =>
        {
            var (found, version) = await evidence.ResolveVersionAsync(id, null, ct);
            if (!found) return NotFound("corpus.not_found", "No such project.");
            if (version is not { } v) return BadRequest("claims.no_version", "This project has no source version yet.");
            var stored = await claims.GetAsync(v, ct);
            if (stored is null) return NotFound("claims.not_run", "No claims have been read for this project yet.");

            var doc = System.Text.Json.Nodes.JsonNode.Parse(stored.Value.Json)!.AsObject();
            if (!string.IsNullOrWhiteSpace(status) && doc["claims"] is System.Text.Json.Nodes.JsonArray all)
            {
                var kept = all.Where(c => string.Equals(c?["status"]?.GetValue<string>(), status, StringComparison.OrdinalIgnoreCase)).Select(c => c!.DeepClone()).ToArray();
                doc["claims"] = new System.Text.Json.Nodes.JsonArray(kept);
            }
            doc["modelCalls"] = stored.Value.ModelCalls;
            doc["elapsedMs"] = stored.Value.ElapsedMs;
            doc["builtAt"] = stored.Value.BuiltAt;
            return Results.Content(doc.ToJsonString(), "application/json");
        });

        // Groups of tickets about the same problem; three or more incidents is a candidate known error. No model.
        app.MapGet("/api/v1/corpora/{id:guid}/ticket-clusters", async (Guid id, EvidenceService evidence, ClaimService claims, CancellationToken ct) =>
        {
            var (found, version) = await evidence.ResolveVersionAsync(id, null, ct);
            if (!found) return NotFound("corpus.not_found", "No such project.");
            if (version is not { } v) return BadRequest("claims.no_version", "This project has no source version yet.");
            var result = await claims.ClustersAsync(v, ct);
            return Results.Ok(new { clusters = result.Clusters, routine = result.Routine });
        });

        return app;
    }

    private static IResult NotFound(string code, string message) => Results.NotFound(new { error = new { code, message } });

    private static IResult BadRequest(string code, string message) => Results.BadRequest(new { error = new { code, message } });
}
