using Astra.Api.Validation;
using Xunit;

namespace Astra.Api.Tests;

public class GateFailureRepairHintTests
{
    private const string Log = """
        === dotnet build (cwd=/tmp/astra-validate-0123456789abcdef0123456789abcdef) ===
          1>/tmp/astra-validate-0123456789abcdef0123456789abcdef/src/Stubs/MormotRestCore.cs(26,172): error CS1737: Optional parameters must appear after all required parameters [/tmp/astra-validate-0123456789abcdef0123456789abcdef/Faithful.Unit.csproj]
        Build FAILED.
        """;

    private const string Stub = """
        // TODO(faithful): stub for mormot.rest.core
        namespace Faithful.MormotRestCore;
        public class TRestOrm
        {
            public object RetrieveDocVariantArray(System.Type tableType, string where, object cache = null, out long lastID) { lastID = 0; throw new System.NotImplementedException(); }
        }
        """;

    [Fact]
    public void The_hint_quotes_the_offending_line_when_the_previous_package_is_available()
    {
        var digest = GateFailureDigest.Parse("COMPILE", Log);
        var files = new Dictionary<string, string> { ["src/Stubs/MormotRestCore.cs"] = string.Join("\n", Enumerable.Repeat("// filler", 25)) + "\n" + "    public object RetrieveDocVariantArray(System.Type tableType, string where, object cache = null, out long lastID) { lastID = 0; throw new System.NotImplementedException(); }" };

        var hint = digest.ToRepairHint(path => files.TryGetValue(path, out var c) ? c : null);

        Assert.Contains("`src/Stubs/MormotRestCore.cs:26` CS1737", hint);
        Assert.Contains("the line was: `public object RetrieveDocVariantArray(System.Type tableType, string where, object cache = null, out long lastID)", hint);
    }

    [Fact]
    public void Without_the_previous_package_the_hint_still_lists_the_error()
    {
        var hint = GateFailureDigest.Parse("COMPILE", Log).ToRepairHint();
        Assert.Contains("CS1737", hint);
        Assert.DoesNotContain("the line was", hint);
        Assert.Equal(hint, GateFailureDigest.Parse("COMPILE", Log).ToRepairHint(_ => null));
    }

    [Fact]
    public void A_line_number_past_the_file_end_is_ignored()
    {
        var hint = GateFailureDigest.Parse("COMPILE", Log).ToRepairHint(_ => Stub);
        Assert.DoesNotContain("the line was", hint);
    }
}
