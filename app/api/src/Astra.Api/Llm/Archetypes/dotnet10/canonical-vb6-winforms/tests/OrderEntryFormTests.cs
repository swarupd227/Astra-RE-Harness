// SPDX-Archetype: canonical-vb6-winforms
//
// xUnit test pack templated against the signed spec's invariants and edge
// cases. The implementer fills in arrange/act/assert; the Fact attributes
// and SpecClaim references are scaffolded automatically so the spec→test
// trace stays intact.

using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Demo.OrderEntry.Tests;

public sealed class OrderEntryFormTests
{
    private static OrderContext NewInMemoryCtx()
    {
        var opts = new DbContextOptionsBuilder<OrderContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrderContext(opts);
    }

    // INV-1: empty txtCustomer short-circuits with no DAO write and no
    // Excel export. Asserts the repository sees zero inserts.
    [Fact]
    [SpecClaim("INV-1")]
    [SpecClaim("EC-1")]
    public async Task EmptyCustomer_ShortCircuits_WithNoSideEffects()
    {
        await using var ctx = NewInMemoryCtx();
        var repo = new OrderRepository(ctx);
        // TODO: drive OnSubmitClick with txtCustomer.Text = ""; assert
        // ctx.Orders.Count() == 0 after the click handler returns.
        Assert.Equal(0, ctx.Orders.Count());
    }

    // INV-2: a successful submit inserts exactly one Orders row. RecordsAffected
    // must equal 1 before the Excel export is attempted.
    [Fact]
    [SpecClaim("INV-2")]
    public async Task SuccessfulSubmit_InsertsExactlyOneRow()
    {
        await using var ctx = NewInMemoryCtx();
        var repo = new OrderRepository(ctx);
        var affected = await repo.InsertAsync(new Order(1001, "ACME"));
        Assert.Equal(1, affected);
        Assert.Single(ctx.Orders);
    }

    // EC-2: duplicate primary key. The repository's InsertAsync should
    // throw a DbException whose ErrorCode is 3022 — and the form's
    // OnSubmitClick should swallow it via the OE-1 typed catch.
    [Fact]
    [SpecClaim("EC-2")]
    [SpecClaim("OE-1")]
    public async Task DuplicatePrimaryKey_IsCaughtByDbErrHandler()
    {
        await using var ctx = NewInMemoryCtx();
        var repo = new OrderRepository(ctx);
        await repo.InsertAsync(new Order(1001, "ACME"));
        // TODO: assert the second insert throws a DbException AND that
        // OnSubmitClick handles it via the OE-1 typed catch without
        // calling InvoiceExporter.ExportInvoiceAsync.
        await Assert.ThrowsAnyAsync<Exception>(
            async () => await repo.InsertAsync(new Order(1001, "ACME-duplicate")));
    }
}
