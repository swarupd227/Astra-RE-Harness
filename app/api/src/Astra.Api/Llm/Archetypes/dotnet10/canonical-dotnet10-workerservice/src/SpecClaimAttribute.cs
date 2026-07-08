namespace Demo.BatchWorker;

/// <summary>
/// Links a code element to the signed spec claim(s) it satisfies.
/// Scraped by the harness to produce the claim-coverage matrix.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class SpecClaimAttribute(params string[] claimIds) : Attribute
{
    public string[] ClaimIds { get; } = claimIds;
}
