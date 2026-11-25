using Astra.Api.Docs;
using Astra.Api.Persistence.Entities;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>The documentation PDF: diagrams are drawn, citations are short, and the requirements travel with the document.</summary>
public class DocExportRenderTests
{
    private const string Sequence = """
        sequenceDiagram
            participant Caller
            participant BuildRequestJson
            participant wsC as wsC (Coverage Sheet)
            Caller->>BuildRequestJson: call(wsD, wsC, wsP)
            Note over BuildRequestJson: Initialise ids = "4" (Trip Cancellation always included)
            loop r = COVER_FIRST_ROW+1 to COVER_LAST_ROW
                BuildRequestJson->>wsC: read Cells(r, "D") - IsYes?
                alt coverage selected
                    Note over BuildRequestJson: append "," & (r-1) to ids
                end
            end
            BuildRequestJson-->>Caller: return completed JSON string
        """;

    private const string Graph = """
        graph TD
            ValidateInputs["ValidateInputs"]
            PostJson["PostJson"]
            com_MSXML["com:MSXML2.XMLHTTP.6.0"]
            PostJson --> com_MSXML
            ValidateInputs -->|ok| PostJson
        """;

    [Fact]
    public void A_sequence_diagram_becomes_an_svg_with_its_participants_messages_and_frames()
    {
        var svg = MermaidSvg.TryRender(Sequence);
        Assert.NotNull(svg);
        Assert.StartsWith("<figure class=\"diagram", svg);
        Assert.Contains("<svg", svg);
        Assert.Contains("BuildRequestJson", svg);
        Assert.Contains("wsC (Coverage Sheet)", svg);            // the "as" label, not the id
        Assert.Contains("marker-end", svg);                      // arrows
        Assert.Contains(">loop<", svg);                          // the loop frame
        Assert.Contains(">alt<", svg);
        Assert.Contains("stroke-dasharray", svg);                // the dashed return arrow
        Assert.DoesNotContain("<script", svg);
    }

