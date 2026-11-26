namespace Astra.Api.Persistence.Entities;

/// <summary>
/// P5 — one durable, resumable batch of per-routine work (a wave run, an
/// extract-many, a generate-many …). Its id doubles as the <c>RunEventBus</c>
/// run id, so the thread's progress card and the Narrator follow it exactly
/// like any other run. All state lives here; the in-memory bus and narrator
/// are rebuilt from these rows after a restart.
/// </summary>
public sealed class WorkRun
{
    public Guid Id { get; set; }
    /// <summary>"wave" | "extract-many" | "generate-many" | "gates-many".</summary>
    public string Kind { get; set; } = "wave";
    public Guid CorpusId { get; set; }
    public Guid? ConversationId { get; set; }
    public Guid? MigrationWaveId { get; set; }
    public string Label { get; set; } = "";

    /// <summary>"RUNNING" | "PAUSED" | "CANCELLED" | "COMPLETED".</summary>
    public string State { get; set; } = "RUNNING";

    /// <summary>Persona the run acts as (the requester). Agents never sign:
    /// the sign-off step is a wait state that only a human completes.</summary>
    public string Persona { get; set; } = "Engineer";
    public string DisplayName { get; set; } = "";
    public string? TargetStack { get; set; }

    /// <summary>Stage kinds this run includes, e.g. extract,signoff,scaffold,compile,test-pack.</summary>
    public string StagesJson { get; set; } = "[]";
    /// <summary>Per-stage concurrency caps for this run, {"extract":4,...}.</summary>
    public string CapsJson { get; set; } = "{}";

    /// <summary>Per-run cost ceiling in USD (null = none). Enforced by the
    /// rate limiter before every model call made on behalf of the run.</summary>
    public decimal? BudgetUsd { get; set; }
    public decimal CostUsd { get; set; }
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>Wall-clock vs the serial equivalent, written on completion.</summary>
    public string? MetricsJson { get; set; }
    public string Summary { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// P5 — one unit of work in the durable queue (<c>work_items</c>). Leased with
/// <c>FOR UPDATE SKIP LOCKED</c>, kept alive with a heartbeat, and reclaimed
/// when the lease lapses (API restart / crashed instance).
/// </summary>
public sealed class WorkItem
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }

    /// <summary>"extract" | "signoff" | "scaffold" | "compile" | "test-pack".
    /// <c>signoff</c> is the human wait state: no worker ever leases it; the
    /// scheduler only observes the spec becoming SIGNED.</summary>
    public string Kind { get; set; } = "";

    /// <summary>What the item works on: the routine.</summary>
    public Guid SubjectId { get; set; }
    public string SubjectName { get; set; } = "";

    /// <summary>Items that must be SUCCEEDED before this one is leasable.</summary>
    public Guid[] DependsOn { get; set; } = Array.Empty<Guid>();

    /// <summary>"PENDING" | "WAITING" | "RUNNING" | "SUCCEEDED" | "FAILED" |
    /// "CANCELLED" | "SKIPPED".</summary>
    public string State { get; set; } = "PENDING";

    /// <summary>"interactive" | "batch" — which limiter lane its model calls use.</summary>
    public string Lane { get; set; } = "batch";
    /// <summary>Lower runs first: wave number × 10 + stage order.</summary>
    public int Priority { get; set; }

    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 2;
    public DateTimeOffset? NotBefore { get; set; }

    public string? Error { get; set; }
    public string? ResultJson { get; set; }

    public decimal CostUsd { get; set; }
    public int Calls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
