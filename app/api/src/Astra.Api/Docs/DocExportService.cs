using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Api.Persistence;
using Astra.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Astra.Api.Docs;

/// <summary>
/// Phase 11.0.g — Documentation export.
///
/// Formats:
///   mkdocs     — MkDocs-Material project as a zip archive (in-memory, no deps)
///   pdf        — Combined markdown → PDF via Pandoc + weasyprint
///   docx       — Combined markdown → DOCX via Pandoc
///   confluence — Confluence REST API JSON payload (page body in Storage Format)
///
/// Only SIGNED sections are included. STALE sections are included with a
/// "⚠ This section may be out of date" warning banner.
/// </summary>
public sealed class DocExportService
{
    private readonly AppDbContext _db;
    private readonly ILogger<DocExportService> _logger;

    // Section ordering for combined output
    private static readonly string[] SectionOrder =
    [
        "overview", "module", "routine-summary",
        "data-dictionary", "glossary", "interface", "business-rules",
        "sequence-diagram", "dependency-diagram",
    ];

    public DocExportService(AppDbContext db, ILogger<DocExportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public sealed record ExportResult(string FileName, string ContentType, byte[] Bytes);

    public async Task<ExportResult> ExportAsync(Guid corpusId, string format, CancellationToken ct)
    {
        var corpus = await _db.Corpora.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == corpusId, ct)
            ?? throw new InvalidOperationException($"Corpus {corpusId} not found.");

        var sections = await _db.DocSections.AsNoTracking()
            .Where(s => s.CorpusId == corpusId && (s.State == "SIGNED" || s.State == "STALE"))
            .Include(s => s.Subroutine)
            .ToListAsync(ct);

        return format.ToLowerInvariant() switch
        {
            "mkdocs" => BuildMkDocsZip(corpus, sections),
            "pdf" => await RunPandocAsync(
                CombineMarkdown(corpus, sections),
                outputExt: "pdf",
                pandocArgs: "--pdf-engine=weasyprint",
                contentType: "application/pdf",
                fileName: $"{Slug(corpus.Name)}-docs.pdf",
                ct),
            "docx" => await RunPandocAsync(
                CombineMarkdown(corpus, sections),
                outputExt: "docx",
                pandocArgs: "--reference-doc=/dev/null",
                contentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                fileName: $"{Slug(corpus.Name)}-docs.docx",
                ct),
            "confluence" => BuildConfluenceJson(corpus, sections),
            _ => throw new ArgumentException(
                $"Unknown format '{format}'. Valid: mkdocs, pdf, docx, confluence"),
        };
    }

    // ── MkDocs zip ──────────────────────────────────────────────────────

    private static ExportResult BuildMkDocsZip(Corpus corpus, IReadOnlyList<DocSection> sections)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var nav = new StringBuilder();
            nav.AppendLine("nav:");

            // Overview → docs/index.md
            var overview = sections.FirstOrDefault(s => s.SectionKind == "overview");
            WriteZipEntry(zip, "docs/index.md",
                MarkdownWithStaleWarning(overview?.RenderedMarkdown ?? $"# {corpus.Name}\n\n*(No overview generated yet.)*", overview?.State));
            nav.AppendLine("  - Home: index.md");

            // Modules → docs/modules/{slug}.md
            var modules = sections.Where(s => s.SectionKind == "module").ToList();
            if (modules.Count > 0)
            {
                nav.AppendLine("  - Modules:");
                foreach (var m in modules.OrderBy(s => s.ModuleName))
                {
                    var slug = Slug(m.ModuleName ?? m.Id.ToString());
                    var path = $"docs/modules/{slug}.md";
                    WriteZipEntry(zip, path, MarkdownWithStaleWarning(m.RenderedMarkdown, m.State));
                    nav.AppendLine($"    - '{m.ModuleName ?? slug}': modules/{slug}.md");
                }
            }

