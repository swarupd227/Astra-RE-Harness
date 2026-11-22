using System.Security.Cryptography;
using System.Text;
using Astra.Api.Evidence;

namespace Astra.Api.Knowledge;

/// <summary>
/// One thing a claim says about the system, in a form that can be checked against what code, SQL, schedulers and
/// configuration state. <see cref="Predicate"/> is a graph relation (<c>runs</c>, <c>reads</c>, <c>writes</c>,
/// <c>precedes</c> …) between <see cref="Subject"/> and <see cref="Object"/>, or <c>attribute</c>: the subject's
/// attribute named by <see cref="Object"/> (<c>schedule</c>, <c>value</c> …) has the value <see cref="Value"/>.
/// Names are those of things in the inventory, as written in the evidence, or <c>type:name</c>.
/// </summary>
public sealed record ClaimAssertion(string Subject, string Predicate, string? Object = null, string? Value = null);

/// <summary>
/// An atomic statement of what a person said or wrote, cut from one piece of narrative evidence (a window of a
/// transcript, a ticket, a note). <see cref="At"/> says when or which ticket (<c>00:14:32</c>, <c>INC-T-0412</c>).
/// </summary>
public sealed record Claim(string Id, string Text, string EvidenceId, string? At, IReadOnlyList<ClaimAssertion> Assertions);

/// <summary>
/// What the structured evidence says about a claim. <see cref="StructuredEvidenceIds"/> are the pieces of structured
/// evidence the verdict rests on; for a conflict they are the other side of the disagreement.
/// </summary>
public sealed record ClaimVerdict(
    string ClaimId,
    string Status,
    string Reason,
    IReadOnlyList<string> StructuredEvidenceIds,
    IReadOnlyList<string> Entities);

public static class ClaimPredicates
{
    /// <summary>The subject's attribute (named by the assertion's object) has the given value.</summary>
    public const string Attribute = "attribute";

    public static bool IsKnown(string? predicate) =>
        predicate is not null && (predicate == Attribute || EntityVocabulary.Relations.ContainsKey(predicate));
}

public static class ClaimIds
{
    /// <summary>Stable for the same words from the same evidence, so a claim keeps its identity when claims are read again.</summary>
    public static string Stable(string text, string evidenceId)
    {
        var normalised = string.Join(' ', text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(evidenceId + "\n" + normalised));
        return "cl-" + Convert.ToHexString(hash, 0, 5).ToLowerInvariant();
    }
}
