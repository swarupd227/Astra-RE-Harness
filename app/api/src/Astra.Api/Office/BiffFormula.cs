using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Astra.Api.Office;

/// <summary>
/// What a BIFF8 formula refers to, resolved for rendering: the workbook's sheets, defined names and external
/// references (the XTI list), with the cell the formula sits in (for relative references).
/// </summary>
internal sealed class BiffFormulaContext
{
    public IReadOnlyList<string> SheetNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DefinedNames { get; init; } = Array.Empty<string>();
    /// <summary>ExternSheet: (supbook index, first sheet, last sheet) per reference index.</summary>
    public IReadOnlyList<(int SupBook, int First, int Last)> Xti { get; init; } = Array.Empty<(int, int, int)>();
    /// <summary>Per SupBook: null for "this workbook", else the external workbook's name and its sheet names.</summary>
    public IReadOnlyList<(bool Self, string? Name, IReadOnlyList<string> Sheets, IReadOnlyList<string> ExternNames)> SupBooks { get; init; }
        = Array.Empty<(bool, string?, IReadOnlyList<string>, IReadOnlyList<string>)>();
    public int Row { get; init; }
    public int Col { get; init; }

    /// <summary>The same workbook context for a formula in another cell (relative references resolve against it).</summary>
    public BiffFormulaContext At(int row, int col) => new()
    {
        SheetNames = SheetNames, DefinedNames = DefinedNames, Xti = Xti, SupBooks = SupBooks, Row = row, Col = col,
    };
}

/// <summary>
/// Turns a BIFF8 parsed formula (the Ptg tokens of [MS-XLS] 2.2.2) into the text Excel shows, enough to
/// read what a formula, a defined name or a control's link points at. A token it does not know ends the
/// rendering with a marker and <see cref="Complete"/> = false, never a wrong guess. Token byte sizes follow
/// the spec's catalog; the function table is Excel's.
/// </summary>
internal static class BiffFormula
{
    public sealed record Rendered(string Text, bool Complete);

    private static readonly string[] BinaryOps = { "+", "-", "*", "/", "^", "&", "<", "<=", "=", ">=", ">", "<>", " ", ",", ":" };

