using System.Text.Json;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Llm;

/// <summary>
/// Phase 7.0 — Assembles a <see cref="Neighbourhood"/> for a given
/// subroutine from the parser-extracted metadata already in Postgres.
///
/// Why this lives between RAG and "stuff the repo": the parser sidecar
/// has ALREADY extracted the call graph + COMMON refs structurally at
/// ingest time (see <see cref="Persistence.Entities.Subroutine.CalledSubroutines"/>
/// and <see cref="Persistence.Entities.Subroutine.CommonBlockRefs"/>).
/// A vector-RAG layer would re-discover this probabilistically; long
/// context would force the model to filter the whole corpus. Instead
/// we resolve names against rows in the same corpus and feed the
/// extractor structured signatures + spec excerpts — the bare minimum
/// for cross-file reasoning, no noise.
///
/// Scope: same corpus, same source version (the LATEST), name-matched.
/// Cross-corpus or cross-version resolution is deliberately out of
/// scope — it would conflate different programs and inflate noise.
/// </summary>
public sealed class NeighbourhoodBuilder
{
    /// <summary>
    /// Cap each neighbourhood section to this many entries. Empirically,
    /// routines with >12 callees / callers are exception-handler hubs
    /// where listing all of them adds bulk without info; the most
    /// recently parsed N are the freshest. We keep deterministic order
    /// (alphabetical by name) so the cache key is stable across runs.
    /// </summary>
    public const int MaxEntriesPerSection = 12;

    private readonly AppDbContext _db;
    private readonly ILogger<NeighbourhoodBuilder> _log;

