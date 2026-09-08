// SPDX-Spec: delphi (signed)
// SPDX-Archetype: canonical-delphi-angular-widget-crud
//
// Minimal ASP.NET Core 8 Web API host for the Angular frontend in
// ../frontend. Registers the widget vertical (record port + service +
// controller) and a permissive dev CORS policy for the Angular dev
// server — a real deployment would replace AngularDevCors with the
// actual allowed origin(s) for its hosting environment.

using Demo.WidgetApi.Services;

namespace Demo.WidgetApi;

/// <summary>
/// Marks a public surface with the signed-spec claim id it implements, so
/// a reviewer can map generated code back to the spec without leaving the
/// IDE. Applies to both the ASP.NET Core controller/service layer and any
/// plain C# type in this package.
/// </summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class SpecClaimAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}

public static class Program
{
    private const string AngularDevCors = "AngularDevCors";

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddControllers();
        builder.Services.AddSingleton<IWidgetRecordPort, InMemoryWidgetRecordPort>();
        builder.Services.AddSingleton<WidgetService>();
        builder.Services.AddCors(options =>
        {
            options.AddPolicy(AngularDevCors, policy =>
                policy.WithOrigins("http://localhost:4200")
                      .AllowAnyHeader()
                      .AllowAnyMethod());
        });

        var app = builder.Build();

        app.UseCors(AngularDevCors);
        app.MapControllers();

        app.Run();
    }
}
