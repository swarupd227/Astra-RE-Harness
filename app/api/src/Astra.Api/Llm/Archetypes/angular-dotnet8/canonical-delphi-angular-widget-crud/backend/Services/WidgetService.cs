using Demo.WidgetApi;
using Demo.WidgetApi.Models;

namespace Demo.WidgetApi.Services;

/// <summary>
/// Business rules for widgets, independent of both the HTTP layer
/// (<c>WidgetsController</c>) and the storage layer (<c>IWidgetRecordPort</c>).
/// </summary>
[SpecClaim("EC-1")]
public sealed class WidgetService(IWidgetRecordPort recordPort)
{
    public IReadOnlyList<Widget> List() => recordPort.List();

    /// <summary>EC-1: a blank name is rejected rather than silently stored.</summary>
    [SpecClaim("EC-1")]
    public Widget Create(NewWidget request)
    {
        var name = request.Name.Trim();
        if (name.Length == 0)
            throw new ArgumentException("Widget name is required.", nameof(request));

        return recordPort.Add(name, request.Quantity);
    }

    public bool Delete(int id) => recordPort.Remove(id);
}
