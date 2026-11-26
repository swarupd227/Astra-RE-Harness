using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Validation.PhpEquivalence;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>PHP track P7 — the pure parts of PHP → Java equivalence: traits, vectors, Java lookup, verdicts.</summary>
public class PhpEquivalenceTests
{
    private static JsonElement Spec(string json) => JsonDocument.Parse(json).RootElement;
    private static readonly JsonElement Empty = Spec("{}");

    // ── traits ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_routine_that_queries_a_database_is_flagged_from_its_code()
    {
        var t = PhpRoutineTraits.Analyze("function get($id) { return mysqli_query($db, \"SELECT 1\"); }", Empty);
        Assert.True(t.TouchesDatabase);
        Assert.Contains("mysqli_query", t.DatabaseEvidence);
        Assert.True(PhpRoutineTraits.Analyze("function f() { $pdo = new PDO('x'); }", Empty).TouchesDatabase);
        Assert.True(PhpRoutineTraits.Analyze("function f() { return DB::select('x'); }", Empty).TouchesDatabase);
    }

    [Fact]
    public void A_data_access_claim_in_the_spec_marks_the_routine_as_needing_a_database()
    {
        var t = PhpRoutineTraits.Analyze("function f($x) { return $this->repo->load($x); }",
            Spec("""{"data_accesses":[{"id":"DA-1","description":"reads orders by id"}]}"""));
        Assert.True(t.TouchesDatabase);
        Assert.StartsWith("DA-1", t.DatabaseEvidence);
    }

    [Fact]
    public void Pure_code_and_commented_out_queries_are_not_database_work()
    {
        var t = PhpRoutineTraits.Analyze("function f($a) {\n  // mysqli_query($db, 'x');\n  /* new PDO() */\n  return $a * 2;\n}", Empty);
        Assert.False(t.TouchesDatabase);
        Assert.Empty(t.Unsuppliable);
    }

    [Fact]
    public void Superglobals_files_and_network_are_recognised()
    {
        var t = PhpRoutineTraits.Analyze("function f() { $q = $_POST['qty']; $_SESSION['x'] = 1; }", Empty);
        Assert.Equal(new[] { "_POST", "_SESSION" }, t.Superglobals);
        Assert.True(t.ReadsRequest);
        Assert.Contains(PhpRoutineTraits.Analyze("function f() { move_uploaded_file($_FILES['a']['tmp'], 'x'); }", Empty).Unsuppliable,
            u => u.Contains("$_FILES"));
        Assert.Contains(PhpRoutineTraits.Analyze("function f() { $c = curl_init('x'); }", Empty).Unsuppliable, u => u.Contains("curl_init"));
        Assert.NotEmpty(PhpRoutineTraits.Analyze("function f() { return file_get_contents('https://x'); }", Empty).Unsuppliable);
    }

    [Fact]
    public void Slice_takes_the_routine_lines()
    {
        Assert.Equal("b\nc", PhpRoutineTraits.Slice("a\nb\nc\nd", 2, 3));
        Assert.Equal("a\nb", PhpRoutineTraits.Slice("a\nb", 0, 0));
    }

    // ── vectors ─────────────────────────────────────────────────────────────

    [Fact]
    public void Generator_hint_examples_become_vectors_as_written()
    {
        var v = PhpEquivalenceVectors.Build(Spec("""
            {"edge_cases":[{"id":"EC-1","generatorHints":{"examples":[{"qty":"0"},{"inputs":{"qty":"5"}},{"qty":"abc","expected":1}]}}]}
            """));
        Assert.Equal(3, v.Count);
        Assert.All(v, x => Assert.Equal("EC-1", x.Source));
        Assert.Equal("""{"qty":"0"}""", v[0].Named.ToJsonString());
        Assert.Equal("""{"qty":"5"}""", v[1].Named.ToJsonString());
        Assert.Equal("""{"qty":"abc"}""", v[2].Named.ToJsonString());   // "expected" is not an input
        Assert.Equal(new[] { "V1", "V2", "V3" }, v.Select(x => x.Id));
    }

