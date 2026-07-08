// SPDX-Spec: php/fetch_price.php (signed)
// SPDX-Archetype: canonical-php-catalog-service (dotnet8)
namespace Acme.Catalog;

/// <summary>
/// Port for the PHP <c>@file_get_contents($url)</c> + <c>json_decode</c> lookup.
/// A null result models the PHP failure path (false → null) so the caller must
/// handle absence EXPLICITLY rather than via the silent <c>@</c> operator.
/// </summary>
[TargetMapping("HttpClient price gateway", PhpConstruct = "@file_get_contents + json_decode")]
public interface IRemotePrice
{
    /// <summary>The remote price, or null if unreachable/unparseable.</summary>
    [SpecClaim("SE-1")]
    decimal? Fetch(string url);
}

/// <summary>Typed exception replacing PHP's silent @-suppressed failure (EH-1).</summary>
public sealed class PriceUnavailableException : Exception
{
    public PriceUnavailableException(string url)
        : base($"Remote price unavailable for: {url}") { }
}

/// <summary>
/// C# projection of PHP fetch_price.php. PHP's <c>@</c> + false-return swallowed
/// failures and returned 0.0. EH-1: the migration surfaces failure EXPLICITLY —
/// throw, or fall back to a caller-supplied default, never a silent 0.
/// </summary>
[TargetMapping("service class", PhpConstruct = "fetch_price.php")]
public sealed class PriceFetcher
{
    private readonly IRemotePrice _port;

    public PriceFetcher(IRemotePrice port) => _port = port;

    /// <summary>Fetch the price or throw — never swallowed.</summary>
    [SpecClaim("EH-1")]
    public decimal FetchOrThrow(string url)
        => _port.Fetch(url) ?? throw new PriceUnavailableException(url);

    /// <summary>Fetch the price or fall back to an EXPLICIT default (not a silent 0).</summary>
    [SpecClaim("EH-1")]
    public decimal FetchOrDefault(string url, decimal fallback)
        => _port.Fetch(url) ?? fallback;
}
