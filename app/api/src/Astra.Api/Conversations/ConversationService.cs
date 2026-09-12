using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Runs;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Conversations;

/// <summary>
/// Threads + messages. Scoped (owns an AppDbContext). Every append also
/// publishes the rendered message on the <see cref="RunEventBus"/> under
/// the conversation id, which is what <c>GET /conversations/{id}/stream</c>
/// fans out to open tabs.
/// </summary>
public sealed class ConversationService
{
    public const string GlobalTitle = "Ask Astra";

    private readonly AppDbContext _db;
    private readonly RunEventBus _bus;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(AppDbContext db, RunEventBus bus, ILogger<ConversationService> logger)
    {
        _db = db;
        _bus = bus;
        _logger = logger;
    }

    // ── Threads ──────────────────────────────────────────────────────────

    public async Task<Conversation> EnsureGlobalAsync(CancellationToken ct)
    {
        var existing = await _db.Conversations.FirstOrDefaultAsync(c => c.Kind == "global", ct);
        if (existing is not null) return existing;

        var now = DateTimeOffset.UtcNow;
        var conv = new Conversation
        {
            Id = Guid.NewGuid(), CorpusId = null, Kind = "global", Title = GlobalTitle,
            CreatedAt = now, UpdatedAt = now,
        };
        _db.Conversations.Add(conv);
        await _db.SaveChangesAsync(ct);
        return conv;
    }

    /// <summary>The programme thread for a corpus, created (with a welcome
    /// post from the Discovery agent) on first touch so existing corpora
    /// get a thread without a migration.</summary>
    public async Task<Conversation> EnsureProgrammeAsync(Guid corpusId, CancellationToken ct)
    {
        var existing = await _db.Conversations
            .FirstOrDefaultAsync(c => c.CorpusId == corpusId && c.Kind == "programme", ct);
        if (existing is not null)
        {
            // A thread created while ingest was still running has no welcome
            // yet (see below) — backfill it the first time routines exist.
            if (existing.MessageCount == 0) await TrySeedWelcomeAsync(existing, ct);
            return existing;
        }

        var corpus = await _db.Corpora.AsNoTracking().FirstOrDefaultAsync(c => c.Id == corpusId, ct)
            ?? throw new InvalidOperationException($"Corpus {corpusId} not found.");

        var now = DateTimeOffset.UtcNow;
        var conv = new Conversation
        {
            Id = Guid.NewGuid(), CorpusId = corpusId, Kind = "programme", Title = corpus.Name,
            CreatedAt = now, UpdatedAt = now,
        };
        _db.Conversations.Add(conv);
        await _db.SaveChangesAsync(ct);
        await TrySeedWelcomeAsync(conv, ct);
        return conv;
    }

    /// <summary>Post the Discovery agent's welcome once the corpus actually
    /// has routines — a thread touched mid-ingest would otherwise open with
    /// "0 routines (unknown language)" forever.</summary>
    private async Task TrySeedWelcomeAsync(Conversation conv, CancellationToken ct)
    {
        if (conv.CorpusId is not { } cid) return;
        try
        {
            var corpus = await _db.Corpora.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct);
            if (corpus?.LatestVersionId is null) return;
            var (routines, _, _) = await ProgrammeStatsAsync(corpus, ct);
            if (routines == 0) return;
            var welcome = await BuildWelcomeAsync(corpus, ct);
            welcome.ConversationId = conv.Id;
            await AppendAsync(welcome, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not seed the welcome post for corpus {CorpusId}", cid);
        }
    }

