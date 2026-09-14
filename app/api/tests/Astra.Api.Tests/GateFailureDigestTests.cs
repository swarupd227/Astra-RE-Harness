using Astra.Api.Validation;
using Xunit;

namespace Astra.Api.Tests;

public class GateFailureDigestTests
{
    // Exactly as the compile gate's log reads: MSBuild indents the lines,
    // prefixes them with the project number, and repeats each one in the
    // summary block without the prefix.
    private const string CompileLog = """
        === dotnet build (cwd=/tmp/astra-validate-e540694cfa524142a41a15f3f5a824c0) ===
        MSBuild version 17.8.3+195e7f5a3 for .NET
          Determining projects to restore...
             1>/tmp/astra-validate-e540694cfa524142a41a15f3f5a824c0/src/Formatter.cs(42,13): error CS0103: The name 'Trim2' does not exist in the current context [/tmp/astra-validate-e540694cfa524142a41a15f3f5a824c0/Demo.Fmt.csproj]
             1>/tmp/astra-validate-e540694cfa524142a41a15f3f5a824c0/src/FormatString.cs(7,7): error CS0246: The type or namespace name 'MailKit' could not be found (are you missing a using directive or an assembly reference?) [/tmp/astra-validate-e540694cfa524142a41a15f3f5a824c0/Demo.Fmt.csproj]
                 /tmp/astra-validate-e540694cfa524142a41a15f3f5a824c0/src/Formatter.cs(42,13): error CS0103: The name 'Trim2' does not exist in the current context [/tmp/astra-validate-e540694cfa524142a41a15f3f5a824c0/Demo.Fmt.csproj]
                 /tmp/astra-validate-e540694cfa524142a41a15f3f5a824c0/src/FormatString.cs(7,7): error CS0246: The type or namespace name 'MailKit' could not be found (are you missing a using directive or an assembly reference?) [/tmp/astra-validate-e540694cfa524142a41a15f3f5a824c0/Demo.Fmt.csproj]
            2 Error(s)
        """;

    private const string TestLog = """
        Starting test execution, please wait...
        A total of 1 test files matched the specified pattern.
        [xUnit.net 00:00:00.61]     Demo.Fmt.Tests.FormatterTests.Rejects_empty_format_string [FAIL]
          Failed Demo.Fmt.Tests.FormatterTests.Rejects_empty_format_string [12 ms]
          Error Message:
           Assert.Throws() Failure: No exception was thrown
          Stack Trace:
             at Demo.Fmt.Tests.FormatterTests.Rejects_empty_format_string() in /tmp/x/tests/FormatterTests.cs:line 31

        Failed!  - Failed:     1, Passed:     4, Skipped:     0, Total:     5, Duration: 48 ms
        """;

    [Fact]
    public void Compile_errors_are_parsed_deduplicated_and_explained()
    {
        var d = GateFailureDigest.Parse("COMPILE", CompileLog);
        Assert.Equal(2, d.ErrorCount);
        Assert.Equal("src/Formatter.cs", d.Errors[0].File);
        Assert.Equal(42, d.Errors[0].Line);
        Assert.Equal("CS0103", d.Errors[0].Code);
        Assert.Equal("CS0246", d.Errors[1].Code);

        var md = d.ToMarkdown("fmt::format");
        Assert.Contains("failed with 2 errors", md);
        Assert.Contains("`CS0103`", md);
        Assert.Contains("used before it is declared", md);
        Assert.Contains("regenerate", md);

        var hint = d.ToRepairHint();
        Assert.Contains("2 compiler error(s)", hint);
        Assert.Contains("CS0246", hint);
    }

    [Fact]
    public void Test_failures_carry_the_assertion_message()
    {
        var d = GateFailureDigest.Parse("TEST_PACK", TestLog);
        Assert.Equal(0, d.ErrorCount);
        Assert.Single(d.FailedTests);
        Assert.Equal("Demo.Fmt.Tests.FormatterTests.Rejects_empty_format_string", d.FailedTests[0].Name);
        Assert.Contains("No exception was thrown", d.FailedTests[0].Message);

        var md = d.ToMarkdown("fmt::format");
        Assert.Contains("1 failing test", md);
        Assert.Contains("`Rejects_empty_format_string`", md);
        Assert.Contains("signed spec", md);
    }

    [Fact]
    public void Runner_problems_are_named_as_the_platform_not_the_code()
    {
        var d = GateFailureDigest.Parse("COMPILE", "Runner error: TimeoutException: dotnet build did not finish within 300s.");
        Assert.Equal(0, d.ErrorCount);
        Assert.NotNull(d.RunnerProblem);
        Assert.Contains("the platform, not the generated code", d.ToMarkdown("X"));
    }

    [Fact]
    public void Unrecognised_log_falls_back_to_the_gate_summary()
    {
        var d = GateFailureDigest.Parse("COMPILE", "some unrelated output\n");
        var md = d.ToMarkdown("X", "Build failed · 1 error");
        Assert.Contains("Build failed · 1 error", md);
        Assert.Contains("open it", md);
    }
}
