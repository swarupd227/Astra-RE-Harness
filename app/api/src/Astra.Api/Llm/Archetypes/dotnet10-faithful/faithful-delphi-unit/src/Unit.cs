// SHAPE EXEMPLAR — never shipped in a generated package.
//
// Faithful 1:1 conversion of a small Delphi unit, showing every convention
// the converter follows. The Delphi source it mirrors:
//
//   unit IdCounter;
//   interface
//   uses IdException;
//   type
//     TIdCounterMode = (cmClamp, cmRaise);
//     TIdCounter = class(TObject)
//     private
//       FValue: Integer;
//       FMax: Integer;
//       FMode: TIdCounterMode;
//     protected
//       procedure CheckRange(AValue: Integer);
//     public
//       constructor Create(AMax: Integer; AMode: TIdCounterMode = cmClamp);
//       procedure Increment;
//       procedure Reset;
//       property Value: Integer read FValue;
//       property Max: Integer read FMax;
//     end;
//   function ClampTo(AValue, AMax: Integer): Integer;
//   implementation
//   ... (lines 30-70)
//
// Conventions, in the order a reviewer meets them:
//   1. One unit → one file, `namespace Faithful.<Unit>`. Types keep their
//      Delphi names exactly (TIdCounter, TIdCounterMode); so do methods,
//      properties and fields (FValue). Parameters and locals become
//      lowerCamelCase (AValue → aValue, LCount → lCount).
//   2. Members appear in the order the implementation section defines them.
//   3. Free procedures/functions live on `public static class <Unit>`.
//   4. Every method/constructor/property carries [SourceRoutine(unit, routine,
//      lineStart, lineEnd)]; every type carries [SourceUnit(path)].
//   5. Claims from a signed spec are honoured and cited with [SpecClaim("ID")]
//      — only ids that exist in the spec, never invented ones.
//   6. Delphi constructs map to their idiomatic counterpart, with the
//      original shown in a trailing comment where the mapping is not obvious:
//      raise → throw · try/finally → try/finally · Free/FreeAndNil → Dispose
//      · property read/write → C# property · default parameters kept ·
//      enum (a, b) → enum · set of → [Flags] enum.
//   7. Types taken from another unit (`uses`) are not guessed at: they get a
//      minimal stub under src/Stubs/<Unit>.cs with a TODO, so the package
//      compiles today and the stub is replaced when that unit is converted.
//      (Shown inline here only because the exemplar is a single file.)
namespace Faithful.IdCounter;

using Faithful.Provenance;

[SourceUnit("Lib/Core/IdCounter.pas")]
public enum TIdCounterMode
{
    cmClamp,
    cmRaise,
}

[SourceUnit("Lib/Core/IdCounter.pas")]
public class TIdCounter
{
    private int FValue;
    private readonly int FMax;
    private readonly TIdCounterMode FMode;

    [SourceRoutine("IdCounter.pas", "TIdCounter.Create", 32, 38)]
    public TIdCounter(int aMax, TIdCounterMode aMode = TIdCounterMode.cmClamp)
    {
        // inherited Create; — TObject has no state to initialise.
        FMax = aMax;
        FMode = aMode;
        FValue = 0;
    }

    [SourceRoutine("IdCounter.pas", "TIdCounter.CheckRange", 40, 46)]
    [SpecClaim("INV-1")]
    protected void CheckRange(int aValue)
    {
        if (aValue > FMax && FMode == TIdCounterMode.cmRaise)
        {
            // raise EIdException.CreateFmt('Value %d exceeds %d', [AValue, FMax]);
            throw new EIdException($"Value {aValue} exceeds {FMax}");
        }
    }

    [SourceRoutine("IdCounter.pas", "TIdCounter.Increment", 48, 56)]
    [SpecClaim("INV-1")]
    [SpecClaim("EC-1")]
    public void Increment()
    {
        var lNext = FValue + 1;
        CheckRange(lNext);
        FValue = IdCounter.ClampTo(lNext, FMax);
    }

    [SourceRoutine("IdCounter.pas", "TIdCounter.Reset", 58, 61)]
    public void Reset()
    {
        FValue = 0;
    }

    [SourceRoutine("IdCounter.pas", "TIdCounter.Value", 22, 22)]
    public int Value => FValue;          // property Value: Integer read FValue;

    [SourceRoutine("IdCounter.pas", "TIdCounter.Max", 23, 23)]
    public int Max => FMax;              // property Max: Integer read FMax;
}

/// <summary>Free routines of unit IdCounter.</summary>
[SourceUnit("Lib/Core/IdCounter.pas")]
public static class IdCounter
{
    [SourceRoutine("IdCounter.pas", "ClampTo", 63, 68)]
    public static int ClampTo(int aValue, int aMax)
    {
        if (aValue > aMax) return aMax;
        return aValue;
    }
}

// ── Stub for a type this unit takes from IdException.pas ─────────────────
// In a generated package this lives in src/Stubs/IdException.cs.
// TODO(faithful): stub for IdException.EIdException — replace when IdException.pas is converted.
[SourceUnit("Lib/Core/IdException.pas")]
public class EIdException(string message) : Exception(message);
