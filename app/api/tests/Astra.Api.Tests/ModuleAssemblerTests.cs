using System.IO.Compression;
using Astra.Api.Assembly;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>Many per-unit Spring Boot packages → one Maven project: packages, shared classes, schema, properties, pom, beans.</summary>
public class ModuleAssemblerTests
{
    private const string Pom = """
        <?xml version="1.0" encoding="UTF-8"?>
        <project xmlns="http://maven.apache.org/POM/4.0.0">
            <modelVersion>4.0.0</modelVersion>
            <parent><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-parent</artifactId><version>{PARENT}</version><relativePath/></parent>
            <groupId>com.example</groupId>
            <artifactId>{ID}</artifactId>
            <version>0.1.0</version>
            <properties><java.version>{JAVA}</java.version></properties>
            <dependencies>
                <dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-jdbc</artifactId></dependency>
                <dependency><groupId>com.h2database</groupId><artifactId>h2</artifactId><scope>{H2SCOPE}</scope></dependency>
                {EXTRA}
            </dependencies>
            <build><plugins><plugin><groupId>org.apache.maven.plugins</groupId><artifactId>maven-surefire-plugin</artifactId><version>3.2.5</version></plugin></plugins></build>
        </project>
        """;

    private static string PomFor(string id, string parent = "3.2.5", string java = "21", string h2Scope = "runtime", string extra = "") =>
        Pom.Replace("{ID}", id).Replace("{PARENT}", parent).Replace("{JAVA}", java).Replace("{H2SCOPE}", h2Scope).Replace("{EXTRA}", extra);

    private const string SpecClaim = """
        package com.example.{P};

        import java.lang.annotation.*;

        @Retention(RetentionPolicy.SOURCE)
        public @interface SpecClaim { String value(); }
        """;

    private const string Societe = """
        package com.example.{P};

        /** A third party. */
        public record Societe(long rowid, String nom) {}
        """;

    private static UnitPackage Unit(string name, string pkg, Dictionary<string, string> files) =>
        new(Guid.NewGuid(), name, $"htdocs/{name}.php", Guid.NewGuid(), files.ToDictionary(f => f.Key.Replace("{P}", pkg), f => f.Value.Replace("{P}", pkg)));

    private static UnitPackage Rib() => Unit("get_all_rib", "societerib", new()
    {
        ["pom.xml"] = PomFor("societe-rib-api"),
        ["src/main/java/com/example/{P}/SocieteRibApplication.java"] = """
            package com.example.{P};

            import org.springframework.boot.SpringApplication;
            import org.springframework.boot.autoconfigure.SpringBootApplication;
            import org.springframework.context.annotation.Bean;

            @SpringBootApplication
            public class SocieteRibApplication {
                public static void main(String[] args) {
                    SpringApplication.run(SocieteRibApplication.class, args);
                }

                @Bean
                public String tablePrefix() { return "llx_"; }
            }
            """,
        ["src/main/java/com/example/{P}/SpecClaim.java"] = SpecClaim,
        ["src/main/java/com/example/{P}/Societe.java"] = Societe,
        ["src/main/java/com/example/{P}/SocieteRibService.java"] = """
            package com.example.{P};

            import org.springframework.stereotype.Service;

            @Service
            public class SocieteRibService {
                @SpecClaim("INV-1")
                public Societe owner(long id) { return new Societe(id, "x"); }
            }
            """,
        ["src/main/java/com/example/{P}/web/RibController.java"] = """
            package com.example.{P}.web;

            import com.example.{P}.SocieteRibService;
            import com.example.{P}.Societe;
            import org.springframework.web.bind.annotation.RestController;

            @RestController
            public class RibController {
                private final SocieteRibService service;
                public RibController(SocieteRibService service) { this.service = service; }
                public Societe get(long id) { return service.owner(id); }
            }
            """,
        ["src/main/java/com/example/{P}/ApiExceptionHandler.java"] = """
            package com.example.{P};

            import org.springframework.web.bind.annotation.RestControllerAdvice;

            @RestControllerAdvice
            public class ApiExceptionHandler {}
            """,
        ["src/test/java/com/example/{P}/SocieteRibServiceTests.java"] = """
            package com.example.{P};

            import org.springframework.boot.test.context.SpringBootTest;

            @SpringBootTest(classes = SocieteRibApplication.class)
            class SocieteRibServiceTests {
                Societe s = new Societe(1, "a");
            }
            """,
        ["src/main/resources/application.properties"] = "spring.application.name=societe-rib-api\nspring.sql.init.mode=embedded\napp.page-size=20\n",
        ["src/main/resources/schema.sql"] = """
            -- legacy table
            CREATE TABLE IF NOT EXISTS societe (
                rowid BIGINT PRIMARY KEY,
                nom   VARCHAR(200) NOT NULL
            );
            CREATE TABLE IF NOT EXISTS societe_rib (
                rowid  BIGINT AUTO_INCREMENT PRIMARY KEY,
                fk_soc BIGINT NOT NULL,
                type   VARCHAR(32) NOT NULL,
                FOREIGN KEY (fk_soc) REFERENCES societe(rowid)
            );
            """,
        ["README.md"] = "# rib\n",
    });