            // Routines → docs/routines/{slug}.md
            var routines = sections.Where(s => s.SectionKind == "routine-summary").ToList();
            if (routines.Count > 0)
            {
                nav.AppendLine("  - Routines:");
                foreach (var r in routines.OrderBy(s => s.Subroutine?.Name ?? s.Id.ToString()))
                {
                    var name = r.Subroutine?.Name ?? r.Id.ToString("N")[..8];
                    var slug = Slug(name);
                    var path = $"docs/routines/{slug}.md";
                    WriteZipEntry(zip, path, MarkdownWithStaleWarning(r.RenderedMarkdown, r.State));
                    nav.AppendLine($"    - '{name}': routines/{slug}.md");
                }
            }

            // Reference catalog sections
            var refSections = new (string Kind, string Label, string File)[]
            {
                ("data-dictionary", "Data Dictionary", "reference/data-dictionary.md"),
                ("glossary",        "Glossary",        "reference/glossary.md"),
                ("interface",       "Interfaces",      "reference/interfaces.md"),
                ("business-rules",  "Business Rules",  "reference/business-rules.md"),
            };

            var anyRef = false;
            foreach (var (kind, label, file) in refSections)
            {
                var s = sections.FirstOrDefault(x => x.SectionKind == kind);
                if (s is null) continue;
                WriteZipEntry(zip, $"docs/{file}", MarkdownWithStaleWarning(s.RenderedMarkdown, s.State));
                if (!anyRef) { nav.AppendLine("  - Reference:"); anyRef = true; }
                nav.AppendLine($"    - '{label}': {file}");
            }

            // Diagrams → docs/diagrams/{slug}.md
            var diagrams = sections.Where(s =>
                s.SectionKind == "sequence-diagram" || s.SectionKind == "dependency-diagram").ToList();
            if (diagrams.Count > 0)
            {
                nav.AppendLine("  - Diagrams:");
                foreach (var d in diagrams.OrderBy(s => s.SectionKind).ThenBy(s => s.Subroutine?.Name))
                {
                    var name = d.Subroutine?.Name is string n
                        ? $"{n}-{(d.SectionKind == "sequence-diagram" ? "seq" : "dep")}"
                        : d.Id.ToString("N")[..8];
                    var slug = Slug(name);
                    var path = $"docs/diagrams/{slug}.md";
                    WriteZipEntry(zip, path, MarkdownWithStaleWarning(d.RenderedMarkdown, d.State));
                    nav.AppendLine($"    - '{name}': diagrams/{slug}.md");
                }
            }