    public static Rendered Render(ReadOnlySpan<byte> rgce, BiffFormulaContext ctx)
    {
        var stack = new List<string>();
        var p = 0;
        var complete = true;
        try
        {
            while (p < rgce.Length)
            {
                var b = rgce[p];
                // Value/reference/array variants (0x20.. 0x7F) share a code in the low five bits.
                var id = b >= 0x20 ? (b & 0x1F) + 0x20 : b;
                switch (id)
                {
                    case 0x01: stack.Add($"(shared formula at {Cell(U16(rgce, p + 1), U16(rgce, p + 3))})"); p += 5; break;       // PtgExp
                    case 0x02: stack.Add("(data table)"); p += 5; break;                                                           // PtgTbl
                    case >= 0x03 and <= 0x11:
                    {
                        var r = Pop(stack); var l = Pop(stack);
                        stack.Add(l + BinaryOps[id - 3] + r);
                        p += 1; break;
                    }
                    case 0x12: stack.Add("+" + Pop(stack)); p += 1; break;
                    case 0x13: stack.Add("-" + Pop(stack)); p += 1; break;
                    case 0x14: stack.Add(Pop(stack) + "%"); p += 1; break;
                    case 0x15: stack.Add("(" + Pop(stack) + ")"); p += 1; break;
                    case 0x16: stack.Add(""); p += 1; break;                                                                        // PtgMissArg
                    case 0x17:                                                                                                      // PtgStr
                    {
                        var cch = rgce[p + 1]; var wide = (rgce[p + 2] & 1) != 0;
                        var n = wide ? cch * 2 : cch;
                        var s = wide ? Encoding.Unicode.GetString(rgce.Slice(p + 3, n)) : Encoding.Latin1.GetString(rgce.Slice(p + 3, n));
                        stack.Add("\"" + s.Replace("\"", "\"\"") + "\"");
                        p += 3 + n; break;
                    }
                    case 0x19:                                                                                                      // PtgAttr
                    {
                        var kind = rgce[p + 1];
                        if (kind == 0x10) { stack.Add("SUM(" + Pop(stack) + ")"); p += 4; }                                         // AttrSum
                        else if (kind == 0x04) { p += 4 + 2 * (U16(rgce, p + 2) + 1); }                                             // AttrChoose
                        else if (kind is 0x01 or 0x02 or 0x08 or 0x20 or 0x21 or 0x40 or 0x41) p += 4;                              // semi, if, goto, baxcel, space
                        else { complete = false; p = rgce.Length; }
                        break;
                    }
                    case 0x1C: stack.Add(ErrorText(rgce[p + 1])); p += 2; break;
                    case 0x1D: stack.Add(rgce[p + 1] != 0 ? "TRUE" : "FALSE"); p += 2; break;
                    case 0x1E: stack.Add(U16(rgce, p + 1).ToString(CultureInfo.InvariantCulture)); p += 3; break;
                    case 0x1F: stack.Add(BitConverter.ToDouble(rgce.Slice(p + 1, 8)).ToString("R", CultureInfo.InvariantCulture)); p += 9; break;
                    case 0x20: stack.Add("{array}"); p += 8; break;                                                                // PtgArray (data is in rgcb)
                    case 0x21:                                                                                                      // PtgFunc
                    {
                        var ft = U16(rgce, p + 1);
                        var (name, min, max) = BiffFunctions.ByIndex.TryGetValue(ft, out var f) ? f : ($"FUNCTION{ft}", 0, 0);
                        var n = min == max ? min : Math.Min(min, stack.Count);
                        stack.Add(Call(name, stack, n));
                        p += 3; break;
                    }
                    case 0x22:                                                                                                      // PtgFuncVar
                    {
                        var n = rgce[p + 1] & 0x7F; var ft = U16(rgce, p + 2) & 0x7FFF;
                        if (ft == 255)                                                                                              // a user-defined function: its name is the first argument
                        {
                            var args = PopN(stack, n);
                            stack.Add(args.Count == 0 ? "UDF()" : $"{args[0]}({string.Join(",", args.Skip(1))})");
                        }
                        else stack.Add(Call(BiffFunctions.ByIndex.TryGetValue(ft, out var f2) ? f2.Name : $"FUNCTION{ft}", stack, n));
                        p += 4; break;
                    }
                    case 0x23: stack.Add(NameAt(ctx, (int)U32(rgce, p + 1))); p += 5; break;                                       // PtgName
                    case 0x24: stack.Add(Ref(ctx, rgce, p + 1, null, relative: false)); p += 5; break;
                    case 0x25: stack.Add(Area(ctx, rgce, p + 1, null, relative: false)); p += 9; break;
                    case 0x26 or 0x27 or 0x28: p += 7; break;                                                                      // PtgMem*: marker; its sub-expression follows as ordinary tokens
                    case 0x29: p += 3; break;                                                                                       // PtgMemFunc
                    case 0x2A: stack.Add("#REF!"); p += 5; break;
                    case 0x2B: stack.Add("#REF!"); p += 9; break;
                    case 0x2C: stack.Add(Ref(ctx, rgce, p + 1, null, relative: true)); p += 5; break;                               // PtgRefN
                    case 0x2D: stack.Add(Area(ctx, rgce, p + 1, null, relative: true)); p += 9; break;                              // PtgAreaN
                    case 0x39:                                                                                                      // PtgNameX
                    {
                        var ixti = U16(rgce, p + 1); var idx = (int)U32(rgce, p + 3);
                        stack.Add(ExternNameAt(ctx, ixti, idx)); p += 7; break;
                    }
                    case 0x3A: stack.Add(Prefix(ctx, U16(rgce, p + 1)) + Ref(ctx, rgce, p + 3, null, relative: false)); p += 7; break;   // PtgRef3d
                    case 0x3B: stack.Add(Prefix(ctx, U16(rgce, p + 1)) + Area(ctx, rgce, p + 3, null, relative: false)); p += 11; break; // PtgArea3d
                    case 0x3C: stack.Add("#REF!"); p += 7; break;
                    case 0x3D: stack.Add("#REF!"); p += 11; break;
                    default: complete = false; stack.Add($"(unreadable token 0x{b:X2})"); p = rgce.Length; break;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidOperationException)
        {
            complete = false;
        }
        return new Rendered(stack.Count == 0 ? "" : string.Join(",", stack), complete && stack.Count <= 1);
    }

    private static string Call(string name, List<string> stack, int n) => $"{name}({string.Join(",", PopN(stack, n))})";

    private static List<string> PopN(List<string> stack, int n)
    {
        n = Math.Min(n, stack.Count);
        var args = stack.GetRange(stack.Count - n, n);
        stack.RemoveRange(stack.Count - n, n);
        return args;
    }

    private static string Pop(List<string> stack)
    {
        if (stack.Count == 0) throw new InvalidOperationException("operator with no operand");
        var s = stack[^1]; stack.RemoveAt(stack.Count - 1); return s;
    }

    private static int U16(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(at, 2));
    private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(at, 4));

    /// <summary>A cell reference: row u16 then column (14 bits) with the column-relative (0x4000) and row-relative (0x8000) flags.</summary>
    private static string Ref(BiffFormulaContext ctx, ReadOnlySpan<byte> b, int at, object? _, bool relative)
    {
        var row = (int)U16(b, at); var colWord = U16(b, at + 2);
        return One(ctx, row, colWord, relative);
    }

    private static string Area(BiffFormulaContext ctx, ReadOnlySpan<byte> b, int at, object? _, bool relative)
    {
        var r1 = (int)U16(b, at); var r2 = (int)U16(b, at + 2); var c1 = U16(b, at + 4); var c2 = U16(b, at + 6);
        var first = One(ctx, r1, c1, relative); var last = One(ctx, r2, c2, relative);
        return first == last ? first : first + ":" + last;
    }

    private static string One(BiffFormulaContext ctx, int row, int colWord, bool relative)
    {
        var colRel = (colWord & 0x4000) != 0; var rowRel = (colWord & 0x8000) != 0;
        var col = colWord & 0x3FFF;
        if (relative)
        {
            // PtgRefN/PtgAreaN: relative parts are signed offsets from the formula's own cell.
            if (rowRel) row = (ctx.Row + (short)row) & 0xFFFF;
            if (colRel) col = (ctx.Col + (sbyte)(col & 0xFF)) & 0xFF;
        }
        // A relative part prints without a "$"; an absolute one with it (for RefN the absolute parts hold plain addresses).
        return (colRel ? "" : "$") + ColumnName(col + 1) + (rowRel ? "" : "$") + (row + 1).ToString(CultureInfo.InvariantCulture);
    }

    public static string ColumnName(int n)
    {
        var s = "";
        while (n > 0) { var r = (n - 1) % 26; s = (char)('A' + r) + s; n = (n - 1) / 26; }
        return s;
    }

    public static string Cell(int row, int col) => ColumnName(col + 1) + (row + 1).ToString(CultureInfo.InvariantCulture);

    private static string NameAt(BiffFormulaContext ctx, int oneBased) =>
        oneBased >= 1 && oneBased <= ctx.DefinedNames.Count ? ctx.DefinedNames[oneBased - 1] : $"(name {oneBased})";

    private static string ExternNameAt(BiffFormulaContext ctx, int ixti, int oneBased)
    {
        if (ixti < ctx.Xti.Count && ctx.Xti[ixti].SupBook < ctx.SupBooks.Count)
        {
            var sb = ctx.SupBooks[ctx.Xti[ixti].SupBook];
            if (oneBased >= 1 && oneBased <= sb.ExternNames.Count) return sb.ExternNames[oneBased - 1];
            // A reference to "this workbook" has no external names of its own: the index is the workbook's Nth defined name.
            if (sb.Self) return NameAt(ctx, oneBased);
        }
        return $"(external name {oneBased})";
    }

    /// <summary><c>Sheet!</c>, <c>'My Sheet'!</c>, <c>Sheet1:Sheet3!</c> or <c>[Book.xls]Sheet!</c> for a reference index.</summary>
    private static string Prefix(BiffFormulaContext ctx, int ixti)
    {
        if (ixti >= ctx.Xti.Count) return "#REF!";
        var (sup, first, last) = ctx.Xti[ixti];
        if (sup >= ctx.SupBooks.Count) return "#REF!";
        var book = ctx.SupBooks[sup];
        var sheets = book.Self ? ctx.SheetNames : book.Sheets;
        if (first == -2) return "";                                          // the whole workbook: a name's scope, no sheet
        string Sheet(int i) => i >= 0 && i < sheets.Count ? sheets[i] : "#REF";
        var text = first == last || last < 0 ? Sheet(first) : Sheet(first) + ":" + Sheet(last);
        if (!book.Self) text = "[" + (book.Name is null ? "?" : book.Name.Replace('\\', '/').Split('/').Last()) + "]" + text;
        return Quote(text) + "!";
    }

    private static string Quote(string sheet) =>
        sheet.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or ':' or '[' or ']') && !char.IsDigit(sheet[0]) ? sheet : "'" + sheet.Replace("'", "''") + "'";

    private static string ErrorText(byte e) => e switch
    {
        0x00 => "#NULL!", 0x07 => "#DIV/0!", 0x0F => "#VALUE!", 0x17 => "#REF!", 0x1D => "#NAME?", 0x24 => "#NUM!", 0x2A => "#N/A", _ => "#ERROR!",
    };
}
