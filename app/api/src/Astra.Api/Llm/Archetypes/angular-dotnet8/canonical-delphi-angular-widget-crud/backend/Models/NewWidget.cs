namespace Demo.WidgetApi.Models;

/// <summary>The create-request payload — no Id; the record port assigns one.</summary>
public sealed record NewWidget(string Name, int Quantity);
