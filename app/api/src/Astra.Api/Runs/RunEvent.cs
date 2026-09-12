namespace Astra.Api.Runs;

/// <summary>
/// One structured event on a long-running agent run (pattern analysis,
/// docs generation, harmonisation, …). Replaces the plain log strings the
/// old <c>DocRunLogger</c> carried, so a UI can render real progress and
/// state transitions — not just scroll text — and an activity feed can
/// multiplex many runs.
/// </summary>
/// <param name="Type">
///   <c>log</c> — human-readable line (<see cref="Message"/>) ·
///   <c>progress</c> — <c>{done, total, failed, propagated, etaSeconds}</c> ·
///   <c>stage</c> — a stage started/finished ·
///   <c>state</c> — run state transition (<c>{state, summary}</c>) ·
///   <c>item</c> — one unit of work finished (<c>{name, ok, …}</c>) ·
///   <c>done</c> — terminal.
/// </param>
public sealed record RunEvent(
    Guid RunId,
    string Agent,
    string Stage,
    string Type,
    DateTimeOffset Ts,
    long Seq,
    object? Data,
    string? Message);
