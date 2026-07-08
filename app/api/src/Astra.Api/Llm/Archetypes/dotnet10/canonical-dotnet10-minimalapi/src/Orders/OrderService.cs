using Demo.OrderApi;

namespace Demo.OrderApi.Orders;

/// <summary>
/// Encapsulates order business rules previously spread across ASP.NET MVC
/// action methods and static helper classes. Now a Scoped service with
/// constructor-injected dependencies — no static HttpContext access.
/// </summary>
public sealed class OrderService(IOrderRepository repo, ILogger<OrderService> logger)
{
    // [INV-1] The total returned is the sum of (UnitPrice × Quantity) for all
    //         lines, rounded to 2 decimal places.
    // [EC-1]  Empty line collections return 0.00m — never throw.
    [SpecClaim("INV-1", "EC-1")]
    public async Task<decimal> CalculateTotalAsync(Guid orderId, CancellationToken ct = default)
    {
        var lines = await repo.GetOrderLinesAsync(orderId, ct);
        if (lines.Count == 0)
            return 0m;

        var total = lines.Sum(l => l.UnitPrice * l.Quantity);
        return Math.Round(total, 2, MidpointRounding.AwayFromZero);
    }

    // [INV-2] CreateOrderAsync persists the order and returns its assigned ID.
    //         The caller is responsible for providing at least one line.
    // [SE-1]  Writes to the Orders and OrderLines tables.
    // [AP-1]  Replaces former .Result call — now fully async.
    [SpecClaim("INV-2", "SE-1", "AP-1")]
    public async Task<Guid> CreateOrderAsync(CreateOrderRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lines.Count == 0)
            throw new ArgumentException("An order must have at least one line.", nameof(request));

        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = request.CustomerId,
            CreatedAt = DateTimeOffset.UtcNow,
            Lines = request.Lines.Select(l => new OrderLine
            {
                Id = Guid.NewGuid(),
                ProductId = l.ProductId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
            }).ToList(),
        };

        await repo.AddAsync(order, ct);
        logger.LogInformation("Order {OrderId} created for customer {CustomerId}", order.Id, request.CustomerId);
        return order.Id;
    }
}
