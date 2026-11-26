using System.Text.Json;
using Astra.Api.Auth;
using Astra.Api.Copilot;
using Astra.Api.Llm;
using Astra.Api.Persistence.Entities;
using Astra.Api.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Astra.Api.Tests;

public class LlmLaneAndBudgetTests
{
    private static AnthropicRateLimiter Make(int max, int reserve = 1, int tokensPerMinute = 0) =>
        new(Options.Create(new AnthropicRateLimiterOptions
        {
            MaxInFlight = max, MinInFlight = 1, InteractiveReserve = reserve, BatchTokensPerMinute = tokensPerMinute,
        }), NullLogger<AnthropicRateLimiter>.Instance);

    private static async Task<AnthropicRateLimiter.Lease> BatchAcquire(AnthropicRateLimiter limiter, CancellationToken ct, RunBudget? budget = null)
    {
        LlmWorkScope.Begin(LlmLane.Batch, budget); // scoped to this async method
        return await limiter.AcquireAsync(null, ct);
    }

    [Fact]
    public async Task Batch_NeverTakesTheSlotsReservedForInteractiveCalls()
    {
        var limiter = Make(max: 3, reserve: 1);
        using var a = await BatchAcquire(limiter, CancellationToken.None);
        using var b = await BatchAcquire(limiter, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BatchAcquire(limiter, cts.Token));

        // The person typing still gets the third slot at once.
        var interactive = limiter.AcquireAsync(null, CancellationToken.None);
        Assert.Same(interactive, await Task.WhenAny(interactive, Task.Delay(1000)));
        (await interactive).Dispose();
    }

    [Fact]
    public async Task AWaitingInteractiveCall_GoesBeforeQueuedBatchCalls()
    {
        var limiter = Make(max: 1, reserve: 0);
        var held = await BatchAcquire(limiter, CancellationToken.None);
        var order = new List<string>();

        var batch = Task.Run(async () => { using var l = await BatchAcquire(limiter, CancellationToken.None); lock (order) order.Add("batch"); });
        await Task.Delay(200);
        var person = Task.Run(async () => { using var l = await limiter.AcquireAsync(null, CancellationToken.None); lock (order) order.Add("interactive"); await Task.Delay(100); });
        await Task.Delay(200);
        held.Dispose();
        await Task.WhenAll(batch, person);

        Assert.Equal(new[] { "interactive", "batch" }, order);
    }

    [Fact]
    public async Task TheTokenPerMinuteBudget_HoldsBatchBack_ButNotInteractive()
    {
        var limiter = Make(max: 4, reserve: 0, tokensPerMinute: 1000);
        limiter.RecordTokens(1500);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BatchAcquire(limiter, cts.Token));
        using var lease = await limiter.AcquireAsync(null, CancellationToken.None);
    }

    [Fact]
    public async Task ASpentCostCeiling_RefusesTheNextCall()
    {
        var limiter = Make(max: 4);
        var budget = new RunBudget(Guid.NewGuid(), 1.00m);
        (await BatchAcquire(limiter, CancellationToken.None, budget)).Dispose();

        budget.Add(1.20m, 10, 10);

        var ex = await Assert.ThrowsAsync<LlmBudgetExceededException>(() => BatchAcquire(limiter, CancellationToken.None, budget));
        Assert.Equal(1.20m, ex.SpentUsd);
        Assert.Equal(budget.RunId, ex.RunId);
    }

    [Fact]
    public async Task UsageOfASavedCall_CountsAgainstTheScopeAndTheRun_AndStaysOutOfOtherFlows()
    {
        var budget = new RunBudget(Guid.NewGuid(), 5m, alreadySpent: 1m);

        async Task<(int Calls, long In, long Out, decimal Cost)> Step()
        {
            var scope = LlmWorkScope.Begin(LlmLane.Batch, budget);
            await Task.Yield();
            LlmUsage.Publish(new LlmCall { InputTokens = 100, CacheReadTokens = 20, OutputTokens = 40, CostUsd = 0.5m });
            return (scope.Calls, scope.InputTokens, scope.OutputTokens, scope.CostUsd);
        }

        var usage = await Step();

        Assert.Equal((1, 120L, 40L, 0.5m), usage);
        Assert.Equal(1.5m, budget.SpentUsd);
        Assert.Null(LlmWorkScope.Current);
    }
}

public class GateExecutorPoolTests
{
    [Fact]
    public async Task NeverMoreThanTheCap_RunsAtOnce()
    {
        var pool = new GateExecutorPool(2);
        int live = 0, peak = 0, queued = 0;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            using var slot = await pool.AcquireAsync(CancellationToken.None, _ => { Interlocked.Increment(ref queued); return Task.CompletedTask; });
            var now = Interlocked.Increment(ref live);
            int seen;
            while (now > (seen = Volatile.Read(ref peak))) Interlocked.CompareExchange(ref peak, now, seen);
            await Task.Delay(40);
            Interlocked.Decrement(ref live);
        })));

        Assert.Equal(2, peak);
        Assert.True(queued >= 1);
        Assert.Equal(0, pool.Running);
        Assert.Equal(0, pool.Waiting);
    }

    [Fact]
    public async Task ACancelledWaiter_LeavesTheQueue()
    {
        var pool = new GateExecutorPool(1);
        using var held = await pool.AcquireAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.AcquireAsync(cts.Token));
        Assert.Equal(0, pool.Waiting);
    }
}

