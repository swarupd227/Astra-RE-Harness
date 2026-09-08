using Demo.WidgetApi;

namespace Demo.WidgetApi.Models;

/// <summary>A stored widget, as returned to the Angular client.</summary>
[SpecClaim("INV-1")]
public sealed record Widget(int Id, string Name, int Quantity);
