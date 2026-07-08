using Asp.Versioning;
using Demo.CustomerApi.Models;
using Demo.CustomerApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace Demo.CustomerApi.Controllers;

/// <summary>
/// Replaces the former System.Web.Http.ApiController-derived controller.
/// Key changes: [ApiController] auto-validates ModelState (no explicit checks);
/// IHttpActionResult → IActionResult; [FromUri] → [FromQuery]; OkNegotiatedContentResult → Ok(T).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/[controller]")]
public sealed class CustomersController(
    CustomerService customerService,
    ILogger<CustomersController> logger) : ControllerBase
{
    // [INV-1] GET /api/v1/customers/{id} returns the customer DTO when found,
    //         or RFC 9457 Problem Details with status 404 when not found.
    // [OA-1]  Replaces IHttpActionResult + NotFound() from ApiController.
    // [OA-2]  Route attribute replaces [RoutePrefix] on class + [Route] on method from Web API 2.
    [HttpGet("{id:guid}")]
    [SpecClaim("INV-1", "OA-1", "OA-2")]
    public async Task<ActionResult<CustomerDto>> GetCustomer(Guid id, CancellationToken ct)
    {
        var customer = await customerService.GetByIdAsync(id, ct);
        if (customer is null)
            return NotFound();   // [ApiController] produces RFC 9457 automatically

        return Ok(customer);
    }

    // [INV-2] POST /api/v1/customers creates a customer and returns 201 + Location header.
    // [AP-1]  Async end-to-end — no .Result or .Wait().
    // [EH-1]  Validation errors surface as ValidationProblem (RFC 9457) via [ApiController].
    //         No explicit ModelState.IsValid check needed.
    [HttpPost]
    [SpecClaim("INV-2", "AP-1", "EH-1")]
    public async Task<ActionResult<CustomerDto>> CreateCustomer(
        [FromBody] CreateCustomerRequest request,
        CancellationToken ct)
    {
        var id = await customerService.CreateAsync(request, ct);
        var dto = await customerService.GetByIdAsync(id, ct);
        return CreatedAtAction(nameof(GetCustomer), new { id }, dto);
    }

    // [EC-1] GET /api/v1/customers?search=... returns an empty array (not 404)
    //        when no customers match the search term.
    [HttpGet]
    [SpecClaim("EC-1")]
    public async Task<ActionResult<IReadOnlyList<CustomerDto>>> SearchCustomers(
        [FromQuery] string? search,
        CancellationToken ct)
    {
        var results = await customerService.SearchAsync(search ?? string.Empty, ct);
        return Ok(results);
    }
}
