// SPDX-Spec: php/Acme-Storefront (signed)
// SPDX-Archetype: canonical-php-catalog-service (dotnet8)
namespace Acme.Catalog;

/// <summary>
/// Cites a signed spec/v1 claim id (e.g. "INV-1", "LTC-1", "SG-1") on the C#
/// surface that realises it — the .NET analogue of the Java @SpecClaim marker.
/// </summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class SpecClaimAttribute : Attribute
{
    public string Value { get; }
    public SpecClaimAttribute(string value) => Value = value;
}

/// <summary>
/// Records the .NET/ASP.NET construct this element is intended to BECOME on
/// promotion, plus the PHP construct it maps FROM (traceability). A documented
/// contract, not live wiring.
/// </summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class TargetMappingAttribute : Attribute
{
    public string Value { get; }
    public string PhpConstruct { get; init; } = "";
    public TargetMappingAttribute(string value) => Value = value;
}