    public async Task<IReadOnlyList<ConversationDto>> ListAsync(Guid? corpusId, CancellationToken ct)
    {
        var result = new List<ConversationDto>();
        if (corpusId is { } cid)
        {
            var conv = await EnsureProgrammeAsync(cid, ct);
            result.Add(await RenderAsync(conv, ct));
            return result;
        }

        var global = await EnsureGlobalAsync(ct);
        result.Add(await RenderAsync(global, ct));

        var corpora = await _db.Corpora.AsNoTracking()
            .Where(c => c.LatestVersionId != null)
            .OrderByDescending(c => c.UpdatedAt)
            .ToListAsync(ct);
        foreach (var c in corpora)
        {
            var conv = await EnsureProgrammeAsync(c.Id, ct);
            result.Add(await RenderAsync(conv, ct));
        }
        return result;
    }

    public async Task<Conversation?> FindAsync(Guid id, CancellationToken ct) =>
        await _db.Conversations.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<MessageDto>> MessagesAsync(Guid conversationId, int take, CancellationToken ct)
    {
        var rows = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
        rows.Reverse();
        return rows.Select(MessageDto.From).ToList();
    }

    public async Task<IReadOnlyList<ConversationMessage>> RecentEntitiesAsync(Guid conversationId, int take, CancellationToken ct)
    {
        var rows = await _db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(take)
            .ToListAsync(ct);
        rows.Reverse();
        return rows;
    }

