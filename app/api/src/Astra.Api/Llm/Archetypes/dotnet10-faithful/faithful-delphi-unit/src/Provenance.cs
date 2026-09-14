// Provenance attributes for faithful 1:1 conversions. Every converted type
// and routine says where it came from, so a reviewer can diff a C# method
// against the exact Delphi lines it replaces, and the equivalence sidecar
// can map a test back to the signed claim it covers.
namespace Faithful.Provenance;

/// <summary>The Delphi unit a converted type came from (corpus-relative path).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Interface | AttributeTargets.Delegate)]
public sealed class SourceUnitAttribute(string path) : Attribute
{
    public string Path { get; } = path;
}

/// <summary>The Delphi routine a member was converted from, with its line range in the unit.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property | AttributeTargets.Event)]
public sealed class SourceRoutineAttribute(string unit, string routine, int lineStart, int lineEnd) : Attribute
{
    public string Unit { get; } = unit;
    public string Routine { get; } = routine;
    public int LineStart { get; } = lineStart;
    public int LineEnd { get; } = lineEnd;
}

/// <summary>A signed-spec claim this code honours (closed vocabulary: the spec's own ids).</summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
public sealed class SpecClaimAttribute(string id) : Attribute
{
    public string Id { get; } = id;
}
