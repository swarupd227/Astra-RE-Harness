namespace Astra.Api.Auth;

/// <summary>
///     Per-request persona context, populated by <see cref="DevPersonaMiddleware"/>.
///     Replaced by claims-principal resolution once OIDC is wired in Phase C.
/// </summary>
public sealed class DevPersonaContext
{
    public Persona Persona { get; set; } = Persona.Engineer;
    public string DisplayName { get; set; } = "Dev User";
    public bool IsBypass { get; set; } = true;
}

public enum Persona
{
    Engineer,
    Sme,
    Observer,
    Admin
}

public static class PersonaExtensions
{
    public static Persona ParsePersona(this string? value, Persona fallback) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "engineer" => Persona.Engineer,
            "sme"      => Persona.Sme,
            "observer" => Persona.Observer,
            "admin"    => Persona.Admin,
            _          => fallback
        };
}
