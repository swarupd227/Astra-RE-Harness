using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Api.Audit;
using Astra.Api.Auth;
using Astra.Api.Llm.Schemas;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Astra.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Validation;

/// <summary>
/// Generates a per-scaffold xUnit test file that maps one [Fact] to every
/// signed-spec claim — invariants, side-effects, edge-cases, and open
/// questions. Each test is named after its claim id, carries the claim
/// text as a Trait+comment, and contains a soft (always-passing) assertion
/// the engineer is expected to replace with a behavioural check when the
/// implementation lands. The point is twofold:
///
///   1. Coverage guarantee — every signed claim has a named test. No claim
///      can ship without a corresponding fixture in the test pack.
///   2. Contract-drift detection — once the engineer fills in real assertions,
///      a CI run that fails on INV-3 makes it obvious which signed claim
///      regressed, and the comment + Trait makes the audit chain explicit.
///
/// The generated file is added/overwritten in the scaffold's manifest under
/// <c>tests/&lt;SubroutineName&gt;_SignedSpecPack.cs</c>. Re-running the
/// generator after a spec re-sign is safe — the file is replaced atomically.
/// </summary>
public sealed class TestPackGenerator
{
    // Allowable C# identifier chars in claim ids / method names.
    private static readonly Regex Sanitize = new(@"[^A-Za-z0-9_]", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly IBlobClient _blob;
    private readonly StorageOptions _storage;
    private readonly IAuditLogger _audit;
    private readonly SpecSchemaProvider _schemas;
    private readonly ILogger<TestPackGenerator> _log;

    public TestPackGenerator(
        AppDbContext db,
        IBlobClient blob,
        StorageOptions storage,
        IAuditLogger audit,
        SpecSchemaProvider schemas,
        ILogger<TestPackGenerator> log)
    {
        _db = db;
        _blob = blob;
        _storage = storage;
        _audit = audit;
        _schemas = schemas;
        _log = log;
    }

    public sealed record GenerationResult(
        Guid ScaffoldId,
        string TestFilePath,
        int InvariantCount,
        int SideEffectCount,
        int EdgeCaseCount,
        int OpenQuestionCount,
        int TotalTests);

    public async Task<GenerationResult> GenerateAsync(
        Guid scaffoldId,
        DevPersonaContext? actor,
        CancellationToken ct)
    {
        var scaffold = await _db.Scaffolds
            .FirstOrDefaultAsync(s => s.Id == scaffoldId, ct)
            ?? throw new InvalidOperationException($"Scaffold {scaffoldId} not found.");

        var spec = await _db.Specs
            .Include(s => s.Subroutine)
            .FirstOrDefaultAsync(s => s.Id == scaffold.SpecId, ct)
            ?? throw new InvalidOperationException($"Spec {scaffold.SpecId} not found.");

        if (spec.State != "SIGNED")
            throw new InvalidOperationException(
                $"Spec {spec.Id} is in state {spec.State}; test pack generation requires SIGNED.");

        var subroutineName = spec.Subroutine?.Name ?? "Unknown";
        var className = SanitizeIdentifier(subroutineName) + "_SignedSpecPack";
        var testFilePath = $"tests/{className}.cs";

        // ── Walk the spec JSON via the externalised schema (Phase #3a) ──
        // The schema declares which kinds exist for this source language
        // and where in the JSON they live. Defaults to fortran-f77 today;
        // when a corpus-level schema id lands (#3b), we'd switch on that.
        var schema = _schemas.Default();
        var root = spec.SpecJson.RootElement;
        var claimsByKind = new Dictionary<string, List<Claim>>();
        foreach (var kind in schema.ClaimKinds)
        {
            // Accept both snake_case and camelCase variants for the array
            // name; the persisted JSON uses snake_case but the schema is
            // authored as it. This keeps re-syncs to older corpora robust.
            var camel = ToCamelCase(kind.SpecJsonField);
            var candidates = camel == kind.SpecJsonField
                ? new[] { kind.SpecJsonField }
                : new[] { kind.SpecJsonField, camel };
            claimsByKind[kind.Id] = ExtractClaimsAny(root, candidates, kind.IdPrefix, kind.TextField);
        }

        // ── Render the file ──────────────────────────────────────────────
        var content = RenderTestFile(className, subroutineName, spec.Id, schema, claimsByKind);

        // ── Update the scaffold manifest in MinIO ────────────────────────
        var allClaims = claimsByKind.SelectMany(kv => kv.Value).ToList();
        var manifestText = await _blob.GetTextAsync(scaffold.PackageBlobUri, ct);
        using var manifestDoc = JsonDocument.Parse(manifestText);
        var newManifest = ReplaceOrAppendFile(
            manifestDoc.RootElement, testFilePath, "csharp", content,
            claimIds: allClaims.Select(c => c.Id).ToArray());

        // Re-upload the manifest under the same key so the scaffold detail
        // endpoint returns the regenerated test file immediately.
        var manifestKey = ExtractObjectName(scaffold.PackageBlobUri);
        await _blob.PutTextAsync(
            _storage.Buckets.Scaffolds,
            manifestKey,
            newManifest,
            "application/json",
            ct);

        // ── Update scaffold counters ─────────────────────────────────────
        // Test pack adds N new test files but keeps the same .cs sources.
        // Recompute line counts + todo counts from the new manifest.
        using var newDoc = JsonDocument.Parse(newManifest);
        var files = newDoc.RootElement.GetProperty("files");
        int totalLines = 0, todoCount = 0;
        foreach (var f in files.EnumerateArray())
        {
            var c = f.GetProperty("content").GetString() ?? "";
            totalLines += c.Count(ch => ch == '\n');
            todoCount += CountTodos(c);
        }
        scaffold.FileCount = files.GetArrayLength();
        scaffold.TotalLines = totalLines;
        scaffold.TodoCount = todoCount;
        await _db.SaveChangesAsync(ct);

        var totalTests = allClaims.Count;
        // Surface per-kind counts in the audit payload + return result.
        // Schema-driven, so adding/removing kinds doesn't lift the audit.
        var perKindCounts = claimsByKind.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        await _audit.LogAsync(
            "test_pack.generated", "scaffold", scaffold.Id, actor,
            payload: new
            {
                specId = spec.Id,
                subroutine = subroutineName,
                schemaId = schema.Id,
                testFilePath,
                perKindCounts,
                totalTests,
            },
            ct: ct);

        _log.LogInformation(
            "Generated test pack for scaffold {Scaffold} ({Subroutine}, schema={Schema}): {Total} tests across {Kinds} kinds",
            scaffold.Id, subroutineName, schema.Id, totalTests, claimsByKind.Count);

        return new GenerationResult(
            scaffold.Id, testFilePath,
            // Map back to legacy named counts for backwards-compat with the
            // existing API surface. Unknown kinds fall through to 0.
            InvariantCount: perKindCounts.GetValueOrDefault("invariant", 0),
            SideEffectCount: perKindCounts.GetValueOrDefault("sideEffect", 0)
                              + perKindCounts.GetValueOrDefault("ioSideEffect", 0)
                              + perKindCounts.GetValueOrDefault("sectionContract", 0),
            EdgeCaseCount: perKindCounts.GetValueOrDefault("edgeCase", 0),
            OpenQuestionCount: perKindCounts.GetValueOrDefault("openQuestion", 0),
            TotalTests: totalTests);
    }

    // ────────────────────────────────────────────────────────────────────
    // Claim extraction
    // ────────────────────────────────────────────────────────────────────

    private sealed record Claim(string Id, string Kind, string Text, string? Confidence, string? Citation);

    private static List<Claim> ExtractClaimsAny(
        JsonElement root, string[] candidateArrayNames, string idPrefix, string textField)
    {
        foreach (var name in candidateArrayNames)
        {
            if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                return ExtractClaimsFromArray(arr, idPrefix, textField);
        }
        return new List<Claim>();
    }

    private static List<Claim> ExtractClaimsFromArray(JsonElement arr, string idPrefix, string textField)
    {
        var out_ = new List<Claim>();
        foreach (var c in arr.EnumerateArray())
        {
            var id = TryGetString(c, "id") ?? "";
            // Prefer the schema-declared text field. Fall back to any of the
            // historical names so re-running against older corpora still maps
            // the right text into the test pack comments.
            var text = TryGetString(c, textField)
                ?? TryGetString(c, "claim")
                ?? TryGetString(c, "description")
                ?? TryGetString(c, "question")
                ?? TryGetString(c, "claim_text")
                ?? "";
            var confidence = TryGetString(c, "confidence");
            var citation = TryGetCitation(c);
            out_.Add(new Claim(id, idPrefix, text, confidence, citation));
        }
        return out_;
    }

    private static string ToCamelCase(string snake)
    {
        if (string.IsNullOrEmpty(snake)) return snake;
        var parts = snake.Split('_');
        if (parts.Length == 1) return snake;
        var sb = new StringBuilder(parts[0]);
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            sb.Append(char.ToUpperInvariant(parts[i][0]));
            if (parts[i].Length > 1) sb.Append(parts[i][1..]);
        }
        return sb.ToString();
    }

