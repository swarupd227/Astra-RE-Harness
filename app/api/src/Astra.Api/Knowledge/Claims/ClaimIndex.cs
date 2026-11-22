namespace Astra.Api.Knowledge;

/// <summary>What the claims pipeline says about a written document: the statements it verified and the disagreements it found.</summary>
public sealed record ClaimFindings(IReadOnlySet<string> VerifiedItemIds, IReadOnlyList<StatementConflict> Conflicts);

/// <summary>
/// Joins a written document to the claims pipeline, purely. A statement matches a claim when it cites the claim's
/// evidence and names every thing the claim is about; a statement that matches a claim the structured evidence
/// supports is <b>verified</b>, and one that matches a claim it contradicts carries a <b>conflict</b> naming the claim's
/// evidence and the structured evidence on the other side. This is what the writers hand the gate as
/// <c>VerifiedItemIds</c> and conflicts.
/// </summary>
public static class ClaimIndex
{
    public static ClaimFindings Apply(StatementDocument document, IReadOnlyList<Claim> claims, IReadOnlyList<ClaimVerdict> verdicts)
    {
        var verdictOf = verdicts.GroupBy(v => v.ClaimId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var verified = new HashSet<string>(StringComparer.Ordinal);
        var conflicts = new List<StatementConflict>();

        foreach (var item in document.Items)
        {
            var text = IdentifierExtractor.Normalise(item.Text);
            foreach (var claim in claims)
            {
                if (!verdictOf.TryGetValue(claim.Id, out var verdict)) continue;
                if (!item.Citations.Contains(claim.EvidenceId, StringComparer.Ordinal)) continue;
                var subjects = claim.Assertions.SelectMany(a => new[] { a.Subject, a.Object ?? "" })
                    .Where(s => !string.IsNullOrWhiteSpace(s) && !IsAttributeName(claim, s))
                    .Select(NameOf).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                if (subjects.Count == 0 || !subjects.All(s => text.Contains(s, StringComparison.Ordinal))) continue;

                if (verdict.Status == StatementStatus.Supported) verified.Add(item.Id);
                else if (verdict.Status == StatementStatus.Conflict && verdict.StructuredEvidenceIds.Count > 0
                         && !conflicts.Any(c => c.ItemId == item.Id))
                    conflicts.Add(new StatementConflict(item.Id, verdict.StructuredEvidenceIds.Prepend(claim.EvidenceId).Distinct(StringComparer.Ordinal).ToList(), verdict.Reason));
            }
        }

        // A statement that conflicts is never also verified.
        verified.ExceptWith(conflicts.Select(c => c.ItemId));
        return new ClaimFindings(verified, conflicts);
    }

    private static bool IsAttributeName(Claim claim, string s) =>
        claim.Assertions.Any(a => a.Predicate == ClaimPredicates.Attribute && string.Equals(a.Object, s, StringComparison.Ordinal) && !string.Equals(a.Subject, s, StringComparison.Ordinal));

    /// <summary><c>job:TLR_EOD_POST</c> and <c>TLR_EOD_POST</c> are the same thing as far as a sentence is concerned.</summary>
    private static string NameOf(string s)
    {
        var i = s.IndexOf(':');
        var bare = i > 0 && i < 16 && !s.Contains("://") && !s.Contains(' ') ? s[(i + 1)..] : s;
        return IdentifierExtractor.Normalise(bare.Trim());
    }
}
