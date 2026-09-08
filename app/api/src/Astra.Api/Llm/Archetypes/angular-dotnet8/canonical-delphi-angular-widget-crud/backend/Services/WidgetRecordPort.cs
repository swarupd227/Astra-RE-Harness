using System.Collections.Concurrent;
using Demo.WidgetApi;
using Demo.WidgetApi.Models;

namespace Demo.WidgetApi.Services;

/// <summary>
/// Data-access seam for widgets — the .NET projection of whatever the
/// legacy Delphi routine actually persisted to (a file, a table, an
/// external service). Swap the implementation for a real store; the
/// service and controller layers above it don't change.
/// </summary>
public interface IWidgetRecordPort
{
    IReadOnlyList<Widget> List();
    Widget Add(string name, int quantity);
    bool Remove(int id);
}

/// <summary>
/// In-memory reference implementation. Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// so concurrent Angular requests don't corrupt the id sequence (SE-1).
/// </summary>
[SpecClaim("SE-1")]
public sealed class InMemoryWidgetRecordPort : IWidgetRecordPort
{
    private readonly ConcurrentDictionary<int, Widget> _widgets = new();
    private int _nextId;

    public IReadOnlyList<Widget> List() =>
        _widgets.Values.OrderBy(w => w.Id).ToList();

    [SpecClaim("INV-1")]
    public Widget Add(string name, int quantity)
    {
        var id = Interlocked.Increment(ref _nextId);
        var widget = new Widget(id, name, quantity);
        _widgets[id] = widget;
        return widget;
    }

    public bool Remove(int id) => _widgets.TryRemove(id, out _);
}