    private static UnitPackage Payment() => Unit("fetch", "companypaymentmode", new()
    {
        ["pom.xml"] = PomFor("company-payment-mode-api", parent: "3.2.4", java: "17", h2Scope: "test",
            extra: "<dependency><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-validation</artifactId></dependency>"),
        ["src/main/java/com/example/{P}/CompanyPaymentModeApplication.java"] = """
            package com.example.{P};

            import org.springframework.boot.SpringApplication;
            import org.springframework.boot.autoconfigure.SpringBootApplication;
            import org.springframework.context.annotation.Bean;

            @SpringBootApplication
            public class CompanyPaymentModeApplication {
                public static void main(String[] args) { SpringApplication.run(CompanyPaymentModeApplication.class, args); }

                @Bean
                public String tablePrefix() { return "llx_"; }
            }
            """,
        ["src/main/java/com/example/{P}/SpecClaim.java"] = SpecClaim,
        ["src/main/java/com/example/{P}/Societe.java"] = Societe,
        ["src/main/java/com/example/{P}/SocieteRibService.java"] = """
            package com.example.{P};

            import org.springframework.stereotype.Service;

            /** Same simple name as the other unit's service, different body: it must stay in this unit's package. */
            @Service
            public class SocieteRibService {
                public int count() { return 0; }
            }
            """,
        ["src/test/java/com/example/{P}/PaymentTests.java"] = """
            package com.example.{P};

            class PaymentTests {
                SocieteRibService s = new SocieteRibService();
                Societe so = new Societe(2, "b");
            }
            """,
        ["src/main/resources/application.properties"] = "spring.application.name=company-payment-mode-api\nspring.sql.init.mode=embedded\napp.page-size=50\n",
        ["src/main/resources/schema.sql"] = """
            CREATE TABLE IF NOT EXISTS societe_rib (
                rowid       BIGINT AUTO_INCREMENT PRIMARY KEY,
                fk_soc      BIGINT NOT NULL,
                type        VARCHAR(64),
                default_rib INT DEFAULT 0
            );
            INSERT INTO societe_rib (fk_soc, type) VALUES (1, 'ban');
            """,
        ["README.md"] = "# payment\n",
    });

    private static AssembledModule Build() =>
        new JavaSpringAssembler().Assemble(new[] { Rib(), Payment() }, new AssemblyOptions("Third parties"));

    [Fact]
    public void Each_unit_moves_into_its_own_package_under_one_application()
    {
        var m = Build();
        Assert.Equal("third-parties-service", m.Report.ArtifactId);
        Assert.Equal("com.astra.third_parties", m.Report.BasePackage);
        Assert.Equal("com.astra.third_parties.ThirdPartiesApplication", m.Report.ApplicationClass);

        var app = m.Files["src/main/java/com/astra/third_parties/ThirdPartiesApplication.java"];
        Assert.Contains("@SpringBootApplication(nameGenerator = FullyQualifiedAnnotationBeanNameGenerator.class)", app);

        var controller = m.Files["src/main/java/com/astra/third_parties/societerib/web/RibController.java"];
        Assert.StartsWith("package com.astra.third_parties.societerib.web;", controller);
        Assert.Contains("import com.astra.third_parties.societerib.SocieteRibService;", controller);
        Assert.Contains("import com.astra.third_parties.common.Societe;", controller);       // the moved class, re-pointed
        Assert.DoesNotContain("com.example", string.Concat(m.Files.Where(f => f.Key.EndsWith(".java")).Select(f => f.Value)));

        Assert.Equal(new[] { "com.astra.third_parties.societerib", "com.astra.third_parties.companypaymentmode" },
            m.Report.Units.Select(u => u.Package));
    }

