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
/// SIGNED, STALE, and DRAFT sections are included. STALE gets a "may be out of date"
/// banner; DRAFT gets a "not yet reviewed" banner.
///
/// Section kind names in the DB:
///   overview, module, routine-summary, data-dictionary, glossary,
///   interface, business-rule, diagram
/// </summary>
public sealed class DocExportService
{
    private readonly AppDbContext _db;
    private readonly ILogger<DocExportService> _logger;

    // Themed pandoc reference document for DOCX export — shipped next to the
    // binary (see Astra.Api.csproj) and applied via --reference-doc.
    private static readonly string DocxReferencePath =
        Path.Combine(AppContext.BaseDirectory, "Docs", "assets", "reference.docx");

    // Canonical section kinds (as stored in the DB).
    private static readonly string[] SectionOrder =
    [
        "overview", "module", "routine-summary",
        "data-dictionary", "glossary", "interface", "business-rule", "diagram",
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
            .Where(s => s.CorpusId == corpusId
                     && (s.State == "SIGNED" || s.State == "STALE" || s.State == "DRAFT"))
            .Include(s => s.Subroutine)
            .ToListAsync(ct);

        return format.ToLowerInvariant() switch
        {
            "mkdocs" => BuildMkDocsZip(corpus, sections),
            "pdf" => await RunPandocAsync(
                CombineMarkdown(corpus, sections),
                outputExt: "pdf",
                pandocArgs: "--pdf-engine=weasyprint --toc --toc-depth=2 --number-sections",
                cssContent: PdfStylesheet,
                contentType: "application/pdf",
                fileName: $"{Slug(corpus.Name)}-docs.pdf",
                ct),
            "docx" => await RunPandocAsync(
                CombineMarkdown(corpus, sections),
                outputExt: "docx",
                pandocArgs: "--toc --toc-depth=2 --number-sections",
                cssContent: null,
                contentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                fileName: $"{Slug(corpus.Name)}-docs.docx",
                ct,
                referenceDocPath: DocxReferencePath),
            "requirements-docx" => await RunPandocAsync(
                CombineRequirementsMarkdown(corpus, sections),
                outputExt: "docx",
                pandocArgs: "--toc --toc-depth=2 --number-sections",
                cssContent: null,
                contentType: "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                fileName: $"{Slug(corpus.Name)}-requirements.docx",
                ct,
                referenceDocPath: DocxReferencePath),
            "requirements-pdf" => await RunPandocAsync(
                CombineRequirementsMarkdown(corpus, sections),
                outputExt: "pdf",
                pandocArgs: "--pdf-engine=weasyprint --toc --toc-depth=2 --number-sections",
                cssContent: PdfStylesheet,
                contentType: "application/pdf",
                fileName: $"{Slug(corpus.Name)}-requirements.pdf",
                ct),
            "confluence" => BuildConfluenceJson(corpus, sections),
            _ => throw new ArgumentException(
                $"Unknown format '{format}'. Valid: mkdocs, pdf, docx, confluence, requirements-docx, requirements-pdf"),
        };
    }

    // ── MkDocs zip ──────────────────────────────────────────────────────

