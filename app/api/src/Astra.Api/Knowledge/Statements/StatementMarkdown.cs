using System.Text;

namespace Astra.Api.Knowledge;

/// <summary>
/// Markdown derived from a statement document, for export and for reading as text. Deterministic: the same
/// document always renders the same way. A statement is followed by its citations in square brackets and, when
/// it is not plainly supported, by what it is (<c>unverified</c>, <c>conflict</c>).
/// </summary>
public static class StatementMarkdown
{
    public static string Render(StatementDocument doc)
    {
        var sb = new StringBuilder();
        if (doc.Title.Length > 0) sb.Append("# ").Append(doc.Title).Append("\n\n");

        foreach (var section in doc.Sections)
        {
            if (section.Heading.Length > 0) sb.Append("## ").Append(section.Heading).Append("\n\n");
            foreach (var item in section.Items) AppendItem(sb, item);
            sb.Append('\n');
        }

        if (doc.Conflicts.Count > 0)
        {
            sb.Append("## Conflicts\n\n");
            var text = doc.Items.ToDictionary(i => i.Id, i => i.Text, StringComparer.Ordinal);
            foreach (var c in doc.Conflicts)
            {
                var about = text.TryGetValue(c.ItemId, out var t) ? t : c.ItemId;
                sb.Append("- ").Append(about);
                if (c.Summary.Length > 0) sb.Append(" — ").Append(c.Summary);
                sb.Append(' ').Append(Cites(c.EvidenceIds)).Append('\n');
            }
            sb.Append('\n');
        }

        if (doc.OpenQuestions.Count > 0)
        {
            sb.Append("## Open questions\n\n");
            foreach (var q in doc.OpenQuestions)
            {
                sb.Append("- ").Append(q.Text);
                if (q.Citations is { Count: > 0 }) sb.Append(' ').Append(Cites(q.Citations));
                sb.Append('\n');
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    private static void AppendItem(StringBuilder sb, StatementItem item)
    {
        sb.Append("- ").Append(item.Text);
        if (item.Citations.Count > 0) sb.Append(' ').Append(Cites(item.Citations));
        switch (item.Status)
        {
            case StatementStatus.Unverified: sb.Append(" _(unverified)_"); break;
            case StatementStatus.Conflict: sb.Append(" _(conflict)_"); break;
        }
        sb.Append('\n');
    }

    private static string Cites(IEnumerable<string> ids) => string.Join(' ', ids.Select(id => $"[{id}]"));
}
