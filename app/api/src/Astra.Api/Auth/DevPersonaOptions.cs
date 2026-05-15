namespace Astra.Api.Auth;

/// <summary>
///     Configuration for the dev-persona auth shim used in Phase A.
///     OIDC + Microsoft Entra ID replace this entire mechanism in Phase C.
/// </summary>
public sealed class DevPersonaOptions
{
    public bool DevPersonaBypass { get; set; } = true;
    public string DevPersonaDefault { get; set; } = "engineer";
}
