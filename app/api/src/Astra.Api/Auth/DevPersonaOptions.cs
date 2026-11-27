namespace Astra.Api.Auth;

/// <summary>
///     Configuration for the dev-persona auth shim used in Phase A.
///     OIDC + Microsoft Entra ID replace this entire mechanism in Phase C.
/// </summary>
public sealed class DevPersonaOptions
{
    public bool DevPersonaBypass { get; set; } = true;
    public string DevPersonaDefault { get; set; } = "engineer";

    /// <summary>
    /// "dev-header" (default: local development and e2e; the X-Dev-Persona header says who you are) or "password"
    /// (a deployed platform: every API call needs a signed-in session; the header only switches between the personas
    /// the signed-in user holds).
    /// </summary>
    public string Mode { get; set; } = AuthModes.DevHeader;

    /// <summary>Password mode: the first admin, created at start-up when no user exists yet. Set by the operator in the
    /// host's settings (never committed); only its hash is stored.</summary>
    public string? BootstrapAdminUser { get; set; }
    public string? BootstrapAdminPassword { get; set; }

    /// <summary>How long a sign-in lasts.</summary>
    public int SessionHours { get; set; } = 12;

    public bool PasswordMode => string.Equals(Mode, AuthModes.Password, StringComparison.OrdinalIgnoreCase);
}

public static class AuthModes
{
    public const string DevHeader = "dev-header";
    public const string Password = "password";
}
