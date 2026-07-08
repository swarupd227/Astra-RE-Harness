// SPDX-Spec: php/Acme-Storefront (signed)
// SPDX-Archetype: canonical-php-catalog-service (dotnet8)
//
// Acceptance harness — the RUNNABLE verification the csharp-sidecar executes via
// /compile-and-run. The platform's csharp-sidecar has no xUnit runner (it does
// `dotnet publish` + run a Main), so a runnable Main is how we genuinely EXECUTE
// and assert the C# domain logic offline, not merely compile it. Each Check
// mirrors a signed-claim assertion; Main exits non-zero if any fails. In a real
// project these become xUnit [Fact]s (which `dotnet test` would run).
namespace Acme.Catalog;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static void Check(string name, Action body)
    {
        try { body(); _passed++; Console.WriteLine($"PASS  {name}"); }
        catch (Exception ex) { _failed++; Console.WriteLine($"FAIL  {name}: {ex.Message}"); }
    }

    private static void Eq(decimal actual, decimal expected, string what)
    {
        if (actual != expected) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    private static void Eq(int actual, int expected, string what)
    {
        if (actual != expected) throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    private static void Throws<T>(Action a, string what) where T : Exception
    {
        try { a(); }
        catch (T) { return; }
        catch (Exception ex) { throw new Exception($"{what}: wrong exception {ex.GetType().Name}"); }
        throw new Exception($"{what}: expected {typeof(T).Name}, nothing thrown");
    }

    private sealed class FakeSession : ISessionCart
    {
        public Dictionary<string, CartLine> Cart { get; } = new();
        public IDictionary<string, CartLine> GetCart() => Cart;
        public void PutLine(CartLine line) => Cart[line.Sku] = line;
    }

    private sealed class FakeRemote : IRemotePrice
    {
        private readonly decimal? _r;
        public FakeRemote(decimal? r) => _r = r;
        public decimal? Fetch(string url) => _r;
    }

    public static int Main()
    {
        Console.WriteLine("=== Acme.Catalog acceptance harness (PHP -> .NET 8) ===");

        var coupon = new CouponService();
        Check("LTC-1 SAVE10 applies 10%", () => Eq(coupon.ApplyCoupon(100m, "SAVE10"), 90.00m, "SAVE10"));
        Check("LTC-1 \"0\" is a real code, NOT false", () => Eq(coupon.ApplyCoupon(100m, "0"), 100m, "\"0\""));
        Check("null code -> no discount", () => Eq(coupon.ApplyCoupon(100m, null), 100m, "null"));
        Check("unknown code -> no discount", () => Eq(coupon.ApplyCoupon(100m, "BOGUS"), 100m, "BOGUS"));

        var qr = new QuantityResolver();
        Check("NUL-1 \"0\" preserved (not forced to 1)", () => Eq(qr.ResolveQty(new Dictionary<string, string> { ["qty"] = "0" }), 0, "0"));
        Check("absent -> default 1", () => Eq(qr.ResolveQty(null), 1, "null"));
        Check("empty dict -> default 1", () => Eq(qr.ResolveQty(new Dictionary<string, string>()), 1, "{}"));
        Check("blank -> default 1", () => Eq(qr.ResolveQty(new Dictionary<string, string> { ["qty"] = "   " }), 1, "blank"));
        Check("\"5\" -> 5", () => Eq(qr.ResolveQty(new Dictionary<string, string> { ["qty"] = "5" }), 5, "5"));
        Check("EC-1 garbage rejected", () => Throws<ArgumentException>(
            () => qr.ResolveQty(new Dictionary<string, string> { ["qty"] = "5 apples" }), "garbage"));

        Check("EC-1 decimal exact 0.10*3 == 0.30", () => Eq(MoneyMath.LineTotal(0.10m, 3), 0.30m, "0.10*3"));
        Check("INV-1 round 2.005 -> 2.01", () => Eq(MoneyMath.LineTotal(2.005m, 1), 2.01m, "2.005"));
        Check("LTC-1 zero total via typed compare", () => Eq(MoneyMath.LineTotal(9.99m, 0), 0m, "zero"));
        Check("negative qty rejected", () => Throws<ArgumentException>(() => MoneyMath.LineTotal(1.00m, -1), "neg"));

        Check("SG-1/SE-1 add new line writes to session", () =>
        {
            var s = new FakeSession();
            Eq(new CartService(s).AddToCart("WIDGET", 2, 5.00m), 2, "add");
            if (!s.Cart.ContainsKey("WIDGET")) throw new Exception("not written to session");
        });
        Check("merge existing line", () =>
        {
            var s = new FakeSession();
            s.Cart["WIDGET"] = new CartLine("WIDGET", 2, 5.00m);
            Eq(new CartService(s).AddToCart("WIDGET", 3, 5.00m), 5, "merge");
        });
        Check("ARR-1/INV-1 cart total 14.50", () =>
        {
            var s = new FakeSession();
            s.Cart["A"] = new CartLine("A", 2, 5.00m);
            s.Cart["B"] = new CartLine("B", 3, 1.50m);
            Eq(new CartService(s).CartTotal(), 14.50m, "total");
        });

        Check("EH-1 present price returned", () => Eq(new PriceFetcher(new FakeRemote(12.50m)).FetchOrThrow("u"), 12.50m, "present"));
        Check("EH-1 failure THROWS (not silent 0)", () => Throws<PriceUnavailableException>(
            () => new PriceFetcher(new FakeRemote(null)).FetchOrThrow("u"), "throws"));
        Check("EH-1 fallback is explicit", () => Eq(new PriceFetcher(new FakeRemote(null)).FetchOrDefault("u", 9.99m), 9.99m, "fallback"));

        Console.WriteLine($"=== {_passed} passed, {_failed} failed ===");
        return _failed == 0 ? 0 : 1;
    }
}
