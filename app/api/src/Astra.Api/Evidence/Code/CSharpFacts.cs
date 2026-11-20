using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Astra.Api.Evidence;

/// <summary>
/// Reads the facts of a project's C# files: what each class reads from configuration, which web endpoints
/// and message queues it talks to, which procedures and tables its SQL names. It parses (syntax only — no
/// compilation, nothing is run) every file twice: once for what only the whole project knows (which
/// settings section an options class is bound to, which base address a registered HTTP client gets), then
/// each file against that. Pure: text in, facts out. A file it cannot read yields no facts and a sentence
/// saying why; one bad file never stops the rest.
/// </summary>
internal static class CSharpFacts
{
    public const int MaxFileChars = 1_000_000;
    public const long MaxTotalChars = 40_000_000;

    public static bool IsCSharp(string path)
    {
        var ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) || ext.Equals(".csx", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyDictionary<string, CodeFileFacts> Read(IReadOnlyList<CodeSource> files, CancellationToken ct = default)
    {
        var result = new Dictionary<string, CodeFileFacts>(StringComparer.Ordinal);
        var readable = new List<CodeSource>();
        long total = 0;
        foreach (var f in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (f.Text.Length > MaxFileChars)
            {
                result[f.Path] = Nothing($"It is longer than {MaxFileChars:N0} characters, so it was not read for facts.");
                continue;
            }
            if (total + f.Text.Length > MaxTotalChars)
            {
                result[f.Path] = Nothing($"The project's C# is longer than {MaxTotalChars:N0} characters in all; this file was not read for facts.");
                continue;
            }
            total += f.Text.Length;
            readable.Add(f);
        }

        // Pass 1: what only the whole project knows.
        var registry = new CodeRegistry();
        foreach (var f in readable)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var root = Parse(f, out _, ct);
                var walker = new RegistryWalker(registry);
                walker.Visit(root);
                walker.Finish();
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // Reported in the second pass, which fails the same way.
            }
        }

        // Pass 2: each file, against the registry.
        foreach (var f in readable)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var root = Parse(f, out var errors, ct);
                var walker = new FactWalker(f.Path, registry);
                var facts = walker.Read(root);
                var notes = new List<string>(walker.Notes);
                if (errors > 0)
                    notes.Add($"It has {errors} syntax error{(errors == 1 ? "" : "s")}; what could be read was read.");
                result[f.Path] = new CodeFileFacts(facts, notes);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                result[f.Path] = Nothing($"It could not be read for facts ({ex.GetType().Name}).");
            }
        }
        return result;
    }

    private static CodeFileFacts Nothing(string note) => new(FactSet.Empty, new[] { note });

    private static CompilationUnitSyntax Parse(CodeSource file, out int errors, CancellationToken ct)
    {
        var script = file.Path.EndsWith(".csx", StringComparison.OrdinalIgnoreCase);
        var options = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.None, script ? SourceCodeKind.Script : SourceCodeKind.Regular);
        var tree = CSharpSyntaxTree.ParseText(file.Text, options, file.Path, cancellationToken: ct);
        errors = tree.GetDiagnostics(ct).Count(d => d.Severity == DiagnosticSeverity.Error);
        return (CompilationUnitSyntax)tree.GetRoot(ct);
    }
}
