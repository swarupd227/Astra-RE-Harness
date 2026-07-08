using Demo.CustomerApi.Models;
using Demo.CustomerApi.Repositories;

namespace Demo.CustomerApi.Services;

public sealed class CustomerService(
    ICustomerRepository repo,
    ILogger<CustomerService> logger)
{
    // [INV-1] Returns null when no customer with the given id exists.
    // [INV-2] CreateAsync persists the customer and returns its ID.
    // [SE-1]  Writes to the Customers table.
    [SpecClaim("INV-1", "INV-2", "SE-1")]
    public async Task<Guid> CreateAsync(CreateCustomerRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var id = Guid.NewGuid();
        await repo.InsertAsync(new()
        {
            Id = id,
            Name = request.Name,
            Email = request.Email,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);

        logger.LogInformation("Customer {CustomerId} created", id);
        return id;
    }

    [SpecClaim("INV-1")]
    public Task<CustomerDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => repo.FindByIdAsync(id, ct);

    // [EC-1] Returns empty list, not null, when no results match.
    [SpecClaim("EC-1")]
    public Task<IReadOnlyList<CustomerDto>> SearchAsync(string search, CancellationToken ct = default)
        => repo.SearchAsync(search, ct);
}
