using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;

namespace Astra.Api.Validation;

/// <summary>
/// P5 — bounded executor for Compile / Test-pack gates. Before this, every gate
/// ran as an uncapped child process inside the API container, so a wave of 80
/// routines could start 80 builds at once. Now at most
/// <c>Validation:MaxConcurrentGates</c> run at a time per process; the rest wait
/// in a FIFO queue and their <see cref="ValidationRun"/> says so ("Waiting for a
/// build slot"). Applies to REST- and copilot-started gates and to wave runs
/// alike, because the validators take their slot themselves.
/// </summary>
public sealed class GateExecutorPool
{
    /// <summary>One pool per process; configured at boot.</summary>
    public static GateExecutorPool Shared { get; private set; } = new(3);

    public static void Configure(int maxConcurrent) => Shared = new GateExecutorPool(maxConcurrent);

    private readonly SemaphoreSlim _slots;
    private int _running;
    private int _waiting;

    public GateExecutorPool(int maxConcurrent)
    {
        Max = Math.Max(1, maxConcurrent);
        _slots = new SemaphoreSlim(Max, Max);
    }

    public int Max { get; }
    public int Running => Volatile.Read(ref _running);
    public int Waiting => Volatile.Read(ref _waiting);

    public async Task<IDisposable> AcquireAsync(CancellationToken ct, Func<int, Task>? onQueued = null)
    {
        if (!_slots.Wait(0))
        {
            var ahead = Interlocked.Increment(ref _waiting);
            try
            {
                if (onQueued is not null)
                {
                    try { await onQueued(ahead); } catch { /* the label is cosmetic */ }
                }
                await _slots.WaitAsync(ct);
            }
            finally { Interlocked.Decrement(ref _waiting); }
        }
        Interlocked.Increment(ref _running);
        return new Slot(this);
    }

    /// <summary>Take a slot and, while queued, say so on the validation run row.</summary>
    public Task<IDisposable> AcquireForAsync(AppDbContext db, ValidationRun run, string label, CancellationToken ct) =>
        AcquireAsync(ct, async ahead =>
        {
            run.Summary = $"Waiting for a {label} slot ({ahead} queued)";
            await db.SaveChangesAsync(ct);
        });

    private sealed class Slot : IDisposable
    {
        private GateExecutorPool? _owner;
        public Slot(GateExecutorPool owner) => _owner = owner;
        public void Dispose()
        {
            var o = Interlocked.Exchange(ref _owner, null);
            if (o is null) return;
            Interlocked.Decrement(ref o._running);
            o._slots.Release();
        }
    }
}
