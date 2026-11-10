using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Astra.Api.Evidence;

/// <summary>One statement of a parsed script: where it is, and what it defines if it defines something named.</summary>
internal sealed record SqlStatementSpan(int StartLine, int EndLine, SqlObject? Object);

/// <summary>
/// Reads T-SQL with Microsoft's own parser (ScriptDom), so what a procedure reads, writes and calls is
/// what the language says, not what a pattern guesses. Returns null when the text does not parse; the
/// caller then reads it by pattern and says so.
/// </summary>
internal static class SqlScriptDomReader
{
    /// <param name="text">A batch, or one statement's worth of a script.</param>
    /// <param name="lineOffset">How many lines of the file come before <paramref name="text"/>.</param>
    public static List<SqlStatementSpan>? TryRead(string text, int lineOffset, out string? error)
    {
        error = null;
        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(text);
            var fragment = parser.Parse(reader, out var errors);
            if (errors.Count > 0)
            {
                error = $"line {errors[0].Line + lineOffset}: {errors[0].Message}";
                return null;
            }

            var spans = new List<SqlStatementSpan>();
            if (fragment is TSqlScript script)
            {
                foreach (var batch in script.Batches)
                    foreach (var statement in batch.Statements)
                    {
                        var (start, end) = Lines(statement);
                        spans.Add(new SqlStatementSpan(start + lineOffset, end + lineOffset, Describe(statement)));
                    }
            }
            return spans;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A statement ScriptDom reads but this code does not expect is as good as one it cannot read.
            error = ex.Message;
            return null;
        }
    }

    // ── what a statement defines ────────────────────────────────────────

    private static SqlObject? Describe(TSqlStatement statement)
    {
        switch (statement)
        {
            case CreateTableStatement create:
            {
                var table = New("table", create.SchemaObjectName);
                ReadDefinition(table, create.Definition);
                return table;
            }
            case AlterTableAddTableElementStatement alter:
            {
                var table = New("alter-table", alter.SchemaObjectName, table: alter.SchemaObjectName);
                ReadDefinition(table, alter.Definition);
                return table;
            }
            case CreateIndexStatement index:
            {
                var onTable = Render(index.OnName);
                var o = new SqlObject { Kind = "index", Name = index.Name?.Value ?? "", Table = onTable, Schema = SchemaOf(index.OnName) };
                if (index.Unique)
                    o.Keys.Add(new SqlKey(index.Name?.Value, "unique", ColumnNames(index.Columns)));
                return o;
            }
            case ViewStatementBody view:
            {
                var o = New("view", view.SchemaObjectName);
                var refs = new RefVisitor();
                view.SelectStatement?.Accept(refs);
                refs.CopyTo(o);
                return o;
            }
            case ProcedureStatementBody proc when proc.ProcedureReference?.Name is { } name:
            {
                var o = New("procedure", name);
                foreach (var p in proc.Parameters)
                    o.Parameters.Add(new SqlParameter(p.VariableName?.Value ?? "", p.DataType is null ? "" : TextOf(p.DataType)));
                var refs = new RefVisitor();
                proc.StatementList?.Accept(refs);
                refs.CopyTo(o);
                return o;
            }
            case FunctionStatementBody function when function.Name is not null:
            {
                var o = New("function", function.Name);
                foreach (var p in function.Parameters)
                    o.Parameters.Add(new SqlParameter(p.VariableName?.Value ?? "", p.DataType is null ? "" : TextOf(p.DataType)));
                return o;
            }
            case TriggerStatementBody trigger when trigger.Name is not null:
                return New("trigger", trigger.Name, table: trigger.TriggerObject?.Name);
            default:
                return null;
        }
    }

    private static SqlObject New(string kind, SchemaObjectName name, SchemaObjectName? table = null) => new()
    {
        Kind = kind,
        Name = Render(name),
        Schema = SchemaOf(name),
        Table = table is null ? null : Render(table),
    };

    private static void ReadDefinition(SqlObject table, TableDefinition? definition)
    {
        if (definition is null) return;

        foreach (var column in definition.ColumnDefinitions)
        {
            var name = column.ColumnIdentifier?.Value ?? "";
            var nullable = column.Constraints.OfType<NullableConstraintDefinition>().FirstOrDefault()?.Nullable ?? true;
            var type = column.DataType is not null ? TextOf(column.DataType) : column.ComputedColumnExpression is not null ? "computed" : "";
            var inline = column.Constraints.OfType<UniqueConstraintDefinition>().ToList();
            var isPrimary = inline.Any(u => u.IsPrimaryKey);
            table.Columns.Add(new SqlColumn(name, type, nullable && !isPrimary, isPrimary));

            foreach (var unique in inline)
                table.Keys.Add(new SqlKey(unique.ConstraintIdentifier?.Value, unique.IsPrimaryKey ? "primary" : "unique", new[] { name }));
            foreach (var fk in column.Constraints.OfType<ForeignKeyConstraintDefinition>())
                table.ForeignKeys.Add(ForeignKey(fk, new[] { name }));
        }

        foreach (var constraint in definition.TableConstraints)
        {
            switch (constraint)
            {
                case UniqueConstraintDefinition unique:
                    table.Keys.Add(new SqlKey(unique.ConstraintIdentifier?.Value, unique.IsPrimaryKey ? "primary" : "unique", ColumnNames(unique.Columns)));
                    break;
                case ForeignKeyConstraintDefinition fk:
                    table.ForeignKeys.Add(ForeignKey(fk, fk.Columns.Select(c => c.Value).ToList()));
                    break;
            }
        }

        // A primary key declared after the columns marks its columns.
        var primary = table.Keys.Where(k => k.Kind == "primary").SelectMany(k => k.Columns).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var c = table.Columns[i];
            if (primary.Contains(c.Name) && !c.InPrimaryKey) table.Columns[i] = c with { InPrimaryKey = true, Nullable = false };
        }
    }

    private static SqlForeignKey ForeignKey(ForeignKeyConstraintDefinition fk, IReadOnlyList<string> columns) => new(
        fk.ConstraintIdentifier?.Value,
        columns,
        fk.ReferenceTableName is null ? "" : Render(fk.ReferenceTableName),
        fk.ReferencedTableColumns.Select(c => c.Value).ToList());

    private static IReadOnlyList<string> ColumnNames(IEnumerable<ColumnWithSortOrder> columns) =>
        columns.Select(c => c.Column?.MultiPartIdentifier?.Identifiers.LastOrDefault()?.Value ?? "")
               .Where(n => n.Length > 0)
               .ToList();

    // ── names and positions ─────────────────────────────────────────────

    /// <summary>The name as a person writes it: no brackets, and no schema when it is dbo.</summary>
    public static string Render(SchemaObjectName name)
    {
        var parts = name.Identifiers.Select(i => i.Value).Where(v => !string.IsNullOrEmpty(v)).ToList();
        if (parts.Count == 2 && parts[0].Equals("dbo", StringComparison.OrdinalIgnoreCase)) return parts[1];
        return string.Join('.', parts);
    }

    private static string? SchemaOf(SchemaObjectName name) =>
        name.Identifiers.Count >= 2 ? name.Identifiers[^2].Value : null;

    private static string TextOf(TSqlFragment fragment)
    {
        var tokens = fragment.ScriptTokenStream;
        if (tokens is null || fragment.FirstTokenIndex < 0) return "";
        return string.Concat(Enumerable.Range(fragment.FirstTokenIndex, fragment.LastTokenIndex - fragment.FirstTokenIndex + 1).Select(i => tokens[i].Text)).Trim();
    }

    private static (int Start, int End) Lines(TSqlFragment fragment)
    {
        var tokens = fragment.ScriptTokenStream;
        var last = tokens[fragment.LastTokenIndex];
        // A last token can itself span lines (a string or a comment).
        return (fragment.StartLine, last.Line + (last.Text?.Count(c => c == '\n') ?? 0));
    }

    // ── what a body reads, writes and calls ─────────────────────────────

    private sealed class RefVisitor : TSqlFragmentVisitor
    {
        private readonly List<string> _reads = new();
        private readonly List<string> _writes = new();
        private readonly List<string> _calls = new();
        private readonly List<string> _raises = new();
        private readonly HashSet<object> _writeTargets = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ctes = new(StringComparer.OrdinalIgnoreCase);

        private bool _transaction, _cursor, _loop, _tryCatch;

        public void CopyTo(SqlObject o)
        {
            // UPDATE a SET … FROM Table a: the write target is the alias, the table is behind it.
            foreach (var w in _writes) SqlObject.AddOnce(o.Writes, _aliases.TryGetValue(w, out var real) ? real : w);
            foreach (var r in _reads)
                if (!(IsSingle(r) && _ctes.Contains(r))) SqlObject.AddOnce(o.Reads, r);
            foreach (var c in _calls) SqlObject.AddOnce(o.Calls, c);
            foreach (var r in _raises) SqlObject.AddOnce(o.Raises, r);
            o.UsesTransaction = _transaction;
            o.HasCursor = _cursor;
            o.HasLoop = _loop;
            o.HasTryCatch = _tryCatch;
        }

        private static bool IsSingle(string name) => !name.Contains('.');

        private void MarkWrite(TableReference? target)
        {
            if (target is NamedTableReference named) _writeTargets.Add(named);
        }

        public override void ExplicitVisit(InsertSpecification node) { MarkWrite(node.Target); base.ExplicitVisit(node); }
        public override void ExplicitVisit(UpdateSpecification node) { MarkWrite(node.Target); base.ExplicitVisit(node); }
        public override void ExplicitVisit(DeleteSpecification node) { MarkWrite(node.Target); base.ExplicitVisit(node); }
        public override void ExplicitVisit(MergeSpecification node) { MarkWrite(node.Target); base.ExplicitVisit(node); }

        public override void ExplicitVisit(TruncateTableStatement node)
        {
            if (node.TableName is not null && !Ignorable(node.TableName)) _writes.Add(Render(node.TableName));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            if (node.Into is not null && !Ignorable(node.Into)) _writes.Add(Render(node.Into));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CommonTableExpression node)
        {
            if (node.ExpressionName?.Value is { } name) _ctes.Add(name);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            var name = node.SchemaObject;
            if (name is not null)
            {
                var rendered = Render(name);
                if (node.Alias?.Value is { } alias) _aliases[alias] = rendered;
                if (!Ignorable(name))
                {
                    if (_writeTargets.Contains(node)) _writes.Add(rendered);
                    else _reads.Add(rendered);
                }
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecutableProcedureReference node)
        {
            var name = node.ProcedureReference?.ProcedureReference?.Name;
            if (name is not null && !IsSystemProcedure(name)) _calls.Add(Render(name));
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(WhileStatement node) { _loop = true; base.ExplicitVisit(node); }
        public override void ExplicitVisit(BeginTransactionStatement node) { _transaction = true; base.ExplicitVisit(node); }
        public override void ExplicitVisit(DeclareCursorStatement node) { _cursor = true; base.ExplicitVisit(node); }
        public override void ExplicitVisit(TryCatchStatement node) { _tryCatch = true; base.ExplicitVisit(node); }

        public override void ExplicitVisit(ThrowStatement node)
        {
            if (Literal(node.ErrorNumber) is { } n) _raises.Add(n);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(RaiseErrorStatement node)
        {
            if (Literal(node.FirstParameter) is { } n) _raises.Add(n);
            base.ExplicitVisit(node);
        }

        private static string? Literal(ScalarExpression? e) => e switch
        {
            IntegerLiteral i => i.Value,
            StringLiteral s => s.Value.Length > 80 ? s.Value[..80] : s.Value,
            _ => null,
        };

        /// <summary>Temp tables, the system catalogue and the pseudo-tables of a trigger are not part of the application.</summary>
        private static bool Ignorable(SchemaObjectName name)
        {
            var b = name.BaseIdentifier?.Value ?? "";
            if (b.StartsWith('#') || b.StartsWith('@')) return true;
            var schema = name.Identifiers.Count >= 2 ? name.Identifiers[^2].Value : null;
            if (schema is not null && (schema.Equals("sys", StringComparison.OrdinalIgnoreCase) || schema.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase))) return true;
            return name.Identifiers.Count == 1 && (b.Equals("inserted", StringComparison.OrdinalIgnoreCase) || b.Equals("deleted", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsSystemProcedure(SchemaObjectName name)
        {
            var b = name.BaseIdentifier?.Value ?? "";
            return b.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) || b.StartsWith("xp_", StringComparison.OrdinalIgnoreCase);
        }
    }
}
