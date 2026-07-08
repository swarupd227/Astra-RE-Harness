namespace Demo.CustomerApi.Models;

public sealed record CustomerDto(
    Guid Id,
    string Name,
    string Email,
    DateTimeOffset CreatedAt);

public sealed record CreateCustomerRequest(
    string Name,
    string Email);
