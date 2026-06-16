// SPDX-Archetype: canonical-vb6-minapi
//
// Reused across every Phase 10 archetype: marks a C# member with the signed
// claim id it implements. Reviewers can grep for `[SpecClaim("INV-1")]` and
// jump to every code path the signed spec's claim INV-1 governs.

namespace Demo.OrderTotals;

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class SpecClaimAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}
