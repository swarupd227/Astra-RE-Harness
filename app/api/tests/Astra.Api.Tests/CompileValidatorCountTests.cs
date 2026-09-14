using Astra.Api.Validation;
using Xunit;

namespace Astra.Api.Tests;

public class CompileValidatorCountTests
{
    [Fact]
    public void Msbuild_duplicates_are_counted_once()
    {
        const string log = """
                 1>/tmp/x/src/A.cs(3,27): error CS8956: File-scoped namespace must precede all other members in a file. [/tmp/x/Demo.csproj]
                 1>/tmp/x/src/B.cs(3,1): error CS8802: Only one compilation unit can have top-level statements. [/tmp/x/Demo.csproj]
                 1>/tmp/x/src/A.cs(9,5): warning CS8618: Non-nullable property 'Name' must contain a non-null value. [/tmp/x/Demo.csproj]
                     /tmp/x/src/A.cs(3,27): error CS8956: File-scoped namespace must precede all other members in a file. [/tmp/x/Demo.csproj]
                     /tmp/x/src/B.cs(3,1): error CS8802: Only one compilation unit can have top-level statements. [/tmp/x/Demo.csproj]
                     /tmp/x/src/A.cs(9,5): warning CS8618: Non-nullable property 'Name' must contain a non-null value. [/tmp/x/Demo.csproj]
                2 Error(s)
                1 Warning(s)
            """;
        var (errors, warnings) = CompileValidator.CountDiagnostics(log);
        Assert.Equal(2, errors);
        Assert.Equal(1, warnings);
    }

    [Fact]
    public void Clean_log_counts_nothing()
    {
        var (errors, warnings) = CompileValidator.CountDiagnostics("Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n");
        Assert.Equal(0, errors);
        Assert.Equal(0, warnings);
    }
}