    [Fact]
    public void Identical_classes_are_held_once_and_differing_ones_stay_per_unit()
    {
        var m = Build();
        Assert.True(m.Files.ContainsKey("src/main/java/com/astra/third_parties/common/SpecClaim.java"));
        Assert.True(m.Files.ContainsKey("src/main/java/com/astra/third_parties/common/Societe.java"));
        Assert.False(m.Files.ContainsKey("src/main/java/com/astra/third_parties/societerib/Societe.java"));
        Assert.StartsWith("package com.astra.third_parties.common;", m.Files["src/main/java/com/astra/third_parties/common/Societe.java"]);
        Assert.Contains(m.Report.Shared, s => s.Kind == "class" && s.Name == "Societe" && s.Units.Count == 2);

        // Different bodies: both kept, reported.
        Assert.True(m.Files.ContainsKey("src/main/java/com/astra/third_parties/societerib/SocieteRibService.java"));
        Assert.True(m.Files.ContainsKey("src/main/java/com/astra/third_parties/companypaymentmode/SocieteRibService.java"));
        Assert.Contains(m.Report.Conflicts, c => c.Kind == "class" && c.Name == "SocieteRibService");

        // Root-package users of a moved class import it; the test does too.
        var service = m.Files["src/main/java/com/astra/third_parties/societerib/SocieteRibService.java"];
        Assert.Contains("import com.astra.third_parties.common.Societe;", service);
        Assert.Contains("import com.astra.third_parties.common.SpecClaim;", service);
        Assert.Contains("import com.astra.third_parties.common.Societe;", m.Files["src/test/java/com/astra/third_parties/companypaymentmode/PaymentTests.java"]);
    }

    [Fact]
    public void Unit_applications_become_configurations_and_tests_point_at_their_own_unit()
    {
        var m = Build();
        var cfg = m.Files["src/main/java/com/astra/third_parties/societerib/SocieteRibApplication.java"];
        Assert.Contains("@org.springframework.context.annotation.Configuration(proxyBeanMethods = false)", cfg);
        Assert.DoesNotContain("@SpringBootApplication", cfg);
        Assert.DoesNotContain("static void main", cfg);
        Assert.Contains("public String tablePrefix()", cfg);                                      // its beans survive

        var test = m.Files["src/test/java/com/astra/third_parties/societerib/SocieteRibServiceTests.java"];
        // Same package as the unit's test application, so no import is needed; the module application is not booted.
        Assert.Contains("@SpringBootTest(classes = UnitTestApplication.class)", test);
        Assert.DoesNotContain("ThirdPartiesApplication", test);
        Assert.True(m.Files.ContainsKey("src/test/java/com/astra/third_parties/societerib/UnitTestApplication.java"));

        // Two units declaring the same @Bean method: reported, and overriding switched on so the context starts.
        Assert.Contains(m.Report.Conflicts, c => c.Kind == "bean" && c.Name == "tablePrefix");
        Assert.Contains("spring.main.allow-bean-definition-overriding=true", m.Files["src/main/resources/application.properties"]);

        // Each unit's controller advice only advises its own controllers.
        Assert.Contains("@RestControllerAdvice(basePackages = \"com.astra.third_parties.societerib\")",
            m.Files["src/main/java/com/astra/third_parties/societerib/ApiExceptionHandler.java"]);
    }