    public async Task<ConversationDto> RenderAsync(Conversation conv, CancellationToken ct)
    {
        ProgrammeSummaryDto? programme = null;
        if (conv.CorpusId is { } cid)
        {
            var corpus = await _db.Corpora.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct);
            if (corpus is not null)
            {
                var (routines, files, language) = await ProgrammeStatsAsync(corpus, ct);
                programme = new ProgrammeSummaryDto(corpus.Name, language, routines, files);
            }
        }
        return new ConversationDto(conv.Id, conv.CorpusId, conv.Kind, conv.Title, conv.CreatedAt, conv.UpdatedAt,
            conv.LastMessageAt, conv.LastMessagePreview, conv.MessageCount, programme);
    }

    // ── Messages ─────────────────────────────────────────────────────────

    /// <summary>Persist a turn, bump the thread's preview, fan it out live.</summary>
    public async Task<MessageDto> AppendAsync(ConversationMessage message, CancellationToken ct)
    {
        if (message.Id == Guid.Empty) message.Id = Guid.NewGuid();
        if (message.CreatedAt == default) message.CreatedAt = DateTimeOffset.UtcNow;

        _db.ConversationMessages.Add(message);
        var conv = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == message.ConversationId, ct);
        if (conv is not null)
        {
            conv.LastMessageAt = message.CreatedAt;
            conv.LastMessagePreview = Preview(message);
            conv.MessageCount += 1;
            conv.UpdatedAt = message.CreatedAt;
        }
        await _db.SaveChangesAsync(ct);

        var dto = MessageDto.From(message);
        PublishLive(dto);
        return dto;
    }

    /// <summary>Update an existing turn in place (used when a paused turn
    /// resolves: pending action → confirmed/declined, final markdown).</summary>
    public async Task<MessageDto> UpdateAsync(ConversationMessage message, CancellationToken ct)
    {
        _db.ConversationMessages.Update(message);
        var conv = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == message.ConversationId, ct);
        if (conv is not null)
        {
            conv.LastMessagePreview = Preview(message);
            conv.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync(ct);
        var dto = MessageDto.From(message);
        PublishLive(dto);
        return dto;
    }

    public async Task<ConversationMessage?> FindMessageAsync(Guid id, CancellationToken ct) =>
        await _db.ConversationMessages.FirstOrDefaultAsync(m => m.Id == id, ct);

    private void PublishLive(MessageDto dto) =>
        _bus.Publish(dto.ConversationId, dto.Agent ?? dto.Role, "", "message", dto, Preview(dto.Markdown));

    private static string Preview(ConversationMessage m) => Preview(m.Markdown);

    private static string Preview(string markdown)
    {
        var flat = markdown.Replace("\r", "").Replace("\n", " ").Replace("**", "").Replace("`", "").Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }

    // ── Programme facts ──────────────────────────────────────────────────

    public async Task<(int Routines, int Files, string? Language)> ProgrammeStatsAsync(Corpus corpus, CancellationToken ct)
    {
        if (corpus.LatestVersionId is not { } vid) return (0, corpus.FileCount, null);
        var files = await _db.SourceFiles.AsNoTracking().CountAsync(f => f.SourceVersionId == vid, ct);
        var byLanguage = await (
            from s in _db.Subroutines.AsNoTracking()
            join f in _db.SourceFiles.AsNoTracking() on s.SourceFileId equals f.Id
            where f.SourceVersionId == vid
            group s by s.SourceLanguage into g
            select new { Language = g.Key, Count = g.Count() }).ToListAsync(ct);
        var routines = byLanguage.Sum(x => x.Count);
        var language = byLanguage.OrderByDescending(x => x.Count).FirstOrDefault()?.Language;
        return (routines, files, language);
    }

    public async Task<FunnelCounts> FunnelAsync(Corpus corpus, CancellationToken ct)
    {
        if (corpus.LatestVersionId is not { } vid) return FunnelCounts.Empty;
        var rows = await (
            from s in _db.Subroutines.AsNoTracking()
            join f in _db.SourceFiles.AsNoTracking() on s.SourceFileId equals f.Id
            where f.SourceVersionId == vid
            group s by s.State into g
            select new { State = g.Key, Count = g.Count() }).ToListAsync(ct);
        return FunnelCounts.FromStates(rows.Select(r => (r.State, r.Count)));
    }

    private async Task<ConversationMessage> BuildWelcomeAsync(Corpus corpus, CancellationToken ct)
    {
        var (routines, files, language) = await ProgrammeStatsAsync(corpus, ct);
        var funnel = await FunnelAsync(corpus, ct);
        var lang = LanguageLabel(language);

        var lines = new List<string>
        {
            $"I've parsed **{corpus.Name}** — {routines:N0} routines across {files:N0} files ({lang}).",
        };
        if (funnel.Signed + funnel.Scaffolded + funnel.Committed > 0)
            lines.Add($"So far: {funnel.Signed:N0} signed, {funnel.Scaffolded:N0} built, {funnel.Committed:N0} committed; {funnel.Parsed:N0} routines haven't been looked at yet.");
        else
            lines.Add("Nothing has been specified yet.");
        lines.Add("Want me to survey the patterns first, or shall we start with the riskiest routines?");

        var suggestions = new List<SuggestionDto>
        {
            new("Survey the patterns", $"Survey the patterns in {corpus.Name}"),
            new("What's the status?", $"What's the status of {corpus.Name}?"),
            new("Riskiest routines", $"Which routines in {corpus.Name} are the riskiest to migrate and why?"),
            new("Search routines", "Find routines named "),
        };
        var artifact = new ArtifactDto("funnel", corpus.Id.ToString(), ConversationJson.ToElement(new
        {
            corpusName = corpus.Name,
            sourceLanguage = language,
            counts = funnel,
        }));

        return new ConversationMessage
        {
            Role = "agent",
            Agent = "discovery",
            AuthorDisplay = "Discovery",
            Markdown = string.Join("\n\n", lines),
            ArtifactsJson = ConversationJson.Serialize(new[] { artifact }),
            SuggestionsJson = ConversationJson.Serialize(suggestions),
        };
    }

    public static string LanguageLabel(string? schemaId) => schemaId switch
    {
        null or "" => "unknown language",
        "fortran-f77" => "Fortran 77",
        "cpp" => "C++",
        "delphi" => "Delphi",
        "cobol" => "COBOL",
        "vb6" => "VB6",
        "vbnet" => "VB.NET",
        "unibasic" => "UniBasic",
        "abl" => "Progress ABL",
        "java" => "Java",
        "csharp" => "C#",
        _ => schemaId,
    };
}
