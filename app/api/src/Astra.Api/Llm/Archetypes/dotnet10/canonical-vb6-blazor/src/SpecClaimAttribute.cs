// SPDX-Archetype: canonical-vb6-blazor

namespace Demo.OrderEntry.Web;

[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class SpecClaimAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}