    [Fact]
    public void Generator_hint_inputs_vary_one_at_a_time_from_typical_values()
    {
        var v = PhpEquivalenceVectors.Build(Spec("""
            {"invariants":[{"id":"INV-1","generatorHints":{"inputs":[{"name":"price","type":"float","min":0,"max":100},{"name":"qty","type":"int","min":1,"max":9}]}}]}
            """));
        Assert.Equal("""{"price":50,"qty":5}""", v[0].Named.ToJsonString());
        Assert.Contains(v, x => x.Named.ToJsonString() == """{"price":0,"qty":5}""");
        Assert.Contains(v, x => x.Named.ToJsonString() == """{"price":50,"qty":9}""");
        Assert.True(v.Count <= 6);
    }

    [Fact]
    public void Http_contract_parameters_go_where_the_source_reads_them()
    {
        var v = PhpEquivalenceVectors.Build(Spec("""
            {"http_contracts":[{"id":"HTTP-1","method":"POST","path":"/cart","request":[
              {"name":"id","source":"path","type":"int","required":"yes"},
              {"name":"qty","source":"form","type":"int","required":"yes"},
              {"name":"note","source":"query","type":"string","required":"no"}]}]}
            """));
        var first = v[0];
        Assert.Equal("HTTP-1", first.Source);
        Assert.Equal("""{"id":"1"}""", first.Named.ToJsonString());               // path → the routine's own argument
        Assert.Equal("""{"post":{"qty":"1"}}""", first.Superglobals!.ToJsonString());
        Assert.Equal("""{"id":"1","qty":"1"}""", first.JavaNamed().ToJsonString()); // Java sees every request parameter
        Assert.Contains(v, x => x.Label == "every parameter" && x.Superglobals!["get"]!["note"]!.ToString() == "abc");
        Assert.Contains(v, x => x.Label == "without qty" && x.Superglobals!["post"] is null);
        Assert.Contains(v, x => x.Label == "qty = \"abc\"");
    }

    [Fact]
    public void Typed_spec_inputs_give_vectors_and_superglobal_inputs_are_lifted()
    {
        var v = PhpEquivalenceVectors.Build(Spec("""
            {"inputs":[{"name":"amount","type":"float","direction":"in"},{"name":"$_POST['coupon']","type":"string","direction":"superglobal"},{"name":"total","type":"float","direction":"return"}]}
            """));
        Assert.Equal("""{"amount":1.5}""", v[0].Named.ToJsonString());
        Assert.Equal("""{"post":{"coupon":"abc"}}""", v[0].Superglobals!.ToJsonString());
        Assert.DoesNotContain(v, x => x.Named.ContainsKey("total"));
    }

    [Fact]
    public void Engineer_vectors_come_first_and_duplicates_are_dropped()
    {
        var extra = new[] { new JsonObject { ["named"] = new JsonObject { ["qty"] = "0" }, ["label"] = "zero" } };
        var v = PhpEquivalenceVectors.Build(Spec("""{"edge_cases":[{"id":"EC-1","generatorHints":{"examples":[{"qty":"0"},{"qty":"2"}]}}]}"""), extra);
        Assert.Equal(2, v.Count);
        Assert.Equal("engineer", v[0].Source);
        Assert.Equal("zero", v[0].Label);
    }

    [Fact]
    public void A_spec_with_nothing_to_try_gives_no_vectors()
    {
        Assert.Empty(PhpEquivalenceVectors.Build(Spec("""{"invariants":[{"id":"INV-1","claim":"x"}]}""")));
    }

    // ── Java lookup ─────────────────────────────────────────────────────────