    public NeighbourhoodBuilder(AppDbContext db, ILogger<NeighbourhoodBuilder> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<Neighbourhood> BuildForSubroutineAsync(
        Guid subroutineId,
        CancellationToken ct = default)
    {
        // Anchor: load the routine + the SourceFile + the corpus's
        // latest SourceVersion. Everything we look up is scoped to
        // that version so we don't conflate program revisions.
        var self = await _db.Subroutines
            .AsNoTracking()
            .Include(s => s.SourceFile)
                .ThenInclude(f => f!.SourceVersion)
            .FirstOrDefaultAsync(s => s.Id == subroutineId, ct);
        if (self?.SourceFile?.SourceVersion is null)
        {
            _log.LogWarning(
                "Neighbourhood requested for unknown subroutine {Id}", subroutineId);
            return Neighbourhood.Empty;
        }
        var versionId = self.SourceFile.SourceVersionId;

        // ── Pull every routine in the same version once. The corpora
        // we work with are <2_000 routines; a single in-memory pass is
        // far cheaper than N round-trips to resolve each name.
        var siblings = await _db.Subroutines
            .AsNoTracking()
            .Include(s => s.SourceFile)
            .Where(s => s.SourceFile!.SourceVersionId == versionId
                     && s.Id != subroutineId)
            .ToListAsync(ct);
        var byNameUpper = siblings
            .GroupBy(s => s.Name.ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        // ── Pre-fetch any existing specs for those siblings. Used to
        // attach a "what this callee does in plain English" summary
        // when available. We take only the latest spec per subroutine.
        var siblingIds = siblings.Select(s => s.Id).ToList();
        var specs = await _db.Specs
            .AsNoTracking()
            .Where(sp => siblingIds.Contains(sp.SubroutineId))
            .OrderByDescending(sp => sp.UpdatedAt)
            .ToListAsync(ct);
        var latestSpecBySubId = specs
            .GroupBy(sp => sp.SubroutineId)
            .ToDictionary(g => g.Key, g => g.First());

        // ── Callees: names the parser saw this routine CALL.
        var calleeNames = ReadNameList(self.CalledSubroutines);
        var callees = new List<RoutineRef>();
        foreach (var rawName in calleeNames
                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                  .OrderBy(n => n, StringComparer.Ordinal)
                                  .Take(MaxEntriesPerSection))
        {
            var nameUp = rawName.ToUpperInvariant();
            if (byNameUpper.TryGetValue(nameUp, out var resolved))
            {
                callees.Add(ToRoutineRef(resolved, "in_corpus", latestSpecBySubId));
            }
            else
            {
                callees.Add(new RoutineRef(
                    Name: rawName,
                    Signature: null,
                    SourcePath: null,
                    Resolution: "external",
                    SpecSummary: null,
                    KnownSideEffects: Array.Empty<string>()));
            }
        }

        // ── Callers: routines in the same version whose
        // CalledSubroutines list mentions this routine's name. There
        // is no reverse-index column so we filter in memory — fine at
        // current corpus sizes.
        var selfNameUp = self.Name.ToUpperInvariant();
        var callers = new List<RoutineRef>();
        foreach (var sib in siblings)
        {
            var sibCallees = ReadNameList(sib.CalledSubroutines)
                .Select(n => n.ToUpperInvariant())
                .ToHashSet();
            if (sibCallees.Contains(selfNameUp))
            {
                callers.Add(ToRoutineRef(sib, "in_corpus", latestSpecBySubId));
            }
            if (callers.Count >= MaxEntriesPerSection) break;
        }
        callers = callers
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        // ── COMMON blocks: for each block this routine references,
        // pick the routine in the same version that declared it first
        // (lowest LineStart, alphabetic tie-break) so the LLM has a
        // canonical layout source. The parser doesn't store the
        // variable list per block today; the signature of the
        // declaring routine + the block name is enough to point at
        // the right file/line range for an SME or follow-up tooling.
        var commonNames = ReadNameList(self.CommonBlockRefs);
        var commonBlocks = new List<CommonBlockRef>();
        foreach (var rawName in commonNames
                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                  .OrderBy(n => n, StringComparer.Ordinal)
                                  .Take(MaxEntriesPerSection))
        {
            // Find the routine in the version that declares this block
            // first (lowest LineStart in the earliest-named source file).
            var declarer = siblings
                .Where(s => ReadNameList(s.CommonBlockRefs)
                              .Any(b => string.Equals(b, rawName, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(s => s.SourceFile?.RelativePath ?? "", StringComparer.Ordinal)
                .ThenBy(s => s.LineStart)
                .FirstOrDefault();
            if (declarer is null
                && ReadNameList(self.CommonBlockRefs)
                     .Any(b => string.Equals(b, rawName, StringComparison.OrdinalIgnoreCase)))
            {
                // The routine itself is the only declarer; surface
                // that for the LLM so it knows there's no other.
                commonBlocks.Add(new CommonBlockRef(
                    Name: rawName,
                    DeclaringRoutine: self.Name,
                    DeclaringRoutineSignature: self.Signature,
                    DeclaringRoutineSourcePath: self.SourceFile?.RelativePath));
            }
            else if (declarer is not null)
            {
                commonBlocks.Add(new CommonBlockRef(
                    Name: rawName,
                    DeclaringRoutine: declarer.Name,
                    DeclaringRoutineSignature: declarer.Signature,
                    DeclaringRoutineSourcePath: declarer.SourceFile?.RelativePath));
            }
        }

        // ── Siblings-in-file: every routine sharing the same .cob/.f
        // file. Often these are tightly coupled (one paragraph calling
        // another), so seeing their names + signatures helps the model
        // ground "what module is this?".
        var siblingsInFile = siblings
            .Where(s => s.SourceFileId == self.SourceFileId)
            .OrderBy(s => s.LineStart)
            .Take(MaxEntriesPerSection)
            .Select(s => ToRoutineRef(s, "in_corpus", latestSpecBySubId))
            .ToList();

        var nbh = new Neighbourhood(
            Callees: callees,
            Callers: callers,
            CommonBlocks: commonBlocks,
            SiblingsInFile: siblingsInFile);
        _log.LogInformation(
            "Neighbourhood for {Subroutine}: {Callees} callees, {Callers} callers, {Commons} commons, {Siblings} siblings",
            self.Name, nbh.Callees.Count, nbh.Callers.Count, nbh.CommonBlocks.Count, nbh.SiblingsInFile.Count);
        return nbh;
    }

    private static RoutineRef ToRoutineRef(
        Subroutine sub,
        string resolution,
        IDictionary<Guid, Spec> latestSpecBySubId)
    {
        string? summary = null;
        var sideEffects = new List<string>();
        if (latestSpecBySubId.TryGetValue(sub.Id, out var spec))
        {
            try
            {
                var root = spec.SpecJson.RootElement;
                if (root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String)
                    summary = s.GetString();
                if (root.TryGetProperty("side_effects", out var se) && se.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in se.EnumerateArray())
                    {
                        if (item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                        {
                            var line = d.GetString();
                            if (!string.IsNullOrWhiteSpace(line)) sideEffects.Add(line);
                        }
                    }
                }
                // COBOL specs use io_side_effects.
                if (root.TryGetProperty("io_side_effects", out var ioSe) && ioSe.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in ioSe.EnumerateArray())
                    {
                        if (item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                        {
                            var line = d.GetString();
                            if (!string.IsNullOrWhiteSpace(line)) sideEffects.Add(line);
                        }
                    }
                }
            }
            catch { /* malformed spec — fall through with empty summary */ }
        }
        return new RoutineRef(
            Name: sub.Name,
            Signature: string.IsNullOrWhiteSpace(sub.Signature) ? null : sub.Signature,
            SourcePath: sub.SourceFile?.RelativePath,
            Resolution: resolution,
            SpecSummary: summary,
            KnownSideEffects: sideEffects);
    }

    private static IReadOnlyList<string> ReadNameList(JsonDocument? doc)
    {
        if (doc is null) return Array.Empty<string>();
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var el in root.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return list;
    }
}
