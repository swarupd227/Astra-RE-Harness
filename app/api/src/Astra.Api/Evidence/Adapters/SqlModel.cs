namespace Astra.Api.Evidence;

internal sealed record SqlColumn(string Name, string Type, bool Nullable, bool InPrimaryKey);

/// <summary>A primary or unique key: <c>Kind</c> is "primary" or "unique".</summary>
internal sealed record SqlKey(string? Name, string Kind, IReadOnlyList<string> Columns);

internal sealed record SqlForeignKey(string? Name, IReadOnlyList<string> Columns, string References, IReadOnlyList<string> ReferencedColumns);

internal sealed record SqlParameter(string Name, string Type);

/// <summary>
/// One statement of a script that defines or changes something named, with the lines it occupies.
/// <see cref="Kind"/> is table, view, procedure, function, index, trigger or alter-table.
/// </summary>
internal sealed class SqlObject
{
    public string Kind { get; init; } = "";

    /// <summary>The name a person writes: no brackets, no schema when it is dbo.</summary>
    public string Name { get; init; } = "";
    public string? Schema { get; init; }

    /// <summary>For an index or an ALTER TABLE: the table it is on.</summary>
    public string? Table { get; init; }

    public int StartLine { get; set; }
    public int EndLine { get; set; }

    /// <summary>True when ScriptDom read the statement; false when it was read by pattern (links from it are medium confidence).</summary>
    public bool Parsed { get; init; } = true;

    public List<SqlColumn> Columns { get; } = new();
    public List<SqlKey> Keys { get; } = new();
    public List<SqlForeignKey> ForeignKeys { get; } = new();
    public List<SqlParameter> Parameters { get; } = new();

    public List<string> Reads { get; } = new();
    public List<string> Writes { get; } = new();
    public List<string> Calls { get; } = new();
    public List<string> Raises { get; } = new();

    public bool UsesTransaction { get; set; }
    public bool HasCursor { get; set; }
    public bool HasLoop { get; set; }
    public bool HasTryCatch { get; set; }

    public static void AddOnce(List<string> list, string value)
    {
        if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
    }
}