    [Fact]
    public void Schema_tables_merge_their_columns_and_relax_not_null_that_only_one_unit_declares()
    {
        var m = Build();
        var schema = m.Files["src/main/resources/schema.sql"];
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(schema, "CREATE TABLE IF NOT EXISTS societe_rib"));
        Assert.Contains("default_rib INT DEFAULT 0", schema);                                    // added by the second unit
        Assert.Contains("fk_soc BIGINT NOT NULL", schema);                                       // both say NOT NULL
        Assert.Contains("type VARCHAR(32),", schema.Replace("\r", ""));                          // one said NOT NULL → nullable
        Assert.Contains(m.Report.Conflicts, c => c.Kind == "column" && c.Name == "societe_rib.type");
        Assert.True(schema.IndexOf("TABLE IF NOT EXISTS societe (", StringComparison.Ordinal) < schema.IndexOf("societe_rib (", StringComparison.Ordinal));
        Assert.Contains("INSERT INTO societe_rib (fk_soc, type) VALUES (1, 'ban');", schema);
    }

    [Fact]
    public void A_table_keeps_one_primary_key_and_one_copy_of_each_constraint()
    {
        // Two units keyed the same link table differently (seen on a real estate: H2 refused the merged table with
        // "Attempt to define a second primary key", and every test whose context needed the schema failed).
        var a = "CREATE TABLE IF NOT EXISTS links (\n rowid BIGINT AUTO_INCREMENT PRIMARY KEY,\n fk_soc BIGINT NOT NULL,\n fk_user BIGINT NOT NULL,\n FOREIGN KEY (fk_soc) REFERENCES soc (rowid)\n);";
        var b = "CREATE TABLE IF NOT EXISTS links (\n fk_soc BIGINT NOT NULL,\n fk_user BIGINT NOT NULL,\n PRIMARY KEY (fk_soc, fk_user),\n FOREIGN KEY (fk_soc) REFERENCES soc(rowid),\n UNIQUE (fk_soc, fk_user)\n);";
        var c = "CREATE TABLE IF NOT EXISTS other (\n id BIGINT NOT NULL,\n PRIMARY KEY (id)\n);\nCREATE TABLE IF NOT EXISTS other (\n id BIGINT PRIMARY KEY\n);";
        var conflicts = new List<AssemblyConflict>();
        var schema = SchemaMerger.Merge(new[] { ("a", a), ("b", b), ("c", c) }, conflicts, new List<SharedItem>(), new List<string>());

        var links = System.Text.RegularExpressions.Regex.Match(schema, @"CREATE TABLE IF NOT EXISTS links \((.*?)\);", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(links, "PRIMARY KEY"));
        Assert.Contains("rowid BIGINT AUTO_INCREMENT PRIMARY KEY", links);                         // the first unit's key wins
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(links, "FOREIGN KEY"));          // spacing is not a difference
        Assert.DoesNotContain("UNIQUE", links);                                                   // only b declares it: left out, so a's rows still fit
        Assert.Contains(conflicts, x => x.Kind == "primary-key" && x.Name == "links");

        var other = System.Text.RegularExpressions.Regex.Match(schema, @"CREATE TABLE IF NOT EXISTS other \((.*?)\);", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(other, "PRIMARY KEY"));          // same key, two spellings
        Assert.DoesNotContain(conflicts, x => x.Kind == "primary-key" && x.Name == "other");
    }

    [Fact]
    public void A_column_stays_required_only_when_every_unit_requires_it_however_often_one_unit_declares_the_table()
    {
        // Seen on a real estate: one unit declared users twice (name NOT NULL), another had no name column at all and
        // its test inserts users without one. Counting statements kept NOT NULL; counting units relaxes it.
        var twice = "CREATE TABLE IF NOT EXISTS users (\n id BIGINT PRIMARY KEY,\n name VARCHAR(200) NOT NULL,\n login VARCHAR(100) NOT NULL\n);\nCREATE TABLE IF NOT EXISTS users (\n id BIGINT PRIMARY KEY,\n name VARCHAR(200) NOT NULL,\n login VARCHAR(100) NOT NULL\n);";
        var without = "CREATE TABLE IF NOT EXISTS users (\n id BIGINT PRIMARY KEY,\n login VARCHAR(100) NOT NULL,\n rights VARCHAR(2000)\n);";
        var schema = SchemaMerger.Merge(new[] { ("a", twice), ("b", without) }, new List<AssemblyConflict>(), new List<SharedItem>(), new List<string>());
        Assert.Contains("name VARCHAR(200),", schema.Replace("\r", ""));                          // b never fills it in
        Assert.Contains("login VARCHAR(100) NOT NULL", schema);                                   // both require it
    }

    [Fact]
    public void A_foreign_key_stays_only_when_every_unit_that_declares_the_table_has_it()
    {
        // Seen on a real estate: one unit's llx_societe_rib referenced llx_societe; another unit's had no key and its
        // tests insert rib rows for third parties they never create — they passed alone, and failed merged.
        var keyed = "CREATE TABLE IF NOT EXISTS soc (\n rowid BIGINT PRIMARY KEY\n);\nCREATE TABLE IF NOT EXISTS rib (\n rowid BIGINT PRIMARY KEY,\n fk_soc BIGINT NOT NULL REFERENCES soc (rowid) ON DELETE CASCADE,\n fk_user BIGINT,\n FOREIGN KEY (fk_user) REFERENCES soc (rowid)\n);";
        var loose = "CREATE TABLE IF NOT EXISTS rib (\n rowid BIGINT PRIMARY KEY,\n fk_soc BIGINT NOT NULL,\n fk_user BIGINT\n);";
        var both = "CREATE TABLE IF NOT EXISTS rib2 (\n id BIGINT PRIMARY KEY,\n fk_soc BIGINT REFERENCES soc(rowid)\n);";
        var notes = new List<string>();
        var schema = SchemaMerger.Merge(new[] { ("a", keyed + "\n" + both), ("b", loose + "\n" + both) }, new List<AssemblyConflict>(), new List<SharedItem>(), notes);

        var rib = System.Text.RegularExpressions.Regex.Match(schema, @"CREATE TABLE IF NOT EXISTS rib \((.*?)\);", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        Assert.DoesNotContain("REFERENCES", rib);                                                 // inline and clause forms both left out
        Assert.Contains("fk_soc BIGINT NOT NULL", rib);                                           // the column itself is kept, still required
        Assert.Contains(notes, n => n.Contains("rib:") && n.Contains("REFERENCES"));
        var rib2 = System.Text.RegularExpressions.Regex.Match(schema, @"CREATE TABLE IF NOT EXISTS rib2 \((.*?)\);", System.Text.RegularExpressions.RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("REFERENCES soc(rowid)", rib2);                                           // both units declare it: kept
    }

    [Fact]
    public void Columns_named_like_h2_keywords_are_found_so_the_database_can_accept_them()
    {
        var schema = "CREATE TABLE IF NOT EXISTS llx_const (\n rowid BIGINT AUTO_INCREMENT PRIMARY KEY,\n name VARCHAR(180) NOT NULL UNIQUE,\n value TEXT,\n \"key\" VARCHAR(10),\n year INT,\n PRIMARY KEY (rowid)\n);\nINSERT INTO llx_const (name, value) VALUES ('a', 'b');";
        Assert.Equal(new[] { "VALUE", "YEAR" }, H2Keywords.UsedAsNames(schema));   // quoted "key" is already safe; PRIMARY is a clause

        var merged = SchemaMerger.Merge(new[] { ("a", schema) }, new List<AssemblyConflict>(), new List<SharedItem>(), new List<string>());
        Assert.Contains("\"VALUE\" TEXT", merged);                                                // written so H2 never reads it as the keyword
        Assert.Contains("\"YEAR\" INT", merged);
        Assert.Contains("name VARCHAR(180)", merged);                                             // ordinary names untouched
    }

    [Fact]
    public void Each_unit_gets_a_test_application_that_starts_only_that_unit()
    {
        var m = Build();
        var testApps = m.Files.Where(f => f.Key.StartsWith("src/test/java/", StringComparison.Ordinal) && f.Key.EndsWith("/" + JavaSpringAssembler.UnitTestAppName + ".java", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(testApps);
        foreach (var (path, text) in testApps)
        {
            var pkg = System.Text.RegularExpressions.Regex.Match(text, @"^package ([\w.]+);", System.Text.RegularExpressions.RegexOptions.Multiline).Groups[1].Value;
            Assert.Contains($"scanBasePackages = {{ \"{pkg}\"", text);                          // its own package first, nothing of the other units
            Assert.Contains("FullyQualifiedAnnotationBeanNameGenerator", text);
        }
        // No test still boots the whole module.
        var app = m.Files.Single(f => f.Key.StartsWith("src/main/java/", StringComparison.Ordinal) && f.Value.Contains("@SpringBootApplication(")).Key;
        var appClass = Path.GetFileNameWithoutExtension(app);
        Assert.DoesNotContain(m.Files.Where(f => f.Key.StartsWith("src/test/java/", StringComparison.Ordinal)), f => f.Value.Contains(appClass + ".class"));
    }

    [Fact]
    public void Properties_and_pom_are_merged_and_disagreements_reported()
    {
        var m = Build();
        var props = m.Files["src/main/resources/application.properties"];
        Assert.Contains("spring.application.name=third-parties-service", props);
        Assert.Contains("app.page-size=20", props);
        Assert.DoesNotContain("app.page-size=50", props);
        Assert.Contains(m.Report.Conflicts, c => c.Kind == "property" && c.Name == "app.page-size");

        var pom = m.Files["pom.xml"];
        Assert.Contains("<artifactId>third-parties-service</artifactId>", pom);
        Assert.Contains("<version>3.2.5</version>", pom);                                         // newest parent
        Assert.Contains("<java.version>21</java.version>", pom);                                 // highest java
        Assert.Contains("spring-boot-starter-validation", pom);                                  // union
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(pom, "<artifactId>h2</artifactId>"));
        Assert.Contains("<scope>runtime</scope>", pom);                                          // widest scope wins over test
        Assert.Contains(m.Report.Conflicts, c => c.Kind == "parent");
        Assert.True(m.Files.ContainsKey("units/societerib/README.md"));
        Assert.Contains("# Third parties", m.Files["ASSEMBLY.md"]);
    }

    [Fact]
    public void The_same_packages_give_the_same_project()
    {
        var rib = Rib();
        var pay = Payment();
        var a = new JavaSpringAssembler().Assemble(new[] { rib, pay }, new AssemblyOptions("Third parties"));
        var b = new JavaSpringAssembler().Assemble(new[] { rib, pay }, new AssemblyOptions("Third parties"));
        Assert.Equal(a.Files.Keys, b.Files.Keys);
        Assert.All(a.Files, f => Assert.Equal(f.Value, b.Files[f.Key]));
    }

    [Theory]
    [InlineData("Assemble the societe module into one application", "assemble_module", "societe")]
    [InlineData("assemble `htdocs/societe/` with passing tests", "assemble_module", "htdocs/societe/")]
    [InlineData("Build one application for wave 2", "assemble_module", null)]
    [InlineData("How did the assembly go?", "get_assembly", null)]
    public void The_mock_brain_routes_assembly_requests(string text, string tool, string? path)
    {
        var (name, input) = Astra.Api.Copilot.MockCopilotBrain.RouteOrNull(text)!.Value;
        Assert.Equal(tool, name);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(input);
        if (tool == "assemble_module")
            Assert.Equal(path, json.GetProperty("pathPrefix").ValueKind == System.Text.Json.JsonValueKind.Null ? null : json.GetProperty("pathPrefix").GetString());
    }

    [Fact]
    public void Only_java_spring_has_an_assembler_so_far()
    {
        Assert.NotNull(ModuleAssemblers.For("java-spring"));
        Assert.Null(ModuleAssemblers.For("dotnet10"));
        Assert.Equal("societe-third-parties", ModuleAssemblers.Slug("  Société: third parties! "));
    }

    /// <summary>
    /// Local check against real packages: set ASSEMBLY_SAMPLES to a folder of package zips and ASSEMBLY_OUT to an empty
    /// folder; the assembled project is written there for <c>mvn test</c>. Skipped (passes) when unset.
    /// </summary>
    [Fact]
    public void Real_packages_assemble_when_samples_are_given()
    {
        var samples = Environment.GetEnvironmentVariable("ASSEMBLY_SAMPLES");
        var outDir = Environment.GetEnvironmentVariable("ASSEMBLY_OUT");
        if (string.IsNullOrEmpty(samples) || string.IsNullOrEmpty(outDir) || !Directory.Exists(samples)) return;
        var units = Directory.GetFiles(samples, "*.zip").OrderBy(p => p, StringComparer.Ordinal).Select(zip =>
        {
            using var z = ZipFile.OpenRead(zip);
            var files = z.Entries.Where(e => e.Length > 0).ToDictionary(e => e.FullName, e => new StreamReader(e.Open()).ReadToEnd());
            return new UnitPackage(Guid.NewGuid(), Path.GetFileNameWithoutExtension(zip), zip, Guid.NewGuid(), files);
        }).ToList();
        var m = new JavaSpringAssembler().Assemble(units, new AssemblyOptions("Sample module"));
        foreach (var (path, text) in m.Files)
        {
            var abs = Path.Combine(outDir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, text);
        }
        Assert.Equal(units.Count, m.Report.Units.Count);
    }
}