            // mkdocs.yml
            WriteZipEntry(zip, "mkdocs.yml", BuildMkDocsYml(corpus, nav.ToString()));
        }

        return new ExportResult(
            $"{Slug(corpus.Name)}-docs.zip",
            "application/zip",
            ms.ToArray());
    }

    private static string BuildMkDocsYml(Corpus corpus, string navBlock)
    {
        return $"""
site_name: "{corpus.Name} — Transition Documentation"
site_description: "Auto-generated by Astra RE Harness on {DateTimeOffset.UtcNow:yyyy-MM-dd}"
theme:
  name: material
  palette:
    - scheme: default
      primary: indigo
      accent: indigo
      toggle:
        icon: material/brightness-7
        name: Switch to dark mode
    - scheme: slate
      primary: indigo
      accent: indigo
      toggle:
        icon: material/brightness-4
        name: Switch to light mode
  features:
    - navigation.tabs
    - navigation.sections
    - navigation.top
    - toc.integrate
    - content.code.annotate
markdown_extensions:
  - tables
  - toc:
      permalink: true
  - pymdownx.superfences:
      custom_fences:
        - name: mermaid
          class: mermaid
          format: !!python/name:pymdownx.superfences.fence_code_format
  - pymdownx.tasklist:
      custom_checkbox: true
  - admonition
  - pymdownx.details
{navBlock}
""";
    }

    private static void WriteZipEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(content);
    }

    private static string MarkdownWithStaleWarning(string? markdown, string? state)
    {
        var body = markdown ?? "*(No content generated.)*";
        if (state == "STALE")
            return "!!! warning \"Out of date\"\n    This section was signed against an older source version.\n    It has not yet been re-confirmed after the latest re-sync.\n\n" + body;
        return body;
    }

    // ── Combined markdown (for Pandoc formats) ───────────────────────────

    private static string CombineMarkdown(Corpus corpus, IReadOnlyList<DocSection> sections)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"---");
        sb.AppendLine($"title: \"{corpus.Name} — Transition Documentation\"");
        sb.AppendLine($"date: \"{DateTimeOffset.UtcNow:yyyy-MM-dd}\"");
        sb.AppendLine($"---");
        sb.AppendLine();

        foreach (var kind in SectionOrder)
        {
            var kindSections = sections
                .Where(s => s.SectionKind == kind)
                .OrderBy(s => s.Subroutine?.Name ?? s.ModuleName ?? s.Id.ToString())
                .ToList();
            foreach (var s in kindSections)
            {
                if (s.State == "STALE")
                    sb.AppendLine("> ⚠ **This section may be out of date** — signed against an older source version.");
                sb.AppendLine(s.RenderedMarkdown ?? $"*(No content for {kind})*");
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    // ── Pandoc runner (PDF / DOCX) ───────────────────────────────────────

    private async Task<ExportResult> RunPandocAsync(
        string inputMarkdown,
        string outputExt,
        string pandocArgs,
        string contentType,
        string fileName,
        CancellationToken ct)
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"astra-export-{Guid.NewGuid():N}.md");
        var outputPath = Path.Combine(Path.GetTempPath(), $"astra-export-{Guid.NewGuid():N}.{outputExt}");
        try
        {
            await File.WriteAllTextAsync(inputPath, inputMarkdown, Encoding.UTF8, ct);
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "pandoc",
                Arguments = $"\"{inputPath}\" -o \"{outputPath}\" {pandocArgs}",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.Start();
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Pandoc exited {process.ExitCode}: {stderr}");

            var bytes = await File.ReadAllBytesAsync(outputPath, ct);
            _logger.LogInformation("Pandoc export: format={Ext} bytes={N}", outputExt, bytes.Length);
            return new ExportResult(fileName, contentType, bytes);
        }
        finally
        {
            TryDelete(inputPath);
            TryDelete(outputPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ── Confluence JSON payload ──────────────────────────────────────────

    private static ExportResult BuildConfluenceJson(Corpus corpus, IReadOnlyList<DocSection> sections)
    {
        // Returns an array of Confluence REST API page payloads — one root page
        // (overview) with child pages per section kind. The caller POSTs each
        // to POST /wiki/rest/api/content on their Confluence instance.
        var pages = new List<object>();

        // Root / overview page
        var overviewSection = sections.FirstOrDefault(s => s.SectionKind == "overview");
        pages.Add(ConfluencePage(
            title: corpus.Name,
            body: MarkdownToConfluenceHtml(overviewSection?.RenderedMarkdown ?? $"<p>Overview for {corpus.Name}</p>"),
            labels: ["astra", "docs", "overview"]));

        // One child page per kind group
        foreach (var kind in SectionOrder.Skip(1))
        {
            var kindSections = sections.Where(s => s.SectionKind == kind).ToList();
            if (kindSections.Count == 0) continue;

            var kindHtml = string.Concat(kindSections.Select(s =>
            {
                var warning = s.State == "STALE"
                    ? "<ac:structured-macro ac:name=\"warning\"><ac:rich-text-body><p>This section may be out of date — signed against an older source version.</p></ac:rich-text-body></ac:structured-macro>"
                    : "";
                return warning + MarkdownToConfluenceHtml(s.RenderedMarkdown ?? $"<p>No content for {s.SectionKind}.</p>");
            }));

            pages.Add(ConfluencePage(
                title: $"{corpus.Name} — {ConfluenceKindTitle(kind)}",
                body: kindHtml,
                labels: ["astra", "docs", kind]));
        }

        var json = JsonSerializer.Serialize(pages, new JsonSerializerOptions { WriteIndented = true });
        return new ExportResult(
            $"{Slug(corpus.Name)}-confluence.json",
            "application/json",
            Encoding.UTF8.GetBytes(json));
    }

    private static object ConfluencePage(string title, string body, string[] labels) => new
    {
        type = "page",
        title,
        body = new
        {
            storage = new
            {
                value = body,
                representation = "storage",
            },
        },
        metadata = new
        {
            labels = labels.Select(l => new { prefix = "global", name = l }),
        },
    };

    private static string MarkdownToConfluenceHtml(string markdown)
    {
        // Lightweight conversion: headings, bold, code fences, Mermaid macros.
        // For full fidelity the caller should pipe through Pandoc; this covers
        // the common patterns our generated markdown actually uses.
        var html = markdown
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        // Mermaid blocks → Confluence macro
        html = Regex.Replace(html, @"```mermaid\n([\s\S]*?)```",
            m => $"<ac:structured-macro ac:name=\"mermaid\"><ac:parameter ac:name=\"diagramDefinition\">{m.Groups[1].Value.Trim()}</ac:parameter></ac:structured-macro>",
            RegexOptions.Multiline);

        // Generic code blocks → Confluence code macro
        html = Regex.Replace(html, @"```(\w*)\n([\s\S]*?)```",
            m => $"<ac:structured-macro ac:name=\"code\"><ac:parameter ac:name=\"language\">{m.Groups[1].Value}</ac:parameter><ac:plain-text-body><![CDATA[{m.Groups[2].Value}]]></ac:plain-text-body></ac:structured-macro>",
            RegexOptions.Multiline);

        // Headings
        html = Regex.Replace(html, @"^#{1} (.+)$", m => $"<h1>{m.Groups[1].Value}</h1>", RegexOptions.Multiline);
        html = Regex.Replace(html, @"^#{2} (.+)$", m => $"<h2>{m.Groups[1].Value}</h2>", RegexOptions.Multiline);
        html = Regex.Replace(html, @"^#{3} (.+)$", m => $"<h3>{m.Groups[1].Value}</h3>", RegexOptions.Multiline);
        html = Regex.Replace(html, @"^#{4} (.+)$", m => $"<h4>{m.Groups[1].Value}</h4>", RegexOptions.Multiline);

        // Bold / italic
        html = Regex.Replace(html, @"\*\*(.+?)\*\*", m => $"<strong>{m.Groups[1].Value}</strong>");
        html = Regex.Replace(html, @"\*(.+?)\*", m => $"<em>{m.Groups[1].Value}</em>");
        html = Regex.Replace(html, @"`(.+?)`", m => $"<code>{m.Groups[1].Value}</code>");

        // HR → Confluence
        html = Regex.Replace(html, @"^---$", "<hr/>", RegexOptions.Multiline);

        // Blank lines between paragraphs
        html = Regex.Replace(html, @"\n\n+", "</p><p>");
        return $"<p>{html}</p>";
    }

    private static string ConfluenceKindTitle(string kind) => kind switch
    {
        "module"            => "Modules",
        "routine-summary"   => "Routines",
        "data-dictionary"   => "Data Dictionary",
        "glossary"          => "Glossary",
        "interface"         => "Interfaces",
        "business-rules"    => "Business Rules",
        "sequence-diagram"  => "Sequence Diagrams",
        "dependency-diagram" => "Dependency Diagrams",
        _                   => kind,
    };

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string Slug(string name) =>
        Regex.Replace(name.ToLowerInvariant().Replace(' ', '-'), @"[^a-z0-9\-]", "")
             .Trim('-');
}