public class BatchToolTests
{
    private static readonly string[] BatchTools = { "run_wave", "extract_many", "generate_many", "run_gates_many", "control_work_run" };

    [Fact]
    public void EveryBatchTool_PausesOnAConfirmCard_AndNoneIsForTheSmeOrSigns()
    {
        var registry = new CopilotToolRegistry();
        foreach (var name in BatchTools)
        {
            var tool = registry.Find(name);
            Assert.NotNull(tool);
            Assert.True(tool!.Mutating, name);
            Assert.NotNull(tool.Describe);
            Assert.NotEmpty(tool.AllowedPersonas);
            Assert.DoesNotContain(Persona.Sme, tool.AllowedPersonas);
            Assert.DoesNotContain(Persona.Observer, tool.AllowedPersonas);
        }
        Assert.False(registry.Find("get_work_run")!.Mutating);
        Assert.False(registry.Find("get_review_queue")!.Mutating);
    }

    private static async Task<(string Tool, JsonElement Input)> Route(string sentence)
    {
        var brain = new MockCopilotBrain();
        var response = await brain.CompleteAsync(
            new BrainRequest("system", new object[] { new { role = "user", content = sentence } }, Array.Empty<object>()),
            CancellationToken.None);
        var call = response.Blocks.First(b => b.Type == "tool_use");
        return (call.ToolName!, call.Input!.Value);
    }

    [Theory]
    [InlineData("Run wave 1", "run_wave")]
    [InlineData("Run the next wave", "run_wave")]
    [InlineData("Execute all the waves with a budget of $5", "run_wave")]
    [InlineData("Extract the specs for all routines", "extract_many")]
    [InlineData("Draft specs for 12 routines", "extract_many")]
    [InlineData("Generate code for all signed routines", "generate_many")]
    [InlineData("Run the compile gate for all routines", "run_gates_many")]
    [InlineData("How is the wave run going?", "get_work_run")]
    [InlineData("Show the review queue", "get_review_queue")]
    [InlineData("What is waiting for sign-off?", "get_review_queue")]
    [InlineData("Pause the wave run", "control_work_run")]
    [InlineData("Resume the wave run", "control_work_run")]
    [InlineData("Cancel the wave run", "control_work_run")]
    [InlineData("Retry the failed steps of the wave run", "control_work_run")]
    // the single-routine sentences still go where they went
    [InlineData("Extract the spec for `CalcPay`", "extract_spec")]
    [InlineData("Run the compile gate for `CalcPay`", "run_gate")]
    [InlineData("Run the quality gate for `CalcPay`", "run_gate")]
    // batches do not cover the quality gate, so its sentence must not queue the other two
    [InlineData("Run the quality gate for all routines", "run_gate")]
    [InlineData("Run the compile and quality gates for all routines", "run_gates_many")]
    // nor does a batch stand in for the one-routine fix loop
    [InlineData("Compile all routines until they're green", "fix_until_green")]
    [InlineData("Fix `CalcPay` until it's green", "fix_until_green")]
    [InlineData("Generate the code for `CalcPay`", "generate_scaffold")]
    [InlineData("Draft a migration plan", "generate_migration_plan")]
    [InlineData("Show me the migration plan", "get_migration_plan")]
    [InlineData("Resume the pattern analysis", "survey_corpus")]
    [InlineData("Run an assessment for payroll", "run_assessment")]
    public async Task Sentences_ReachTheRightTool(string sentence, string tool)
    {
        Assert.Equal(tool, (await Route(sentence)).Tool);
    }

    [Fact]
    public void ABatchThatTakesOnlyPartOfWhatMatches_SaysSoInItsName()
    {
        Assert.Equal("Spec drafts · 500 of 900 routines · demo", Astra.Api.Work.BatchSentence.ListLabel("Spec drafts", 500, 900, "demo"));
        Assert.Equal("Spec drafts · 80 routines · demo", Astra.Api.Work.BatchSentence.ListLabel("Spec drafts", 80, 80, "demo"));
        Assert.Equal("Gates · 1 routine · demo", Astra.Api.Work.BatchSentence.ListLabel("Gates", 1, 0, "demo"));
    }

    [Theory]
    [InlineData("Cancel the wave run 39571149")]
    [InlineData("Retry the failed steps of the wave run 39571149")]
    public async Task ACardsButtonSentence_NamesItsOwnRun(string sentence)
    {
        Assert.Equal("39571149", (await Route(sentence)).Input.GetProperty("runId").GetString());
    }

    [Fact]
    public async Task WaveSentences_CarryTheWaveNumberAndTheCeiling()
    {
        var (_, input) = await Route("Run wave 2 with a budget of $7.50");
        Assert.Equal(2, input.GetProperty("wave").GetInt32());
        Assert.Equal("7.50", input.GetProperty("budgetUsd").GetString());
        Assert.Equal("retry-failed", (await Route("Retry the failed steps of the wave run")).Input.GetProperty("action").GetString());
    }
}
