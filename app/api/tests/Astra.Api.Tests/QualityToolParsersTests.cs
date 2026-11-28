using Astra.Api.Validation.Quality;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>ESLint's JSON and the .NET analyzers' build warnings become quality findings with the right weight.</summary>
public class QualityToolParsersTests
{
    [Fact]
    public void Eslint_output_becomes_findings_with_package_paths_and_weights()
    {
        const string json = """
            [{"filePath":"/tmp/astra-jsquality-1/frontend/src/bad.ts","messages":[
               {"ruleId":"no-eval","severity":2,"message":"`eval` can be harmful.","line":1},
               {"ruleId":"security/detect-non-literal-regexp","severity":1,"message":"RegExp() called with a variable","line":4},
               {"ruleId":"react-hooks/rules-of-hooks","severity":2,"message":"React Hook \"useState\" is called conditionally.","line":2},
               {"ruleId":"complexity","severity":1,"message":"Function has a complexity of 20.","line":9},
               {"ruleId":"@typescript-eslint/no-unused-vars","severity":2,"message":"'x' is defined but never used.","line":3},
               {"ruleId":null,"severity":2,"fatal":true,"message":"Parsing error: ';' expected.","line":7}]},
             {"filePath":"/tmp/astra-jsquality-1/frontend/src/ok.ts","messages":[]}]
            """;
        var f = JsTools.ParseEslint(json, "/tmp/astra-jsquality-1");
        Assert.Equal(6, f.Count);
        Assert.All(f, x => Assert.Equal("frontend/src/bad.ts", x.File));
        Assert.All(f, x => Assert.Equal("tool", x.Source));
        Assert.All(f, x => Assert.DoesNotContain(x.Severity, QualityRules.BlockingByDefault));
        Assert.Equal(("major", "security"), (f[0].Severity, f[0].Category));
        Assert.Equal("ESLINT-no-eval", f[0].Rule);
        Assert.Equal(("minor", "security"), (f[1].Severity, f[1].Category));
        Assert.Equal(("major", "standards"), (f[2].Severity, f[2].Category));
        Assert.Equal(("minor", "maintainability"), (f[3].Severity, f[3].Category));
        Assert.Equal("ESLINT-parse", f[5].Rule);
        Assert.Equal(7, f[5].Line);
    }

    [Theory]
    [InlineData("frontend/src/App.tsx", true)]
    [InlineData("frontend/vite.config.ts", true)]
    [InlineData("frontend/jest.config.cjs", true)]
    [InlineData("frontend/src/vite-env.d.ts", false)]
    [InlineData("frontend/node_modules/x/index.js", false)]
    [InlineData("frontend/dist/assets/index.js", false)]
    [InlineData("backend/app/api.py", false)]
    public void Eslint_reads_script_source_only(string path, bool linted) => Assert.Equal(linted, JsTools.IsLinted(path));

    [Fact]
    public void Analyzer_warnings_become_findings_once_each_with_injection_critical()
    {
        const string log = """
              Determining projects to restore...
            /tmp/astra-dnquality-1/src/Repo.cs(12,13): warning CA2100: Review if the query string passed to 'SqlCommand' accepts any user input [/tmp/astra-dnquality-1/src/App.csproj]
            /tmp/astra-dnquality-1/src/Repo.cs(12,13): warning CA2100: Review if the query string passed to 'SqlCommand' accepts any user input [/tmp/astra-dnquality-1/src/App.csproj]
            /tmp/astra-dnquality-1/src/Crypto.cs(5,9): warning CA5351: Do Not Use Broken Cryptographic Algorithms [/tmp/astra-dnquality-1/src/App.csproj]
            /tmp/astra-dnquality-1/src/Svc.cs(30,1): warning CA2016: Forward the 'CancellationToken' parameter to methods [/tmp/astra-dnquality-1/src/App.csproj]
            /tmp/astra-dnquality-1/src/Svc.cs(31,1): warning CS8618: Non-nullable property must contain a value [/tmp/astra-dnquality-1/src/App.csproj]
            /tmp/astra-dnquality-1/src/Svc.cs(40,1): error CS1002: ; expected [/tmp/astra-dnquality-1/src/App.csproj]
            """;
        var f = DotnetAnalyzers.ParseBuildLog(log, "/tmp/astra-dnquality-1", "backend/");
        Assert.Equal(3, f.Count);
        Assert.Equal("DOTNET-CA2100", f[0].Rule);
        Assert.Equal("backend/src/Repo.cs", f[0].File);
        Assert.Equal(12, f[0].Line);
        Assert.Equal("critical", f[0].Severity);
        Assert.Equal("Review if the query string passed to 'SqlCommand' accepts any user input", f[0].Message);
        Assert.Equal(("major", "security"), (f[1].Severity, f[1].Category));
        Assert.Equal(("minor", "standards"), (f[2].Severity, f[2].Category));
    }

    [Theory]
    [InlineData("dotnet10", "")]
    [InlineData("dotnet10-faithful", "")]
    [InlineData("dotnet8", "")]
    [InlineData("angular-dotnet8", "backend/")]
    [InlineData("react-python", null)]
    [InlineData("java-spring", null)]
    public void The_analyzers_build_where_the_dotnet_code_lives(string stack, string? prefix) =>
        Assert.Equal(prefix, DotnetAnalyzers.Prefix(stack));

    /// <summary>A real build with the SDK the tests run on: the analyzers report weak crypto in a tiny project.</summary>
    [Fact]
    public async Task The_analyzer_build_reports_weak_crypto_in_a_real_project()
    {
        var files = new List<(string, string)>
        {
            ("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><OutputType>Library</OutputType></PropertyGroup>
                </Project>
                """),
            ("src/App/Hasher.cs", """
                namespace App;
                public static class Hasher
                {
                    public static byte[] Hash(byte[] data) { using var md5 = System.Security.Cryptography.MD5.Create(); return md5.ComputeHash(data); }
                }
                """),
        };
        var (findings, runs) = await DotnetAnalyzers.RunAsync("dotnet10", files, CancellationToken.None);
        Assert.True(runs["dotnet-analyzers"].Ok, runs["dotnet-analyzers"].Error);
        var md5 = Assert.Single(findings, f => f.Rule == "DOTNET-CA5351");
        Assert.Equal("src/App/Hasher.cs", md5.File);
        Assert.Equal("security", md5.Category);
    }

    [Fact]
    public void The_analyzers_build_the_root_solution_or_else_each_project()
    {
        Assert.Equal([null], DotnetAnalyzers.BuildTargets(["App.sln", "src/App/App.csproj", "tests/T/T.csproj"]));
        Assert.Equal(["src/App/App.csproj", "tests/T/T.csproj"], DotnetAnalyzers.BuildTargets(["tests/T/T.csproj", "src/App/App.csproj", "src/App/A.cs"]));
    }

    [Fact]
    public void The_analyzer_build_switches_every_security_rule_on() =>
        Assert.Contains("-p:AnalysisModeSecurity=All", DotnetAnalyzers.BuildArguments);
}
