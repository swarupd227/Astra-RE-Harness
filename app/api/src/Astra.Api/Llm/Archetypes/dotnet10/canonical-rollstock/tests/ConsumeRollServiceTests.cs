using System.Threading.Tasks;
using Moq;
using Xunit;

namespace Demo.RollStock.Tests;

/// <summary>
/// Engineer-authored fixtures derived from CONSUME_ROLL invariants.
/// The bodies that call the service are stubbed until the implementation
/// lands — pre-impl they assert the contract surface only, so green
/// here means "harness is wired correctly", not "behaviour is correct".
/// The auto-generated SignedSpecPack tests cover claim-to-test mapping;
/// these are the hand-shaped fixtures the engineer will flesh out.
/// </summary>
public class ConsumeRollServiceTests
{
    [Fact] // INV-1
    public void Consume_WhenRollNotFound_ReturnsNotFound_ContractExists()
    {
        // Pre-impl: assert the contract surface (repository + service)
        // exists. The engineer un-stubs this once ConsumeAsync runs.
        var repo = new Mock<IRollRepository>();
        var sut = new ConsumeRollService(repo.Object, Mock.Of<IEventNotifier>());
        Assert.NotNull(sut);
    }

    [Fact] // INV-2
    public void Consume_WhenRollLocked_ReturnsLocked_ContractExists()
    {
        var roll = new Roll("R001", 100m, 1, "GR1", Locked: true);
        Assert.True(roll.Locked, "Roll DTO carries Locked flag — INV-2 contract surface.");
    }

    [Fact] // INV-3
    public void Consume_WhenUsedExceedsOnHand_ReturnsInsufficient_ContractExists()
    {
        // Result enum carries the Insufficient case the spec calls out.
        Assert.Contains(ConsumeRollResult.Insufficient, System.Enum.GetValues<ConsumeRollResult>());
    }

    [Fact] // INV-5
    public void Consume_WhenRemainingBelowThreshold_SetsDepletedStatus_ContractExists()
    {
        // MIN_REMAIN is surfaced as a typed constant per the signed spec.
        Assert.Equal(12.0m, ConsumeRollService.MinRemainLf);
    }

    [Fact] // INV-6
    public void Consume_OnSuccess_NotifiesCsc_ContractExists()
    {
        // IEventNotifier is in the DI surface — INV-6 emission contract.
        Assert.NotNull(typeof(IEventNotifier));
    }

    [Fact] // EC-4
    public void Consume_WhenWriteFails_ReturnsNotFound_ContractExists()
    {
        // RESULT_CD=1 overload (not-found + write-fail). The enum
        // carries NotFound; behaviour assertion lands when the
        // engineer wires the write-fail branch.
        Assert.Equal((int)ConsumeRollResult.NotFound, 1);
    }
}