    private const string Resolver = """
        package com.acme.catalog;

        import java.util.Map;

        /** Java projection of PHP {@code qty.php}'s {@code resolveQty}. */
        @TargetMapping(value = "@Service", phpConstruct = "qty.php resolveQty")
        public final class QuantityResolver {
            @SpecClaim("NUL-1")
            public int resolveQty(Map<String, String> input) {
                if (input == null) { return 1; }
                return Integer.parseInt(input.get("qty"));
            }
            private static String helper(String s) { return s.trim(); }
        }
        """;

    private const string Controller = """
        package com.acme.web;

        @RestController
        public class CartController {
            // Converted from cart.php add_to_cart()
            @PostMapping("/cart")
            public ResponseEntity<CartDto> add(@RequestParam(name = "sku") String sku, @RequestParam(value = "qty", required = false) Integer qty) {
                if (sku == null) throw new IllegalArgumentException("x");
                return ResponseEntity.ok(new CartDto());
            }
        }
        """;

    [Fact]
    public void The_java_method_with_the_php_routines_name_is_found()
    {
        var t = JavaTargetLocator.Find(new[] { ("src/main/java/com/acme/catalog/QuantityResolver.java", Resolver) }, "resolveQty", null);
        Assert.NotNull(t);
        Assert.Equal("com.acme.catalog.QuantityResolver", t!.ClassName);
        Assert.Equal("resolveQty", t.MethodName);
        Assert.Equal(1, t.ParamCount);
    }

    [Fact]
    public void Snake_case_php_names_match_camel_case_java_and_annotations_can_name_the_routine()
    {
        Assert.Equal("addToCart", JavaTargetLocator.Camel("add_to_cart"));
        var t = JavaTargetLocator.Find(new[] { ("src/main/java/com/acme/web/CartController.java", Controller) }, "add_to_cart", null);
        Assert.NotNull(t);
        Assert.Equal("add", t!.MethodName);   // named differently, but its comment says which PHP routine it came from
        Assert.Equal(2, t.ParamCount);        // the commas inside @RequestParam(...) are not parameters
    }

    [Fact]
    public void Tests_interfaces_and_unrelated_methods_are_not_targets()
    {
        var files = new[]
        {
            ("src/test/java/com/acme/catalog/QuantityResolverTests.java", Resolver.Replace("class QuantityResolver", "class QuantityResolverTests")),
            ("src/main/java/com/acme/catalog/Port.java", "package x; public interface Port { int resolveQty(java.util.Map<String,String> m); }"),
            ("src/main/java/com/acme/catalog/Other.java", "package x; public class Other { public int total() { return 1; } }"),
        };
        Assert.Null(JavaTargetLocator.Find(files, "resolveQty", null));
    }

    [Fact]
    public void A_class_named_like_the_php_class_wins_a_tie()
    {
        var files = new[]
        {
            ("src/main/java/a/Invoice.java", "package a; public class Invoice { public int total(int x) { return x; } }"),
            ("src/main/java/a/Cart.java", "package a; public class Cart { public int total(int x) { return x; } }"),
        };
        Assert.Equal("a.Cart", JavaTargetLocator.Find(files, "total", "Shop\\Cart")!.ClassName);
    }

    // ── verdicts ────────────────────────────────────────────────────────────

