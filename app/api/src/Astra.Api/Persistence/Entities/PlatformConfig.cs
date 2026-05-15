namespace Astra.Api.Persistence.Entities;

/// <summary>
/// Generic key/value platform configuration row.
///
/// Used by Phase #4 Admin-CRUD surfaces (validation policy overrides,
/// language enable/disable, prompt-version pins, …) so each surface
/// doesn't need its own table for what is conceptually one small JSON
/// blob per concern.
///
/// Key conventions:
///   "validation.policy"     — JSON override of the static gate config
///   "languages.enabled"     — JSON map of language id → bool
///   "prompts.pinned"        — JSON map of project id → prompt version
/// </summary>
public sealed class PlatformConfig
{
    /// <summary>Stable string key (e.g. "validation.policy").</summary>
    public string Key { get; set; } = "";

    /// <summary>JSON payload. Schema is per-key, not enforced by the table.</summary>
    public string ValueJson { get; set; } = "{}";

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
    public string UpdatedByDisplay { get; set; } = "";
}
