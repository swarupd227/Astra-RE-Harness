// Test anchor for faithful 1:1 packages. The signed-spec test pack the
// validation gate generates lands in this directory and namespace; this
// file only proves the provenance attributes ship and the package builds.
using Faithful.Provenance;
using Xunit;

namespace Faithful.Tests;

public class UnitTests
{
    [Fact]
    public void Provenance_attributes_are_available_to_converted_code()
    {
        var routine = new SourceRoutineAttribute("IdCounter.pas", "TIdCounter.Increment", 48, 56);
        Assert.Equal("TIdCounter.Increment", routine.Routine);
        Assert.True(routine.LineEnd >= routine.LineStart);
        Assert.Equal("INV-1", new SpecClaimAttribute("INV-1").Id);
    }
}
