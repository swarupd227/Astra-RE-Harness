using Demo.WidgetApi;
using Demo.WidgetApi.Models;
using Demo.WidgetApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace Demo.WidgetApi.Controllers;

/// <summary>
/// REST surface the Angular <c>WidgetService</c> (frontend/src/app/widget.service.ts)
/// calls. Thin by design — every rule lives in <see cref="Services.WidgetService"/>;
/// this class only translates HTTP to/from that layer.
/// </summary>
[ApiController]
[Route("api/widgets")]
[SpecClaim("INV-1")]
public sealed class WidgetsController(WidgetService widgetService) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<Widget>> List() => Ok(widgetService.List());

    [HttpPost]
    [SpecClaim("EC-1")]
    public ActionResult<Widget> Create([FromBody] NewWidget request)
    {
        try
        {
            var created = widgetService.Create(request);
            return CreatedAtAction(nameof(List), new { }, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("{id:int}")]
    public IActionResult Delete(int id) =>
        widgetService.Delete(id) ? NoContent() : NotFound();
}
