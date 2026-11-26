using System.Text.Json;
using Astra.Api.Validation.Quality;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>Q4: declared libraries, the CycloneDX bill of materials, and the Python tools' output.</summary>
public class QualityDependenciesTests
{
    private const string Pom = """
        <project xmlns="http://maven.apache.org/POM/4.0.0">
          <parent><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-parent</artifactId><version>3.2.5</version></parent>
          <properties><jackson.version>2.17.0</jackson.version></properties>
          <dependencies>
            <dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-web</artifactId></dependency>
            <dependency><groupId>com.fasterxml.jackson.core</groupId><artifactId>jackson-databind</artifactId><version>${jackson.version}</version></dependency>
          </dependencies>
        </project>
        """;

    [Fact]
    public void Every_kind_of_manifest_is_read_with_the_version_it_declares()
    {
        var deps = Dependencies.Read([
            ("backend/requirements.txt", "fastapi==0.115.5\nuvicorn[standard]==0.32.1\n# comment\npydantic>=2.9.0\nhttpx\n"),
            ("frontend/package.json", """{"dependencies":{"react":"^18.3.1","@tanstack/react-query":"~5.59.0"},"devDependencies":{"vite":"5.4.10","local":"file:../x"}}"""),
            ("pom.xml", Pom),
            ("src/App/App.csproj", """<Project><ItemGroup><PackageReference Include="Serilog" Version="4.0.1" /></ItemGroup></Project>"""),
            ("frontend/node_modules/x/package.json", """{"dependencies":{"evil":"1.0.0"}}"""),
        ]);
        string V(string name) => deps.Single(d => d.Name == name).Version;
        Assert.Equal("0.115.5", V("fastapi"));
        Assert.Equal("0.32.1", V("uvicorn"));
        Assert.Equal("2.9.0", V("pydantic"));
        Assert.DoesNotContain(deps, d => d.Name == "httpx");               // no version, nothing to check
        Assert.Equal("18.3.1", V("react"));
        Assert.Equal("5.59.0", V("@tanstack/react-query"));
        Assert.DoesNotContain(deps, d => d.Name == "local");
        Assert.DoesNotContain(deps, d => d.Name == "evil");                 // node_modules is not the package's manifest
        Assert.Equal("3.2.5", V("org.springframework.boot:spring-boot-starter-parent"));
        Assert.Equal("", V("org.springframework.boot:spring-boot-starter-web"));   // version from the BOM
        Assert.Equal("2.17.0", V("com.fasterxml.jackson.core:jackson-databind"));  // property resolved
        Assert.Equal("4.0.1", V("Serilog"));
    }

    [Fact]
    public void Purls_follow_the_package_url_spec()
    {
        Assert.Equal("pkg:npm/%40tanstack/react-query@5.59.0", new Dependency("npm", "@tanstack/react-query", "5.59.0", "p").Purl);
        Assert.Equal("pkg:maven/com.fasterxml.jackson.core/jackson-databind@2.17.0", new Dependency("Maven", "com.fasterxml.jackson.core:jackson-databind", "2.17.0", "p").Purl);
        Assert.Equal("pkg:pypi/fastapi@0.115.5", new Dependency("PyPI", "FastAPI", "0.115.5", "p").Purl);
    }

    [Fact]
    public void The_bill_of_materials_is_cyclonedx_with_one_component_per_library()
    {
        var deps = new[] { new Dependency("PyPI", "fastapi", "0.115.5", "backend/requirements.txt"), new Dependency("Maven", "a:b", "", "pom.xml") };
        using var doc = JsonDocument.Parse(Dependencies.BuildSbom("PostJson", "1", deps, DateTimeOffset.Parse("2026-11-26T22:00:00Z")));
        var r = doc.RootElement;
        Assert.Equal("CycloneDX", r.GetProperty("bomFormat").GetString());
        Assert.Equal("1.5", r.GetProperty("specVersion").GetString());
        Assert.Equal("PostJson", r.GetProperty("metadata").GetProperty("component").GetProperty("name").GetString());
        var comps = r.GetProperty("components");
        Assert.Equal(2, comps.GetArrayLength());
        Assert.Equal("pkg:pypi/fastapi@0.115.5", comps[0].GetProperty("purl").GetString());
        Assert.Equal(JsonValueKind.Null, comps[1].GetProperty("purl").ValueKind);   // unversioned: no purl claimed
    }

    [Theory]
    [InlineData("CRITICAL", "critical")]
    [InlineData("HIGH", "critical")]
    [InlineData("MODERATE", "major")]
    [InlineData("LOW", "minor")]
    [InlineData(null, "major")]
    public void A_known_high_vulnerability_blocks(string? osv, string expected) => Assert.Equal(expected, Dependencies.SeverityFor(osv));

    [Fact]
    public void Ruff_and_bandit_output_become_findings_with_package_paths()
    {
        var ruff = """
            [{"code":"F821","filename":"/tmp/x/backend/app/service.py","location":{"row":12,"column":5},"message":"Undefined name `rate`"},
             {"code":"C901","filename":"/tmp/x/backend/app/calc.py","location":{"row":3,"column":1},"message":"`price` is too complex (18 > 15)"}]
            """;
        var r = PythonTools.ParseRuff(ruff, "/tmp/x");
        Assert.Equal(("RUFF-F821", "major", "backend/app/service.py", 12), (r[0].Rule, r[0].Severity, r[0].File, r[0].Line));
        Assert.Equal(("maintainability", "minor"), (r[1].Category, r[1].Severity));
        Assert.All(r, f => Assert.Equal("tool", f.Source));

        var bandit = """
            {"results":[{"test_id":"B602","issue_severity":"HIGH","issue_confidence":"HIGH","filename":"./backend/app/run.py","line_number":7,"issue_text":"subprocess call with shell=True identified, security issue."},
                        {"test_id":"B311","issue_severity":"LOW","issue_confidence":"HIGH","filename":"./backend/app/ids.py","line_number":2,"issue_text":"Standard pseudo-random generators are not suitable for security/cryptographic purposes."}]}
            """;
        var b = PythonTools.ParseBandit(bandit, "/tmp/x");
        Assert.Equal(("BANDIT-B602", "critical", "security", "backend/app/run.py", 7), (b[0].Rule, b[0].Severity, b[0].Category, b[0].File, b[0].Line));
        Assert.Equal("minor", b[1].Severity);
        Assert.Empty(PythonTools.ParseBandit("", "/tmp/x"));
    }
}