    private static string? TryGetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? TryGetCitation(JsonElement el)
    {
        // citations is an array of { lines: "..." } objects (or sometimes
        // { line_range: "..." }, or even plain strings). We accept all three.
        if (!el.TryGetProperty("citations", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var parts = new List<string>();
        foreach (var c in arr.EnumerateArray())
        {
            string? lines = c.ValueKind switch
            {
                JsonValueKind.String => c.GetString(),
                JsonValueKind.Object => TryGetString(c, "lines") ?? TryGetString(c, "line_range"),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(lines)) parts.Add(lines);
        }
        return parts.Count == 0 ? null : "lines " + string.Join("; ", parts);
    }

    // ────────────────────────────────────────────────────────────────────
    // Test file rendering
    // ────────────────────────────────────────────────────────────────────

    private static string RenderTestFile(
        string className,
        string subroutineName,
        Guid specId,
        SpecSchema schema,
        Dictionary<string, List<Claim>> claimsByKind)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// =====================================================================");
        sb.AppendLine("// AUTO-GENERATED — Astra TestPackGenerator");
        sb.AppendLine($"// Subroutine: {subroutineName}");
        sb.AppendLine($"// Source spec id: {specId}");
        sb.AppendLine($"// Schema: {schema.Id} ({schema.DisplayName})");
        sb.AppendLine("//");
        sb.AppendLine("// One [Fact] per signed-spec claim. Soft assertions pass trivially —");
        sb.AppendLine("// the contract this file enforces is \"every signed claim has a named");
        sb.AppendLine("// test fixture\". Engineers replace each body with a behavioural");
        sb.AppendLine("// assertion as the implementation lands; the [Trait] attributes keep");
        sb.AppendLine("// the spec→test mapping queryable in CI test reports.");
        sb.AppendLine("//");
        sb.AppendLine("// Regenerate this file when the spec is re-signed.");
        sb.AppendLine("// =====================================================================");
        sb.AppendLine("using Xunit;");
        sb.AppendLine();
        sb.AppendLine("namespace Demo.RollStock.Tests;");
        sb.AppendLine();
        sb.AppendLine($"public class {className}");
        sb.AppendLine("{");

        // Iterate schema-declared kinds in order. Empty buckets are skipped.
        foreach (var kind in schema.ClaimKinds)
        {
            if (!claimsByKind.TryGetValue(kind.Id, out var claims) || claims.Count == 0) continue;
            EmitSection(sb, PluraliseLabel(kind.Label), claims);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string PluraliseLabel(string label)
    {
        if (string.IsNullOrEmpty(label)) return label;
        if (label.EndsWith("s")) return label;
        if (label.EndsWith("y")) return label[..^1] + "ies";
        return label + "s";
    }

    private static void EmitSection(StringBuilder sb, string heading, List<Claim> claims)
    {
        if (claims.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine($"    // ── {heading} ──");
        foreach (var c in claims)
        {
            var methodName = SanitizeIdentifier(c.Id) + "_" + ShortenForIdentifier(c.Text);
            var citationComment = c.Citation is null ? "" : $" — {c.Citation}";
            var assertMessage = EscapeForString($"Soft assertion for {c.Id}. Replace with a behaviour check.");

            sb.AppendLine();
            sb.AppendLine($"    [Fact]");
            sb.AppendLine($"    [Trait(\"ClaimId\", \"{c.Id}\")]");
            if (!string.IsNullOrWhiteSpace(c.Citation))
                sb.AppendLine($"    [Trait(\"Citation\", \"{EscapeForString(c.Citation!)}\")]");
            if (!string.IsNullOrWhiteSpace(c.Confidence))
                sb.AppendLine($"    [Trait(\"Confidence\", \"{c.Confidence}\")]");
            sb.AppendLine($"    public void {methodName}()");
            sb.AppendLine($"    {{");
            sb.AppendLine($"        // CLAIM ({c.Id}{citationComment}):");
            foreach (var line in WrapForComment(c.Text, 80))
                sb.AppendLine($"        // {line}");
            sb.AppendLine($"        Assert.True(true, \"{assertMessage}\");");
            sb.AppendLine($"    }}");
        }
    }

    private static IEnumerable<string> WrapForComment(string text, int width)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        var words = text.Split(' ');
        var line = new StringBuilder();
        foreach (var w in words)
        {
            if (line.Length > 0 && line.Length + w.Length + 1 > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(w);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    private static string SanitizeIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s)) return "_";
        var cleaned = Sanitize.Replace(s, "_");
        if (char.IsDigit(cleaned[0])) cleaned = "_" + cleaned;
        return cleaned;
    }

    private static string ShortenForIdentifier(string s, int maxLen = 60)
    {
        if (string.IsNullOrEmpty(s)) return "Claim";
        // Take the first sentence (up to . or ,), then word-shorten.
        var firstClause = s.Split(new[] { '.', ',', ';' }, 2)[0];
        var sanitised = SanitizeIdentifier(firstClause).Trim('_');
        if (sanitised.Length > maxLen) sanitised = sanitised.Substring(0, maxLen).TrimEnd('_');
        if (sanitised.Length == 0) sanitised = "Claim";
        return sanitised;
    }

    private static string EscapeForString(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");

    private static int CountTodos(string content) =>
        Regex.Matches(content, @"\bTODO\b").Count;

    // ────────────────────────────────────────────────────────────────────
    // Manifest manipulation
    // ────────────────────────────────────────────────────────────────────

    private static string ReplaceOrAppendFile(
        JsonElement manifestRoot,
        string path,
        string language,
        string content,
        string[] claimIds)
    {
        // Read existing fields. We preserve everything except the file with the
        // matching path, which is replaced (or appended if not present).
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            bool wroteFile = false;
            foreach (var prop in manifestRoot.EnumerateObject())
            {
                if (prop.Name == "files")
                {
                    writer.WritePropertyName("files");
                    writer.WriteStartArray();
                    foreach (var f in prop.Value.EnumerateArray())
                    {
                        var existingPath = f.TryGetProperty("path", out var p) ? p.GetString() : null;
                        if (existingPath == path)
                        {
                            WriteFile(writer, path, language, content, claimIds);
                            wroteFile = true;
                        }
                        else
                        {
                            f.WriteTo(writer);
                        }
                    }
                    if (!wroteFile)
                        WriteFile(writer, path, language, content, claimIds);
                    writer.WriteEndArray();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteFile(
        Utf8JsonWriter writer, string path, string language, string content, string[] claimIds)
    {
        writer.WriteStartObject();
        writer.WriteString("path", path);
        writer.WriteString("language", language);
        writer.WriteString("content", content);
        writer.WritePropertyName("derivedFromClaimIds");
        writer.WriteStartArray();
        foreach (var id in claimIds) writer.WriteStringValue(id);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string ExtractObjectName(string blobUri)
    {
        // minio://<bucket>/<objectName>
        const string prefix = "minio://";
        if (!blobUri.StartsWith(prefix))
            throw new InvalidOperationException($"Unexpected blob URI shape: {blobUri}");
        var rest = blobUri.Substring(prefix.Length);
        var slash = rest.IndexOf('/');
        if (slash < 0) throw new InvalidOperationException($"Missing object name in blob URI: {blobUri}");
        return rest.Substring(slash + 1);
    }
}