    private static JsonObject O(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void Equal_values_pass_and_different_values_fail_with_the_path()
    {
        var pass = EquivalenceComparer.Compare(O("""{"ok":true,"returnValue":3,"responseCode":200}"""), O("""{"ok":true,"returnValue":3.0}"""));
        Assert.Equal("PASSED", pass.Status);
        var fail = EquivalenceComparer.Compare(O("""{"ok":true,"returnValue":1,"responseCode":200}"""), O("""{"ok":true,"returnValue":0}"""));
        Assert.Equal("FAILED", fail.Status);
        Assert.Equal("$: PHP 1 ≠ Java 0", fail.Diff);
        var deep = EquivalenceComparer.Compare(
            O("""{"ok":true,"returnValue":{"line_total":"10.50","items":[1,2]}}"""),
            O("""{"ok":true,"returnValue":{"__class":"Cart","lineTotal":10.5,"items":[1,3]}}"""));
        Assert.Equal("FAILED", deep.Status);
        Assert.Equal("$.items[1]: PHP 2 ≠ Java 3", deep.Diff);
        var typed = EquivalenceComparer.Compare(
            O("""{"ok":true,"returnValue":{"line_total":"10.50"}}"""),
            O("""{"ok":true,"returnValue":{"__class":"Cart","lineTotal":10.5}}"""));
        Assert.Equal("PASSED", typed.Status);
        Assert.Contains(typed.Notes, n => n.Contains("PHP gives a string, Java a number"));
    }

    [Fact]
    public void A_rejection_on_one_side_only_fails_and_on_both_passes()
    {
        var oneSided = EquivalenceComparer.Compare(O("""{"ok":true,"returnValue":1}"""),
            O("""{"ok":true,"exception":{"class":"IllegalArgumentException","message":"qty is not an integer"}}"""));
        Assert.Equal("FAILED", oneSided.Status);
        Assert.Contains("IllegalArgumentException", oneSided.Reason);
        var both = EquivalenceComparer.Compare(O("""{"ok":true,"exception":{"class":"InvalidArgumentException","message":"x"}}"""),
            O("""{"ok":true,"exception":{"class":"IllegalArgumentException","message":"y"}}"""));
        Assert.Equal("PASSED", both.Status);
    }

    [Fact]
    public void Http_status_is_compared_when_both_sides_have_one()
    {
        var php404 = O("""{"ok":true,"returnValue":null,"output":"","responseCode":404}""");
        Assert.Equal("PASSED", EquivalenceComparer.Compare(php404, O("""{"ok":true,"exception":{"class":"ResponseStatusException","message":"nf","status":404}}""")).Status);
        Assert.Equal("FAILED", EquivalenceComparer.Compare(php404, O("""{"ok":true,"returnValue":{"__http":true,"status":200,"body":null}}""")).Status);
        Assert.Equal("SKIPPED", EquivalenceComparer.Compare(php404, O("""{"ok":true,"returnValue":"x"}""")).Status);
        var echoed = O("""{"ok":true,"returnValue":null,"output":"{\"ok\":true}","responseCode":201}""");
        Assert.Equal("PASSED", EquivalenceComparer.Compare(echoed, O("""{"ok":true,"returnValue":{"__http":true,"status":201,"body":{"ok":true}}}""")).Status);
    }

    [Fact]
    public void Vectors_that_cannot_run_fairly_are_skipped_with_the_reason()
    {
        Assert.Equal("SKIPPED", EquivalenceComparer.Compare(O("""{"ok":false,"missingInput":"$cart"}"""), null).Status);
        Assert.Equal("SKIPPED", EquivalenceComparer.Compare(
            O("""{"ok":true,"exception":{"class":"TypeError","message":"resolve(): Argument #1 ($q) must be of type int, string given, called in x"}}"""),
            O("""{"ok":true,"returnValue":1}""")).Status);
        Assert.Equal("SKIPPED", EquivalenceComparer.Compare(
            O("""{"ok":true,"exception":{"class":"Error","message":"Call to undefined function Shop\\price_of()"}}"""), O("""{"ok":true}""")).Status);
        Assert.Equal("SKIPPED", EquivalenceComparer.Compare(O("""{"ok":true,"returnValue":1}"""),
            O("""{"ok":true,"exception":{"class":"NotSupplied","message":"m"},"unsupplied":["OrderRepository.findById"]}""")).Status);
        Assert.Equal("SKIPPED", EquivalenceComparer.Compare(O("""{"ok":true,"returnValue":1}"""), O("""{"ok":false,"inputError":"input \"abc\" for qty has no Java value"}""")).Status);
        Assert.Equal("ERRORED", EquivalenceComparer.Compare(O("""{"ok":false,"timedOut":true}"""), O("""{"ok":true}""")).Status);
        Assert.Equal("ERRORED", EquivalenceComparer.Compare(O("""{"ok":true,"returnValue":1}"""), null).Status);
    }

    [Fact]
    public void Php_quirks_that_are_not_differences()
    {
        Assert.Null(EquivalenceComparer.Diff(JsonNode.Parse("[]"), JsonNode.Parse("{}"), "$"));
        Assert.Null(EquivalenceComparer.Diff(JsonNode.Parse("""{"a":1,"b":null}"""), JsonNode.Parse("""{"a":1}"""), "$"));
        Assert.Null(EquivalenceComparer.Diff(JsonNode.Parse("\"active\""), JsonNode.Parse("\"ACTIVE\""), "$", new()));
        Assert.Null(EquivalenceComparer.Diff(JsonNode.Parse("0.30000000000000004"), JsonNode.Parse("0.3"), "$"));
        Assert.NotNull(EquivalenceComparer.Diff(JsonNode.Parse("\"abc\""), JsonNode.Parse("\"abd\""), "$"));
        Assert.NotNull(EquivalenceComparer.Diff(JsonNode.Parse("true"), JsonNode.Parse("1"), "$"));
    }

    [Fact]
    public void Echoed_text_is_compared_with_the_java_return_ignoring_whitespace_runs()
    {
        var v = EquivalenceComparer.Compare(O("""{"ok":true,"returnValue":null,"output":"<p>Hello  Ann</p>\n","responseCode":200}"""),
            O("""{"ok":true,"returnValue":"<p>Hello Ann</p>"}"""));
        Assert.Equal("PASSED", v.Status);
    }

    // ── harness plumbing ────────────────────────────────────────────────────

    [Fact]
    public void Harness_lines_are_read_from_the_maven_output()
    {
        var (results, fatal) = PhpEquivalenceRunner.ParseHarness(
            "[INFO] Running astra.equivalence.AstraEquivalenceHarnessTest\n" +
            "@@ASTRA-EQ@@ {\"method\":{\"name\":\"resolveQty\"}}\n" +
            "@@ASTRA-EQ@@ {\"id\":\"V1\",\"ok\":true,\"returnValue\":3}\n" +
            "noise @@ASTRA-EQ@@ {\"id\":\"V2\",\"ok\":true,\"exception\":{\"class\":\"X\"}}\n" +
            "@@ASTRA-EQ@@ not json\n");
        Assert.Null(fatal);
        Assert.Equal(new[] { "V1", "V2" }, results.Keys.OrderBy(k => k));
        Assert.Equal("class not found: a.B", PhpEquivalenceRunner.ParseHarness("@@ASTRA-EQ@@ {\"fatal\":\"class not found: a.B\"}").Fatal);
    }

    [Fact]
    public void A_pom_without_junit_gets_it_and_one_with_it_is_untouched()
    {
        const string bare = "<project><dependencies><dependency><artifactId>x</artifactId></dependency></dependencies></project>";
        Assert.Contains("junit-jupiter", PhpEquivalenceRunner.EnsureJUnit(bare));
        const string spring = "<project><dependencies><dependency><artifactId>spring-boot-starter-test</artifactId></dependency></dependencies></project>";
        Assert.Equal(spring, PhpEquivalenceRunner.EnsureJUnit(spring));
        Assert.Contains("<dependencies><dependency><groupId>org.junit.jupiter", PhpEquivalenceRunner.EnsureJUnit("<project></project>"));
    }

    [Fact]
    public void The_junit_harness_ships_inside_the_api()
    {
        var src = PhpEquivalenceRunner.HarnessSource();
        Assert.Contains("package astra.equivalence;", src);
        Assert.Contains("class AstraEquivalenceHarnessTest", src);
        Assert.Contains("@@ASTRA-EQ@@", src);
    }
}