    private static ExportResult BuildMkDocsZip(Corpus corpus, IReadOnlyList<DocSection> sections)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteMkDocsTree(zip, "", corpus, sections);
        }

        return new ExportResult(
            $"{Slug(corpus.Name)}-docs.zip",
            "application/zip",
            ms.ToArray());
    }

    /// <summary>
    /// Writes the MkDocs-Material project tree into an existing archive under
    /// <paramref name="prefix"/> — "" for the standalone docs zip, "docs/" when
    /// embedded in the project-level artifact bundle (ProjectExportService).
    /// Nav paths inside mkdocs.yml stay relative, so the tree works from
    /// whatever directory it lands in.
    /// </summary>
    internal static void WriteMkDocsTree(ZipArchive zip, string prefix, Corpus corpus, IReadOnlyList<DocSection> sections)
    {
        var nav = new StringBuilder();
        nav.AppendLine("nav:");

        // Same-named routines in different files (MINPACK's ex/ vs examples/
        // trees, or every example defining an `fcn`) slug to the same path.
        // ZipArchive would happily write duplicate entries, which extractors
        // reject or silently overwrite — so suffix collisions with -2, -3, …
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Unique(string dir, string slug)
        {
            var candidate = slug;
            var i = 2;
            while (!usedPaths.Add($"{dir}/{candidate}.md")) candidate = $"{slug}-{i++}";
            return candidate;
        }

        // Overview → docs/index.md
        var overview = sections.FirstOrDefault(s => s.SectionKind == "overview");
        WriteZipEntry(zip, $"{prefix}docs/index.md",
            MarkdownWithStateWarning(overview?.RenderedMarkdown ?? $"# {corpus.Name}\n\n*(No overview generated yet.)*", overview?.State));
        nav.AppendLine("  - Home: index.md");

        // Modules → docs/modules/{slug}.md
        var modules = sections.Where(s => s.SectionKind == "module").ToList();
        if (modules.Count > 0)
        {
            nav.AppendLine("  - Modules:");
            foreach (var m in modules.OrderBy(s => s.ModuleName))
            {
                var slug = Unique("modules", Slug(m.ModuleName ?? m.Id.ToString()));
                WriteZipEntry(zip, $"{prefix}docs/modules/{slug}.md", MarkdownWithStateWarning(m.RenderedMarkdown, m.State));
                nav.AppendLine($"    - '{m.ModuleName ?? slug}': modules/{slug}.md");
            }
        }

        // Routines → docs/routines/{slug}.md — full page per substantive
        // routine; bean-style accessors collapse into one table page so a
        // 450-routine corpus isn't 150 pages of getter boilerplate.
        var routines = sections.Where(s => s.SectionKind == "routine-summary").ToList();
        var accessors = routines.Where(IsTrivialAccessor).ToList();
        if (routines.Count > 0)
        {
            nav.AppendLine("  - Routines:");
            foreach (var r in routines.Where(s => !IsTrivialAccessor(s)).OrderBy(s => s.Subroutine?.Name ?? s.Id.ToString()))
            {
                var name = r.Subroutine?.Name ?? r.Id.ToString("N")[..8];
                var slug = Unique("routines", Slug(name));
                WriteZipEntry(zip, $"{prefix}docs/routines/{slug}.md", MarkdownWithStateWarning(r.RenderedMarkdown, r.State));
                nav.AppendLine($"    - '{name}': routines/{slug}.md");
            }
            if (accessors.Count > 0)
            {
                WriteZipEntry(zip, $"{prefix}docs/routines/accessors.md",
                    "# Property Accessors\n\n" + AccessorTable(accessors));
                nav.AppendLine($"    - 'Property accessors ({accessors.Count})': routines/accessors.md");
            }
        }

        // Reference catalog sections
        var refSections = new (string Kind, string Label, string File)[]
        {
            ("data-dictionary", "Data Dictionary", "reference/data-dictionary.md"),
            ("glossary",        "Glossary",        "reference/glossary.md"),
            ("interface",       "Interfaces",      "reference/interfaces.md"),
            ("business-rule",   "Business Rules",  "reference/business-rules.md"),
        };

        var anyRef = false;
        foreach (var (kind, label, file) in refSections)
        {
            // A corpus can hold MANY sections of one reference kind
            // (EnvestNet: 31 business-rule sections). Ship them all —
            // taking only the first silently dropped 30 of 31 rules.
            var kindSections = sections.Where(x => x.SectionKind == kind).ToList();
            if (kindSections.Count == 0) continue;
            WriteZipEntry(zip, $"{prefix}docs/{file}", CombineReferenceKind(kindSections));
            if (!anyRef) { nav.AppendLine("  - Reference:"); anyRef = true; }
            nav.AppendLine($"    - '{label}': {file}");
        }

        // Requirements pack → docs/requirements/*.md. Separate nav group
        // because it addresses a different reader than the transition
        // docs: a business analyst scoping a replacement, not an engineer
        // maintaining the original.
        var reqSections = new (string Kind, string Label, string File)[]
        {
            ("capability-map",         "Capability Map",         "requirements/capability-map.md"),
            ("process-flow",           "Process Flows",          "requirements/process-flows.md"),
            ("functional-requirement", "Functional Requirements", "requirements/functional-requirements.md"),
            ("nfr",                    "Non-Functional Characteristics", "requirements/non-functional.md"),
        };
        var anyReq = false;
        foreach (var (kind, label, file) in reqSections)
        {
            var kindSections = sections.Where(x => x.SectionKind == kind).ToList();
            if (kindSections.Count == 0) continue;
            WriteZipEntry(zip, prefix + "docs/" + file, CombineReferenceKind(kindSections));
            if (!anyReq) { nav.AppendLine("  - Requirements:"); anyReq = true; }
            nav.AppendLine("    - '" + label + "': " + file);
        }

        // Diagrams → docs/diagrams/{slug}.md
        var diagrams = sections.Where(s => s.SectionKind == "diagram").ToList();
        if (diagrams.Count > 0)
        {
            nav.AppendLine("  - Diagrams:");
            foreach (var d in diagrams.OrderBy(s => s.Scope).ThenBy(s => s.Subroutine?.Name ?? s.ModuleName))
            {
                var name = d.Scope == "subroutine" && d.Subroutine?.Name is string sn
                    ? $"{sn} — Sequence"
                    : d.ModuleName is string mn
                        ? $"{mn} — Dependency"
                        : d.Id.ToString("N")[..8];
                var slug = Unique("diagrams", Slug(name));
                WriteZipEntry(zip, $"{prefix}docs/diagrams/{slug}.md", MarkdownWithStateWarning(d.RenderedMarkdown, d.State));
                nav.AppendLine($"    - '{name}': diagrams/{slug}.md");
            }
        }

        // mkdocs.yml
        WriteZipEntry(zip, $"{prefix}mkdocs.yml", BuildMkDocsYml(corpus, nav.ToString()));
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

    internal static void WriteZipEntry(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(content);
    }

    private static string MarkdownWithStateWarning(string? markdown, string? state)
    {
        var body = markdown ?? "*(No content generated.)*";
        if (state == "STALE")
            return "!!! warning \"Out of date\"\n    This section was signed against an older source version.\n    It has not yet been re-confirmed after the latest re-sync.\n\n" + body;
        if (state == "DRAFT")
            return "!!! note \"Draft\"\n    This section has not yet been reviewed and signed off.\n\n" + body;
        return body;
    }

    // ── Combined markdown (for Pandoc formats) ────────────────────────────

    // Reference chapters, in reading order. Each carries a short authored intro
    // that frames what the chapter contains and how to read its entries — the
    // connective tissue a human technical writer supplies between reference
    // sections. The overview section is handled separately as the opening
    // "Executive Summary" chapter, so it is not listed here.
    private static readonly (string Kind, string ChapterTitle, string Intro)[] Chapters =
    [
        ("module", "Module Reference",
            "Each entry in this chapter corresponds to one module in the system. It states the module's purpose, the architectural characteristics that shape how it behaves, the public routines it exposes to the rest of the system, and the risks a maintainer should weigh before changing it."),
        ("routine-summary", "Routine Reference",
            "This chapter is the per-routine reference. Every entry describes what the routine does in domain terms, the inputs it consumes and outputs it produces, the preconditions a caller must satisfy before invoking it, and the edge cases and boundary behaviours to be aware of."),
        ("data-dictionary", "Data Dictionary",
            "The data dictionary catalogues the domain entities, fields, and types that flow through the system, each with a plain-language description of the role it plays."),
        ("glossary", "Glossary",
            "Domain and technical terms used throughout this document, defined for a reader who is approaching the system for the first time."),
        ("interface", "Interface Catalogue",
            "The external and internal interfaces the system exposes or depends upon, together with the contract each one implies for its callers."),
        ("business-rule", "Business Rules",
            "The business rules and invariants the system enforces, extracted from the implementation and stated in a form that a domain reviewer can confirm."),
        ("diagram", "Sequence & Dependency Diagrams",
            "Sequence and dependency diagrams for the system's principal flows. The diagram definition is reproduced inline; an interactive, rendered version is available in the web UI and the MkDocs export."),
    ];

    private static string CombineMarkdown(Corpus corpus, IReadOnlyList<DocSection> sections)
    {
        var sb = new StringBuilder();

        // YAML front matter — pandoc converts this into a title page.
        sb.AppendLine("---");
        sb.AppendLine($"title: \"{corpus.Name}\"");
        sb.AppendLine("subtitle: \"System Documentation\"");
        sb.AppendLine($"date: \"{DateTimeOffset.UtcNow:MMMM d, yyyy}\"");
        sb.AppendLine("author: \"Astra RE Harness\"");
        sb.AppendLine("---");
        sb.AppendLine();

        // Unnumbered preface — reader's guide + review-state legend. Kept out of
        // the section numbering so the Executive Summary is chapter 1.
        sb.AppendLine("# About This Document {.unnumbered}");
        sb.AppendLine();
        sb.AppendLine(
            $"This is the system documentation for **{MdEscape(corpus.Name)}**. It opens with an " +
            "executive summary of the system as a whole, then provides reference chapters covering " +
            "every module and routine, followed by supporting catalogues — data dictionary, glossary, " +
            "interfaces, and business rules — and the system's sequence and dependency diagrams.");
        sb.AppendLine();
        sb.AppendLine(
            "Each section is generated from the source and independently reviewed and signed off. " +
            "Sections that have not completed review are flagged inline:");
        sb.AppendLine();
        sb.AppendLine("> ⚠ **Out of date** — signed against an earlier version of the source and awaiting re-confirmation.");
        sb.AppendLine(">");
        sb.AppendLine("> 📝 **Draft** — generated but not yet reviewed and signed off.");
        sb.AppendLine();

        // Chapter 1 — Executive Summary, sourced from the corpus-level overview
        // synthesis. Its own top-level title is dropped so its body headings
        // become the numbered subsections of this chapter.
        var overview = sections.FirstOrDefault(s => s.SectionKind == "overview");
        if (overview?.RenderedMarkdown is { Length: > 0 } overviewMd)
        {
            sb.AppendLine("# Executive Summary");
            sb.AppendLine();
            sb.AppendLine(NormalizeHeadings(StripLeadingH1(overviewMd)));
            sb.AppendLine();
        }

        // Reference chapters.
        foreach (var (kind, chapterTitle, intro) in Chapters)
        {
            var kindSections = sections
                .Where(s => s.SectionKind == kind)
                .OrderBy(s => s.Subroutine?.Name ?? s.ModuleName ?? s.Id.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Collapse bean-style accessors out of the routine chapter —
            // they get a one-line-per-family table at the chapter's end.
            var accessorRows = kind == "routine-summary"
                ? kindSections.Where(IsTrivialAccessor).ToList()
                : new List<DocSection>();
            if (accessorRows.Count > 0)
                kindSections = kindSections.Where(s => !IsTrivialAccessor(s)).ToList();

            if (kindSections.Count == 0 && accessorRows.Count == 0) continue;

            sb.AppendLine($"# {chapterTitle}");
            sb.AppendLine();
            sb.AppendLine(intro);
            sb.AppendLine();

            foreach (var s in kindSections)
            {
                if (s.State == "STALE")
                    sb.AppendLine("> ⚠ **Out of date** — signed against an older source version.\n");
                else if (s.State == "DRAFT")
                    sb.AppendLine("> 📝 **Draft** — not yet reviewed and signed off.\n");

                var content = s.RenderedMarkdown ?? $"*(No content for {kind}.)*";
                if (kind == "diagram")
                    content = StripMermaid(content, s.Subroutine?.Name ?? s.ModuleName);

                // Renumber each section's headings so it nests cleanly under its
                // chapter (top heading → H2, no skipped levels). This is what
                // lets --number-sections produce the 1 / 2.1 / 2.1.1 hierarchy.
                sb.AppendLine(NormalizeHeadings(content));
                sb.AppendLine();
            }

            if (accessorRows.Count > 0)
            {
                sb.AppendLine("## Property Accessors");
                sb.AppendLine();
                sb.AppendLine(AccessorTable(accessorRows));
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    // ── Trivial-accessor collapsing ─────────────────────────────────────
    // Bean-style corpora (EnvestNet: 450 routines, a third of them getters
    // and setters) drown the routine reference in boilerplate — a full
    // formal page per one-line accessor is what readers flag as poor
    // quality. Anything with a get/set/is name and a body of five lines or
    // fewer collapses into a one-line-per-family table; everything else
    // keeps its full page.

    private static readonly Regex AccessorNameRegex =
        new(@"^(get|set|is)[A-Z_]", RegexOptions.Compiled);

    private static bool IsTrivialAccessor(DocSection s) =>
        s.SectionKind == "routine-summary"
        && s.Subroutine is { } sub
        && AccessorNameRegex.IsMatch(sub.Name)
        && sub.LineEnd - sub.LineStart <= 4;

    private static string AccessorTable(IReadOnlyList<DocSection> accessors)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $"{accessors.Count} bean-style accessors (get/set/is, five lines or fewer) are summarised " +
            "one line per family below; they carry no behaviour beyond field access. " +
            "Full evidence-cited specs remain available for each in the review UI.");
        sb.AppendLine();
        sb.AppendLine("| Routine | Summary |");
        sb.AppendLine("|---|---|");
        foreach (var family in accessors
                     .GroupBy(a => a.Subroutine!.Name)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var label = family.Count() > 1 ? $"`{family.Key}` ×{family.Count()}" : $"`{family.Key}`";
            sb.AppendLine($"| {label} | {FirstSentence(family.First().RenderedMarkdown)} |");
        }
        return sb.ToString();
    }

    private static string FirstSentence(string? markdown, int max = 180)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var line = markdown.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith('>') && !l.StartsWith("!!!"));
        if (line is null) return "";
        var dot = line.IndexOf(". ", StringComparison.Ordinal);
        var sentence = dot > 0 ? line[..(dot + 1)] : line;
        if (sentence.Length > max) sentence = sentence[..max] + "…";
        return sentence.Replace("|", "\\|");
    }

    /// <summary>
    /// Joins every section of one reference kind (business rules, glossary,
    /// interfaces, data dictionary) into a single page, with one shared
    /// state banner instead of one per entry.
    /// </summary>
    private static string CombineReferenceKind(IReadOnlyList<DocSection> kindSections)
    {
        var sb = new StringBuilder();
        if (kindSections.Any(s => s.State == "STALE"))
            sb.AppendLine("!!! warning \"Out of date\"\n    One or more entries were signed against an older source version.\n");
        else if (kindSections.Any(s => s.State == "DRAFT"))
            sb.AppendLine("!!! note \"Draft\"\n    One or more entries have not yet been reviewed and signed off.\n");
        foreach (var s in kindSections.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id))
        {
            sb.AppendLine(s.RenderedMarkdown ?? "");
        }
        return sb.ToString();
    }

    // ── Requirements pack (Phase B) ──────────────────────────────────
    // A different document from the transition docs: it states what the
    // legacy system does today, in business language, so a replacement
    // can be scoped and verified against it. Chapter order follows how a
    // reader builds understanding — what the system does, how work flows
    // through it, the specific behaviours, then the operational envelope.

    private static readonly (string Kind, string ChapterTitle, string Intro)[] RequirementsChapters =
    [
        ("capability-map", "Business Capabilities",
            "The distinct capabilities the system provides today, described in business terms. This chapter answers \"what does this system do for the business?\" and frames everything that follows."),
        ("process-flow", "Business Process Flows",
            "The end-to-end journeys the system supports, narrated as business process. Each flow records the gating rules exactly as they behave today, including the points where progression is blocked or a step is silently skipped."),
        ("functional-requirement", "Functional Requirements",
            "Numbered statements of current system behaviour, each with acceptance criteria that can be verified against the existing system and a traceability line back to the routines the behaviour was derived from. These describe the system as it is — where present behaviour is surprising, it is recorded as-is rather than corrected, so a replacement team can decide deliberately whether to preserve it."),
        ("nfr", "Non-Functional Characteristics",
            "The operational envelope the system exhibits today — performance, data integrity, concurrency, security, and error handling. Each entry names what a replacement inherits if the behaviour is carried over unchanged."),
    ];

    private static string CombineRequirementsMarkdown(Corpus corpus, IReadOnlyList<DocSection> sections)
    {
        var sb = new StringBuilder();

        sb.AppendLine("---");
        sb.AppendLine($"title: \"{corpus.Name}\"");
        sb.AppendLine("subtitle: \"Requirements Pack — Current-State Behaviour\"");
        sb.AppendLine($"date: \"{DateTimeOffset.UtcNow:MMMM d, yyyy}\"");
        sb.AppendLine("author: \"Astra RE Harness\"");
        sb.AppendLine("---");
        sb.AppendLine();

        sb.AppendLine("# About This Document {.unnumbered}");
        sb.AppendLine();
        sb.AppendLine(
            $"This pack documents what **{MdEscape(corpus.Name)}** does today. It is written for a " +
            "business analyst, solution architect, or delivery team scoping a replacement, and it " +
            "deliberately describes current behaviour rather than proposing a target design.");
        sb.AppendLine();
        sb.AppendLine(
            "Every requirement is derived from specifications extracted directly from the source and " +
            "carries a traceability line naming the routines it came from. Where the system's present " +
            "behaviour is unexpected — an unvalidated input, a silent no-op, an ignored parameter — it " +
            "is recorded as it is. Those are real requirements: a replacement must consciously decide " +
            "whether to preserve or change them.");
        sb.AppendLine();
        sb.AppendLine("> Sections marked **Draft** have been generated but not yet reviewed and signed off.");
        sb.AppendLine();

        var any = false;
        foreach (var (kind, chapterTitle, intro) in RequirementsChapters)
        {
            var kindSections = sections.Where(x => x.SectionKind == kind).OrderBy(x => x.CreatedAt).ToList();
            if (kindSections.Count == 0) continue;
            any = true;

            sb.AppendLine($"# {chapterTitle}");
            sb.AppendLine();
            sb.AppendLine(intro);
            sb.AppendLine();
            foreach (var sec in kindSections)
            {
                sb.AppendLine(NormalizeHeadings(sec.RenderedMarkdown ?? ""));
                sb.AppendLine();
            }
        }

        if (!any)
        {
            sb.AppendLine("# No Requirements Generated {.unnumbered}");
            sb.AppendLine();
            sb.AppendLine(
                "This project has no requirements sections yet. Generate them with the " +
                "`capability-map`, `process-flow`, `functional-requirement` and `nfr` stages.");
        }

        return sb.ToString();
    }

    // ── Heading-outline normalisation ────────────────────────────────────
    // Section renderers emit inconsistent heading levels (## for modules, ###
    // for routines, #### for field groups). To get a clean numbered outline we
    // remap each section's DISTINCT heading levels to consecutive levels
    // starting at H2 — collapsing gaps — so it slots under the chapter H1.

    private static readonly Regex AtxHeadingRegex =
        new(@"^(#{1,6})(\s.*)$", RegexOptions.Compiled);

    private static bool IsFence(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal);
    }

    private static string NormalizeHeadings(string markdown, int topLevel = 2)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');

        // Pass 1 — collect the distinct heading levels used outside code fences.
        var levels = new SortedSet<int>();
        var inFence = false;
        foreach (var line in lines)
        {
            if (IsFence(line)) { inFence = !inFence; continue; }
            if (inFence) continue;
            var m = AtxHeadingRegex.Match(line);
            if (m.Success) levels.Add(m.Groups[1].Value.Length);
        }
        if (levels.Count == 0) return markdown;

        var ordered = levels.ToList();
        var map = new Dictionary<int, int>();
        for (var i = 0; i < ordered.Count; i++)
            map[ordered[i]] = Math.Min(topLevel + i, 6);

        // Pass 2 — rewrite heading lines, leaving fenced code untouched.
        var sb = new StringBuilder();
        inFence = false;
        foreach (var line in lines)
        {
            if (IsFence(line)) { inFence = !inFence; sb.Append(line).Append('\n'); continue; }
            if (!inFence)
            {
                var m = AtxHeadingRegex.Match(line);
                if (m.Success)
                {
                    sb.Append(new string('#', map[m.Groups[1].Value.Length]))
                      .Append(m.Groups[2].Value).Append('\n');
                    continue;
                }
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private static string StripLeadingH1(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        foreach (var t in lines.Select((l, i) => (line: l.TrimStart(), idx: i)))
        {
            if (t.line.Length == 0) continue;          // skip leading blanks
            if (t.line.StartsWith("# ", StringComparison.Ordinal))
                lines[t.idx] = "";                     // drop the title line
            break;                                     // only the first non-blank line
        }
        return string.Join("\n", lines);
    }

    private static string MdEscape(string s) =>
        s.Replace("*", "\\*").Replace("_", "\\_").Replace("[", "\\[").Replace("]", "\\]");

    private static readonly Regex MermaidBlockRegex = new(
        @"```mermaid[\r\n]+([\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static string StripMermaid(string markdown, string? name)
    {
        // weasyprint has no JS engine so Mermaid can't render. Show the definition
        // as a readable code block — sequenceDiagram / graph syntax is clear enough
        // for an engineer to trace the call flow by eye.
        return MermaidBlockRegex.Replace(markdown, m =>
        {
            var code = m.Groups[1].Value.Trim();
            var htmlLabel = (name ?? "this component")
                .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            return
                $"<div class=\"diagram-placeholder\">Sequence diagram — <strong>{htmlLabel}</strong>" +
                $" <em>(interactive render available in the web UI and MkDocs export)</em></div>\n\n" +
                $"```\n{code}\n```";
        });
    }

    // ── Pandoc runner (PDF / DOCX) ──────────────────────────────────────

    private async Task<ExportResult> RunPandocAsync(
        string inputMarkdown,
        string outputExt,
        string pandocArgs,
        string? cssContent,
        string contentType,
        string fileName,
        CancellationToken ct,
        string? referenceDocPath = null)
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"astra-export-{Guid.NewGuid():N}.md");
        var outputPath = Path.Combine(Path.GetTempPath(), $"astra-export-{Guid.NewGuid():N}.{outputExt}");
        var cssPath = cssContent != null
            ? Path.Combine(Path.GetTempPath(), $"astra-export-{Guid.NewGuid():N}.css")
            : null;
        try
        {
            await File.WriteAllTextAsync(inputPath, inputMarkdown, Encoding.UTF8, ct);
            if (cssPath != null)
                await File.WriteAllTextAsync(cssPath, cssContent!, Encoding.UTF8, ct);

            var args = $"\"{inputPath}\" -o \"{outputPath}\" {pandocArgs}";
            if (cssPath != null)
                args += $" --css=\"{cssPath}\"";
            // Apply the themed Word template when present. If it's missing we
            // fall back to pandoc's default styling rather than failing.
            if (referenceDocPath != null && File.Exists(referenceDocPath))
                args += $" --reference-doc=\"{referenceDocPath}\"";
            else if (referenceDocPath != null)
                _logger.LogWarning("DOCX reference doc not found at {Path}; using pandoc defaults.", referenceDocPath);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "pandoc",
                Arguments = args,
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
            if (cssPath != null) TryDelete(cssPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ── Professional CSS for PDF export ──────────────────────────────────

    private const string PdfStylesheet = """
/* Astra RE Harness — Professional PDF Export */
/* Rendered by pandoc + weasyprint           */

@page {
    margin: 2.4cm 2cm 2.2cm 2.4cm;
    size: A4;
    @bottom-left {
        content: string(doctitle);
        font-family: 'Calibri', 'Carlito', 'Segoe UI', 'DejaVu Sans', Arial, sans-serif;
        font-size: 8pt;
        color: #9ca3af;
    }
    @bottom-right {
        content: counter(page) " of " counter(pages);
        font-family: 'Calibri', 'Carlito', 'Segoe UI', 'DejaVu Sans', Arial, sans-serif;
        font-size: 8pt;
        color: #9ca3af;
    }
}

@page :first {
    @bottom-left { content: none; }
    @bottom-right { content: none; }
}

body {
    font-family: 'Calibri', 'Carlito', 'Segoe UI', 'DejaVu Sans', Arial, sans-serif;
    font-size: 10.5pt;
    line-height: 1.55;
    color: #1a1a1a;
}

/* Carry the document title into the running footer. */
h1.title { string-set: doctitle content(); }

/* ── Title block ── */
header#title-block-header {
    padding: 8cm 0 0;
    page-break-after: always;
}

h1.title {
    font-size: 33pt;
    font-weight: 700;
    color: #0A1628;
    margin: 0 0 0.5cm;
    line-height: 1.1;
    letter-spacing: -0.01em;
    border-bottom: 3px solid #156082;
    padding-bottom: 0.5cm;
}

p.subtitle {
    font-size: 15pt;
    font-weight: 400;
    color: #156082;
    margin: 0 0 3cm;
    letter-spacing: 0.02em;
}

p.author, p.date {
    font-size: 10pt;
    color: #6b7280;
    margin: 0.1cm 0 0;
}

/* ── Table of contents ── */
nav#TOC {
    page-break-after: always;
    padding: 0;
    margin-bottom: 1cm;
}

nav#TOC::before {
    content: "Contents";
    display: block;
    font-size: 18pt;
    font-weight: 700;
    color: #0A1628;
    margin-bottom: 0.6cm;
    padding-bottom: 0.25cm;
    border-bottom: 2px solid #156082;
}

nav#TOC ul {
    margin: 0;
    padding-left: 0;
    list-style: none;
}

nav#TOC ul ul { padding-left: 0.8cm; }

nav#TOC li {
    margin: 0.14em 0;
    line-height: 1.5;
}

nav#TOC > ul > li {
    margin-top: 0.28em;
    font-weight: 600;
}

nav#TOC a {
    color: #1a1a1a;
    text-decoration: none;
}

nav#TOC ul ul a { font-weight: 400; color: #374151; }

/* ── Headings ──
   H1 = numbered chapter, starts a new page, Atlas-style navy with a rule. */
h1 {
    font-size: 21pt;
    font-weight: 700;
    color: #0A1628;
    border-bottom: 1.5px solid #d4d9e0;
    padding-bottom: 0.22cm;
    margin: 0 0 0.55cm;
    page-break-before: always;
    page-break-after: avoid;
    line-height: 1.15;
}

/* The unnumbered preface follows directly after the TOC's own page break, so
   it must not force a second break (which would leave a blank page). Every
   numbered chapter keeps its break-before and begins on a fresh page. */
h1.unnumbered { page-break-before: avoid; }

h2 {
    font-size: 14.5pt;
    font-weight: 700;
    color: #0E2841;
    margin: 0.85cm 0 0.25cm;
    page-break-after: avoid;
}

h3 {
    font-size: 11.5pt;
    font-weight: 700;
    color: #156082;
    margin: 0.6cm 0 0.15cm;
    page-break-after: avoid;
}

h4, h5, h6 {
    font-size: 10.5pt;
    font-weight: 700;
    color: #374151;
    text-transform: uppercase;
    letter-spacing: 0.03em;
    margin: 0.45cm 0 0.12cm;
    page-break-after: avoid;
}

/* Section numbers emitted by pandoc --number-sections. */
.header-section-number {
    color: #156082;
    font-weight: 700;
    margin-right: 0.5em;
}
h1 .header-section-number { color: #156082; }
h2 .header-section-number,
h3 .header-section-number { color: #94a3b8; }

/* ── Body text ── */
p {
    margin: 0.35cm 0;
    orphans: 3;
    widows: 3;
}

/* ── Code ── */
pre {
    background: #0f172a;
    color: #e2e8f0;
    font-family: 'JetBrains Mono', 'Courier New', 'Consolas', monospace;
    font-size: 8.5pt;
    line-height: 1.5;
    padding: 0.5cm 0.6cm;
    border-radius: 5px;
    white-space: pre-wrap;
    word-wrap: break-word;
    page-break-inside: avoid;
    margin: 0.4cm 0;
    overflow: hidden;
}

code {
    font-family: 'JetBrains Mono', 'Courier New', 'Consolas', monospace;
    font-size: 8.5pt;
    background: #f1f5f9;
    color: #c7254e;
    padding: 1px 5px;
    border-radius: 3px;
}

pre code {
    background: none;
    color: inherit;
    padding: 0;
}

/* ── Tables ── */
table {
    border-collapse: collapse;
    width: 100%;
    margin: 0.5cm 0;
    font-size: 9.5pt;
    page-break-inside: avoid;
}

thead tr {
    background: #1e3a5f;
}

th {
    color: #ffffff;
    font-weight: 600;
    text-align: left;
    padding: 0.22cm 0.35cm;
    border: 1px solid #1e3a5f;
}

td {
    padding: 0.18cm 0.35cm;
    border: 1px solid #d1d5db;
    vertical-align: top;
}

tr:nth-child(even) td {
    background: #f8fafc;
}

/* ── Lists ── */
ul, ol {
    margin: 0.3cm 0;
    padding-left: 1.6em;
}

li {
    margin: 0.15cm 0;
}

/* ── Horizontal rules ── */
hr {
    border: none;
    border-top: 1px solid #e5e7eb;
    margin: 0.7cm 0;
}

/* ── Blockquotes (DRAFT / STALE banners) ── */
blockquote {
    border-left: 4px solid #f59e0b;
    background: #fffbeb;
    padding: 0.3cm 0.6cm;
    margin: 0.4cm 0;
    color: #92400e;
    border-radius: 0 4px 4px 0;
    font-style: normal;
    page-break-inside: avoid;
}

blockquote p {
    margin: 0;
}

/* ── Diagram placeholder (replaces Mermaid blocks in PDF) ── */
div.diagram-placeholder {
    border: 1.5px dashed #9ca3af;
    background: #f9fafb;
    padding: 0.45cm 0.7cm;
    color: #6b7280;
    font-size: 9.5pt;
    border-radius: 5px;
    margin: 0.35cm 0;
    page-break-inside: avoid;
}

/* ── Strong / emphasis ── */
strong {
    color: #111827;
    font-weight: 700;
}

em {
    color: #374151;
}
""";

    // ── Confluence JSON payload ──────────────────────────────────────────

    private static ExportResult BuildConfluenceJson(Corpus corpus, IReadOnlyList<DocSection> sections)
    {
        var pages = new List<object>();

        var overviewSection = sections.FirstOrDefault(s => s.SectionKind == "overview");
        pages.Add(ConfluencePage(
            title: corpus.Name,
            body: MarkdownToConfluenceHtml(overviewSection?.RenderedMarkdown ?? $"<p>Overview for {corpus.Name}</p>"),
            labels: ["astra", "docs", "overview"]));

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
        var html = markdown
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        html = Regex.Replace(html, @"```mermaid\n([\s\S]*?)```",
            m => $"<ac:structured-macro ac:name=\"mermaid\"><ac:parameter ac:name=\"diagramDefinition\">{m.Groups[1].Value.Trim()}</ac:parameter></ac:structured-macro>",
            RegexOptions.Multiline);

        html = Regex.Replace(html, @"```(\w*)\n([\s\S]*?)```",
            m => $"<ac:structured-macro ac:name=\"code\"><ac:parameter ac:name=\"language\">{m.Groups[1].Value}</ac:parameter><ac:plain-text-body><![CDATA[{m.Groups[2].Value}]]></ac:plain-text-body></ac:structured-macro>",
            RegexOptions.Multiline);

        html = Regex.Replace(html, @"^#{1} (.+)$", m => $"<h1>{m.Groups[1].Value}</h1>", RegexOptions.Multiline);
        html = Regex.Replace(html, @"^#{2} (.+)$", m => $"<h2>{m.Groups[1].Value}</h2>", RegexOptions.Multiline);
        html = Regex.Replace(html, @"^#{3} (.+)$", m => $"<h3>{m.Groups[1].Value}</h3>", RegexOptions.Multiline);
        html = Regex.Replace(html, @"^#{4} (.+)$", m => $"<h4>{m.Groups[1].Value}</h4>", RegexOptions.Multiline);

        html = Regex.Replace(html, @"\*\*(.+?)\*\*", m => $"<strong>{m.Groups[1].Value}</strong>");
        html = Regex.Replace(html, @"\*(.+?)\*", m => $"<em>{m.Groups[1].Value}</em>");
        html = Regex.Replace(html, @"`(.+?)`", m => $"<code>{m.Groups[1].Value}</code>");

        html = Regex.Replace(html, @"^---$", "<hr/>", RegexOptions.Multiline);

        html = Regex.Replace(html, @"\n\n+", "</p><p>");
        return $"<p>{html}</p>";
    }

    private static string ConfluenceKindTitle(string kind) => kind switch
    {
        "module"          => "Modules",
        "routine-summary" => "Routines",
        "data-dictionary" => "Data Dictionary",
        "glossary"        => "Glossary",
        "interface"       => "Interfaces",
        "business-rule"   => "Business Rules",
        "diagram"         => "Diagrams",
        _                 => kind,
    };

    // ── Helpers ──────────────────────────────────────────────────────────

    internal static string Slug(string name) =>
        Regex.Replace(name.ToLowerInvariant().Replace(' ', '-'), @"[^a-z0-9\-]", "")
             .Trim('-');
}