    [Fact]
    public void A_dependency_graph_becomes_an_svg_with_nodes_and_edges()
    {
        var svg = MermaidSvg.TryRender(Graph);
        Assert.NotNull(svg);
        Assert.Contains("ValidateInputs", svg);
        Assert.Contains("com:MSXML2.XMLHTTP.6.0", svg);
        Assert.Contains(">ok<", svg);                            // the edge label
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(svg!, "marker-end=\"url\\(#head\\)\"").Count);
    }

    [Fact]
    public void A_wide_and_long_sequence_diagram_goes_landscape_and_is_split_into_pages_that_each_repeat_the_header()
    {
        var sb = new System.Text.StringBuilder("sequenceDiagram\n");
        for (var i = 0; i < 12; i++) sb.Append($"    participant P{i}\n");
        for (var i = 0; i < 70; i++) sb.Append($"    P{i % 11}->>P{i % 11 + 1}: step {i} with a reasonably long description of the call\n");
        var svg = MermaidSvg.TryRender(sb.ToString());
        Assert.NotNull(svg);
        var figures = System.Text.RegularExpressions.Regex.Matches(svg!, "<figure class=\"diagram wide\">").Count;
        Assert.True(figures > 1, "a diagram taller than a page is cut into several");
        // every page names the participants at the top, so a reader is never left guessing who is who
        Assert.Equal(figures, System.Text.RegularExpressions.Regex.Matches(svg!, ">P0</text>").Count);
        Assert.Contains("viewBox=\"0 0 ", svg);
    }

    [Fact]
    public void A_requirement_is_a_short_heading_over_its_statement_and_the_criteria_are_a_list()
    {
        var md = "## FR-007 — The system shall submit the quote to the pricing API, and write premium figures only on 200.\n\n" +
                 "Body text.\n\n" +
                 "**Acceptance criteria** - A 200 response writes figures. - Any other status writes none. - Estimate only - not a bindable quote is shown.\n\n" +
                 "Traceability: PostJson";
        var shaped = DocExportService.ShapeRequirement(md);
        Assert.Contains("## FR-007\n\n**The system shall submit the quote to the pricing API, and write premium figures only on 200.**", shaped);
        Assert.Contains("**Acceptance criteria**\n\n- A 200 response writes figures.\n- Any other status writes none.\n- Estimate only - not a bindable quote is shown.", shaped);
        Assert.Contains("Traceability: PostJson", shaped);
    }

    [Fact]
    public void Something_that_is_not_a_requirement_or_has_a_single_criterion_is_left_alone()
    {
        var md = "## Trip Detail Capture\n\nText.\n\n**Acceptance criteria** - Only one thing holds.";
        Assert.Equal(md, DocExportService.ShapeRequirement(md));
    }

    [Fact]
    public void A_small_graph_is_not_scaled_up_and_connected_nodes_sit_in_the_row_nearest_their_target()
    {
        var svg = MermaidSvg.TryRender("graph TD\n  A[\"Alpha\"]\n  B[\"Beta\"]\n  C[\"Gamma\"]\n  A --> C");
        Assert.NotNull(svg);
        Assert.Contains("max-width:", svg);
    }

    [Fact]
    public void Text_is_escaped_and_something_that_is_not_a_known_diagram_is_not_drawn()
    {
        var svg = MermaidSvg.TryRender("sequenceDiagram\n  A->>B: <b>1 & 2</b>");
        Assert.Contains("&lt;b&gt;1 &amp; 2&lt;/b&gt;", svg);
        Assert.Null(MermaidSvg.TryRender("pie title Pets\n \"Dogs\" : 5"));
        Assert.Null(MermaidSvg.TryRender(""));
    }

    [Fact]
    public void The_pdf_gets_a_figure_and_word_keeps_the_source_with_a_plain_caption()
    {
        var md = "Intro.\n\n```mermaid\n" + Sequence.Replace("        ", "") + "\n```\n";
        var pdf = DocExportService.RenderDiagrams(md, "BuildRequestJson", svg: true);
        Assert.Contains("```{=html}", pdf);
        Assert.Contains("<figure class=\"diagram\"><svg", pdf);
        Assert.DoesNotContain("interactive render", pdf);

        var docx = DocExportService.RenderDiagrams(md, "BuildRequestJson", svg: false);
        Assert.DoesNotContain("<figure", docx);
        Assert.Contains("sequenceDiagram", docx);
        Assert.Contains("Diagram source — BuildRequestJson", docx);
    }

    [Theory]
    [InlineData("see [Ultimate_Comprehensive_Travel_Insurance_Raterv1.xlsm/TravelRaterApi.bas:L39–L89].", "see [TravelRaterApi.bas L39–89]{.cite}.")]
    [InlineData("at [Book.xlsm/Module1.bas:L9]", "at [Module1.bas L9]{.cite}")]
    [InlineData("two [A.xlsm/X.bas:L1–L2] [A.xlsm/X.bas:L5-L6]", "two [X.bas L1–2]{.cite} [X.bas L5–6]{.cite}")]
    [InlineData("a link [see below](#x) and a list [1, 2]", "a link [see below](#x) and a list [1, 2]")]
    public void A_long_citation_becomes_a_short_one_and_other_brackets_are_left_alone(string input, string expected) =>
        Assert.Equal(expected, DocExportService.ShortenCitations(input));

    private static DocSection Sec(string kind, string state, string md, string? module = null) => new()
    {
        Id = Guid.NewGuid(), SectionKind = kind, State = state, RenderedMarkdown = md, ModuleName = module, CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void The_combined_document_carries_the_requirements_and_states_the_draft_status_once()
    {
        var sections = new List<DocSection>
        {
            Sec("overview", "DRAFT", "# Overview\n\n## What it does\n\nIt quotes."),
            Sec("module", "SIGNED", "# Mod\n\nBody [B.xlsm/M.bas:L1–L2].", "Mod"),
            Sec("business-rule", "DRAFT", "# Rule 1\n\nText."),
            Sec("functional-requirement", "DRAFT", "# FR-001\n\nThe system shall."),
            Sec("capability-map", "DRAFT", "# Quote\n\nA capability."),
        };
        var md = DocExportService.CombineMarkdown(new Corpus { Name = "Demo workbook" }, sections);

        Assert.Contains("# Business Capabilities", md);
        Assert.Contains("# Functional Requirements", md);
        Assert.Contains("| **Requirements** |", md);                 // listed in "how it is organised"
        Assert.Contains("**1 of 4** sections are reviewed and signed off", md);
        Assert.Equal(0, CountOf(md, "**Draft**"));                    // no banner after each draft section
        Assert.Contains("[M.bas L1–2]{.cite}", md);
        Assert.DoesNotContain("B.xlsm/M.bas", md);
    }

    [Fact]
    public void Without_requirements_the_document_does_not_mention_them()
    {
        var md = DocExportService.CombineMarkdown(new Corpus { Name = "Demo" }, new List<DocSection> { Sec("module", "DRAFT", "# M\n\nBody", "M") });
        Assert.DoesNotContain("| **Requirements** |", md);
        Assert.DoesNotContain("# Functional Requirements", md);
    }

    [Fact]
    public void A_signed_section_that_went_out_of_date_is_still_flagged_where_it_appears()
    {
        var md = DocExportService.CombineMarkdown(new Corpus { Name = "Demo" }, new List<DocSection> { Sec("module", "STALE", "# M\n\nBody", "M") });
        Assert.Contains("Out of date", md);
    }

    /// <summary>Local only: draws every .mmd file in ASTRA_MMD_DIR to a .svg beside it, so the pictures can be looked at.</summary>
    [Fact]
    public void Local_render_of_diagram_samples()
    {
        var dir = Environment.GetEnvironmentVariable("ASTRA_MMD_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*.mmd"))
        {
            var svg = MermaidSvg.TryRender(File.ReadAllText(f));
            File.WriteAllText(Path.ChangeExtension(f, ".svg"), svg ?? "<svg xmlns='http://www.w3.org/2000/svg'><text y='20'>NOT DRAWN</text></svg>");
        }
    }

    private static int CountOf(string text, string needle)
    {
        var n = 0; var i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
