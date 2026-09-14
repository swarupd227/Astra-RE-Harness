using System.Threading.Tasks;
using Moq;
using Xunit;

namespace Demo.RollStock.Tests;

/// <summary>
/// Claim-mapped xUnit fixtures for the signed COBOL spec. Each test
/// cites the claim id(s) it exercises so the audit trail can pin the
/// test back to the SME's signed line.
/// </summary>
public sealed class ConsumeRollServiceTests
{
    private static ConsumeRollService Subject(Mock<IRollRepository>? rolls = null, Mock<IEventNotifier>? events = null)
    {
        rolls ??= new Mock<IRollRepository>();
        events ??= new Mock<IEventNotifier>();
        return new ConsumeRollService(rolls.Object, events.Object);
    }

    [Fact(DisplayName = "INV-1: missing roll returns NotFound")]
    public async Task Inv1_NotFound()
    {
        var rolls = new Mock<IRollRepository>();
        rolls.Setup(r => r.ReadAsync("R-999", default)).ReturnsAsync((Roll?)null);
        var sut = Subject(rolls);
        var result = await sut.ConsumeAsync("R-999", 1m, "OP001");
        Assert.Equal(ConsumeRollResult.NotFound, result);
    }

    [Fact(DisplayName = "INV-2: locked roll returns Locked without REWRITE")]
    public async Task Inv2_Locked()
    {
        var rolls = new Mock<IRollRepository>();
        rolls.Setup(r => r.ReadAsync("R-LCK", default))
             .ReturnsAsync(new Roll("R-LCK", 100m, 1, "G-AS", Locked: true));
        var sut = Subject(rolls);
        var result = await sut.ConsumeAsync("R-LCK", 5m, "OP001");
        Assert.Equal(ConsumeRollResult.Locked, result);
        rolls.Verify(r => r.RewriteAsync(It.IsAny<Roll>(), default), Times.Never);
    }

    [Fact(DisplayName = "INV-3: USED_LF > ON_HAND_LF returns Insufficient")]
    public async Task Inv3_Insufficient()
    {
        var rolls = new Mock<IRollRepository>();
        rolls.Setup(r => r.ReadAsync("R-001", default))
             .ReturnsAsync(new Roll("R-001", 10m, 1, "G-AS", Locked: false));
        var sut = Subject(rolls);
        var result = await sut.ConsumeAsync("R-001", 50m, "OP001");
        Assert.Equal(ConsumeRollResult.Insufficient, result);
    }

    [Fact(DisplayName = "INV-5: NEW_LF below MinRemain sets ROLL_STATUS=9")]
    public async Task Inv5_Depleted()
    {
        Roll? written = null;
        var rolls = new Mock<IRollRepository>();
        rolls.Setup(r => r.ReadAsync("R-001", default))
             .ReturnsAsync(new Roll("R-001", 20m, 1, "G-AS", Locked: false));
        rolls.Setup(r => r.RewriteAsync(It.IsAny<Roll>(), default))
             .Callback<Roll, System.Threading.CancellationToken>((r, _) => written = r)
             .Returns(Task.CompletedTask);
        var sut = Subject(rolls);
        await sut.ConsumeAsync("R-001", 15m, "OP001");
        Assert.NotNull(written);
        Assert.Equal(9, written!.Status);
    }

    [Fact(DisplayName = "EC-1: successful CONSUME-ROLL emits INV-CHG event")]
    public async Task Ec1_EmitsEvent()
    {
        var rolls = new Mock<IRollRepository>();
        rolls.Setup(r => r.ReadAsync("R-001", default))
             .ReturnsAsync(new Roll("R-001", 100m, 1, "G-AS", Locked: false));
        var events = new Mock<IEventNotifier>();
        var sut = Subject(rolls, events);
        await sut.ConsumeAsync("R-001", 40m, "OP001");
        events.Verify(e => e.EmitInventoryChangedAsync(
            "R-001", "G-AS", 60m, default), Times.Once);
    }
}
