namespace Astra.Api.Llm;

/// <summary>
/// Pre-canned spec/v1 for the CONSUME_ROLL synthetic subroutine. Used by
/// <see cref="MockLlmProvider"/> to produce a deterministic, demo-ready
/// stream — line citations are aligned with the source in
/// <c>Persistence.Seed.ConsumeRollSource</c>.
/// </summary>
public static class CanonicalSpec
{
    public sealed record Citation(string Lines);
    public sealed record Input(string Id, string Name, string Type, string Semantic, Citation[] Citations);
    public sealed record Output(string Id, string Name, string Type, string Semantic, Citation[] Citations);
    public sealed record Invariant(string Id, string Claim, Citation[] Citations, string Confidence);
    public sealed record SideEffect(string Id, string Description, Citation[] Citations);
    public sealed record EdgeCase(string Id, string Description, Citation[] Citations, string Behavior, string Confidence);
    public sealed record OpenQuestion(string Id, string Question, string Status);

    public const string Summary =
        "Posts a roll-consumption event from the wet end. Reads the current roll record via ISAM, " +
        "validates locked / insufficient stock, decrements on-hand linear footage, marks the roll " +
        "DEPLETED if stock drops below MIN_REMAIN, persists the update, and emits a CSC " +
        "inventory-changed notification.";

    public static readonly Input[] Inputs =
    {
        new("in.ROLL_ID",  "ROLL_ID",  "CHARACTER*12", "Unique roll identifier.",            new[]{new Citation("1, 14")}),
        new("in.USED_LF",  "USED_LF",  "REAL",         "Linear feet consumed in this event.", new[]{new Citation("1, 16")}),
        new("in.OPER_ID",  "OPER_ID",  "CHARACTER*8",  "Operator ID for audit.",             new[]{new Citation("1, 15")}),
    };

    public static readonly Output[] Outputs =
    {
        new("out.RESULT_CD", "RESULT_CD", "INTEGER",
            "Result code: 0=ok, 1=not_found, 2=insufficient, 3=locked.",
            new[]{new Citation("1, 17")}),
    };

    public static readonly Invariant[] Invariants =
    {
        new("INV-1",
            "RESULT_CD is set to 1 (not_found) and the routine returns when INV_READ yields IO_STAT ≠ 0.",
            new[]{new Citation("32-35")},
            "high"),
        new("INV-2",
            "When the roll is LOCKED, RESULT_CD is set to 3 and the routine returns without modifying inventory.",
            new[]{new Citation("37-40")},
            "high"),
        new("INV-3",
            "When USED_LF exceeds ON_HAND_LF, RESULT_CD is set to 2 (insufficient) and no write is performed.",
            new[]{new Citation("42-45")},
            "high"),
        new("INV-4",
            "On the success path, NEW_LF = ON_HAND_LF − USED_LF. The arithmetic is a single REAL subtraction with no clamping.",
            new[]{new Citation("47")},
            "high"),
        new("INV-5",
            "If NEW_LF < MIN_REMAIN (12.0), ROLL_STATUS is overwritten to 9 (DEPLETED) before the rewrite.",
            new[]{new Citation("50-52")},
            "high"),
        new("INV-6",
            "Successful consumption performs an ISAM rewrite via INV_WRITE and emits a EMIT_EVENT('INV_CHG', ...).",
            new[]{new Citation("55-62")},
            "high"),
    };

    public static readonly SideEffect[] SideEffects =
    {
        new("SE-1",
            "Rewrites the roll record in INVMASTR (via INV_WRITE) on the success path.",
            new[]{new Citation("55-58")}),
        new("SE-2",
            "Emits an outbound CSC inventory-changed notification with the grade code and new linear footage.",
            new[]{new Citation("61")}),
    };

    public static readonly EdgeCase[] EdgeCases =
    {
        new("EC-1",
            "Roll record cannot be located (INV_READ IO_STAT≠0).",
            new[]{new Citation("32-35")},
            "Returns RESULT_CD=1; no further state is changed.",
            "high"),
        new("EC-2",
            "Roll is locked at the time of consumption.",
            new[]{new Citation("37-40")},
            "Returns RESULT_CD=3; no inventory or status change.",
            "high"),
        new("EC-3",
            "USED_LF strictly greater than ON_HAND_LF.",
            new[]{new Citation("42-45")},
            "Returns RESULT_CD=2; nothing persists.",
            "high"),
        new("EC-4",
            "INV_WRITE returns IO_STAT≠0 after the read succeeded.",
            new[]{new Citation("57-59")},
            "Returns RESULT_CD=1 (not_found) — the source overloads code 1 for both not-found and write-failure.",
            "medium"),
    };

    public static readonly OpenQuestion[] OpenQuestions =
    {
        new("Q-1",
            "USED_LF is not validated as non-negative. A negative USED_LF would INCREASE on-hand stock through " +
            "the subtraction at line 47. Should this case yield RESULT_CD=2, a new error code, or is it a known " +
            "operator-trust assumption? The source does not declare its intent.",
            "unresolved"),
        new("Q-2",
            "MIN_REMAIN = 12.0 (linear feet) is a magic constant. Should this be a per-grade lookup or " +
            "configurable, or is the global threshold operationally correct?",
            "unresolved"),
        new("Q-3",
            "ROLL_STATUS = 9 (DEPLETED) is a magic numeric code. A symbolic enum would be safer; confirming " +
            "that 9 is the intended depletion code with the SME.",
            "unresolved"),
    };
}
