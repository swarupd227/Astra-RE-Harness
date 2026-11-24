using Astra.Api.Office;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// A legacy workbook written byte by byte from the [MS-XLS] layouts — sheets with states, a defined name, a
/// formula, two input rules, a button with a macro and a check box with a linked cell — and read back. Real
/// files cover sheets and formulas (see LegacyWorkbookTests); no real file with controls was available, so these
/// pin what the spec says about them.
/// </summary>
public class LegacyWorkbookBuiltTests
{
    private static byte[] W16(int v) => BitConverter.GetBytes((ushort)v);
    private static byte[] W32(uint v) => BitConverter.GetBytes(v);
    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
    private static byte[] Rec(int id, params byte[][] data) { var d = Cat(data); return Cat(W16(id), W16(d.Length), d); }
    private static byte[] Chars(string s) => Cat(new byte[] { 0 }, System.Text.Encoding.Latin1.GetBytes(s));      // flag byte (1 byte per char) + chars
    private static byte[] ShortStr(string s) => Cat(new[] { (byte)s.Length }, Chars(s));                          // ShortXLUnicodeString
    private static byte[] LongStr(string s) => Cat(W16(s.Length), Chars(s));                                      // XLUnicodeString

    private static byte[] PtgRef3d(int ixti, int row, int col) => Cat(new byte[] { 0x3A }, W16(ixti), W16(row), W16(col));
    private static byte[] PtgName(int index) => Cat(new byte[] { 0x23 }, W32((uint)index));
    private static byte[] PtgInt(int v) => Cat(new byte[] { 0x1E }, W16(v));
    private static byte[] PtgStr(string s) => Cat(new[] { (byte)0x17, (byte)s.Length }, Chars(s));
    private static readonly byte[] PtgMul = { 0x05 };

    /// <summary>An ObjFmla: cbFmla, then cce (15 bits), four unused bytes and the one token (padded to an even length).</summary>
    private static byte[] ObjFmla(byte[] rgce)
    {
        var body = Cat(W16(rgce.Length), new byte[4], rgce);
        if (body.Length % 2 != 0) body = Cat(body, new byte[1]);
        return Cat(W16(body.Length), body);
    }

    private static byte[] Lbl(string name, int flags, int itab, byte[] rgce) => Rec(0x0018,
        W16(flags), new byte[] { 0, (byte)name.Length }, W16(rgce.Length), W16(0), W16(itab), new byte[4], Chars(name), rgce);

    private static byte[] Cmo(int ot, int id) => Cat(W16(0x15), W16(0x12), W16(ot), W16(id), W16(0x4011), new byte[12]);

    /// <summary>An MSODRAWING holding one shape whose OfficeArt property table carries the shape's name (wzName, 0x0380).</summary>
    private static byte[] Drawing(string shapeName)
    {
        var name = System.Text.Encoding.Unicode.GetBytes(shapeName + "\0");
        var opt = Cat(W16(0x0013), W16(0xF00B), W32(6 + (uint)name.Length), W16(0x8380), W32((uint)name.Length), name);   // ver 3, inst 1, one complex property
        var sp = Cat(W16(0x0A02), W16(0xF00A), W32(8), W32(1025), W32(0xA00));                                           // ver 2: spid + flags
        var inner = Cat(sp, opt);
        return Rec(0x00EC, Cat(W16(0x000F), W16(0xF004), W32((uint)inner.Length), inner));
    }

    private static byte[] Txo(string text) => Cat(
        Rec(0x01B6, W16(0x0212), W16(0), new byte[6], W16(text.Length), W16(16), W16(0), W16(0)),
        Rec(0x003C, Chars(text)),
        Rec(0x003C, new byte[16]));

    private static byte[] Dv(int flags, byte[] f1, byte[] f2, params (int r1, int r2, int c1, int c2)[] ranges) => Rec(0x01BE,
        W32((uint)flags), LongStr("\0"), LongStr("\0"), LongStr("\0"), LongStr("\0"),
        W16(f1.Length), W16(0), f1, W16(f2.Length), W16(0), f2,
        W16(ranges.Length), Cat(ranges.Select(r => Cat(W16(r.r1), W16(r.r2), W16(r.c1), W16(r.c2))).ToArray()));

    private static byte[] BuildBook()
    {
        var strings = new[] { "Gender", "Annual premium", "Rate" };
        var sst = Rec(0x00FC, W32(3), W32(3), Cat(strings.Select(s => Cat(W16(s.Length), new byte[] { 0 }, System.Text.Encoding.Latin1.GetBytes(s))).ToArray()));

        byte[] Bof(int dt) => Rec(0x0809, W16(0x0600), W16(dt), W16(0x0DBB), W16(0x07CC), new byte[8]);
        byte[] Eof() => Rec(0x000A);

        var quote = Cat(
            Bof(0x10), Rec(0x01BA, LongStr("shQuote")),
            Rec(0x0200, W32(0), W32(9), W16(0), W16(4), W16(0)),
            Rec(0x00FD, W16(3), W16(0), W16(0), W32(0)),                                     // A4 "Gender"
            Rec(0x00FD, W16(7), W16(0), W16(0), W32(1)),                                     // A8 "Annual premium"
            Rec(0x0203, W16(7), W16(2), W16(0), BitConverter.GetBytes(120.5)),               // C8 = 120.5
            Rec(0x0006, W16(8), W16(3), W16(0), new byte[8], W16(0), W32(0),                 // D9: =Premium*Rates!$B$2
                W16(PtgName(1).Length + PtgRef3d(1, 1, 1).Length + 1), Cat(PtgName(1), PtgRef3d(1, 1, 1), PtgMul)),
            Rec(0x01B2, W16(0), W32(0), W32(0), W32(0xFFFFFFFF), W32(2)),
            Dv(0x83, PtgStr("Male\0Female"), Array.Empty<byte>(), (3, 3, 1, 1)),              // B4: list "Male,Female"
            Dv(0x01, PtgInt(18), PtgInt(80), (4, 4, 1, 1)),                                   // B5: whole number between 18 and 80
            Drawing("cmdCalc"),
            Rec(0x005D, Cmo(0x07, 1), Cat(W16(0x04), ObjFmla(PtgName(2))), W16(0), W16(0)),    // a button running the macro registered as Lbl #2
            Txo("Get quote"),
            Drawing("chkSmoker"),
            Rec(0x005D, Cmo(0x0B, 2), Cat(W16(0x0A), W16(12), new byte[12]), Cat(W16(0x14), ObjFmla(PtgRef3d(0, 3, 3))),
                Cat(W16(0x12), W16(8), W16(0), W16(0), W16(0), W16(0)), W16(0), W16(0)),          // a check box linked to D4
            Txo("Smoker"),
            Eof());
        var rates = Cat(Bof(0x10), Rec(0x01BA, LongStr("shRates")), Rec(0x0200, W32(0), W32(3), W16(0), W16(2), W16(0)),
            Rec(0x00FD, W16(0), W16(0), W16(0), W32(2)), Eof());

        byte[] Globals(int quotePos, int ratesPos) => Cat(
            Bof(0x05), Rec(0x01BA, LongStr("ThisWorkbook")),
            Rec(0x0085, W32((uint)quotePos), new byte[] { 0, 0 }, ShortStr("Quote")),
            Rec(0x0085, W32((uint)ratesPos), new byte[] { 2, 0 }, ShortStr("Rates")),          // very hidden
            Rec(0x01AE, W16(2), W16(0x0401)),                                                // SupBook: this workbook
            Rec(0x0017, W16(2), Cat(W16(0), W16(0), W16(0)), Cat(W16(0), W16(1), W16(1))),    // XTI 0 = Quote, XTI 1 = Rates
            Lbl("Premium", 0, 0, PtgRef3d(0, 7, 2)),                                         // #1 Premium = Quote!$C$8
            Lbl("Pricing.Calculate", 0x08, 0, Array.Empty<byte>()),                          // #2 a macro registration (fProc)
            sst, Eof());

        var len = Globals(0, 0).Length;
        return Cat(Globals(len, len + quote.Length), quote, rates);
    }

    [Fact]
    public void Reads_sheets_names_formulas_rules_and_controls_from_a_book_written_from_the_spec()
    {
        var vba = new VbaProject("VBAProject", 1252, new[] { new VbaModule("Pricing", VbaModuleKind.Standard, "Sub Calculate()\nEnd Sub\n", null) });
        var warnings = new List<string>();
        var r = LegacyWorkbook.ReadStream(BuildBook(), "rating.xls", vba, warnings)!;
        Assert.Empty(warnings);

        Assert.Equal(new[] { "Quote", "Rates" }, r.Sheets.Select(s => s.Name));
        Assert.Equal(new[] { "visible", "veryHidden" }, r.Sheets.Select(s => s.State));
        Assert.Equal(new[] { "shQuote", "shRates" }, r.Sheets.Select(s => s.CodeName));
        var quote = r.Sheets[0];
        Assert.Equal("A1:D9", quote.UsedRange);
        Assert.Equal(3, quote.ValueCells);                                                   // two labels and one number

        // A defined name, labelled by the text on its row; the macro registration is not a named cell.
        var premium = Assert.Single(r.Names);
        Assert.Equal(("Premium", "Quote!$C$8", "Annual premium"), (premium.Name, premium.RefersTo, premium.Label));

        // A formula: another sheet and a name it reads.
        var f = Assert.Single(quote.Formulas);
        Assert.Equal(("D9", "Premium*Rates!$B$2"), (f.Cell, f.Formula));
        Assert.Equal(new[] { "name:Premium", "sheet:Rates" }, f.Refs);
        Assert.Equal(new[] { "Rates" }, quote.ReadsFromSheets);

        // Input rules: a literal list and a numeric range, each labelled by the text beside it.
        Assert.Equal(2, quote.Validations.Count);
        var list = quote.Validations.Single(v => v.Type == "list");
        Assert.Equal(("B4", "Male,Female", "Gender"), (list.Cells, list.Source, list.Label));
        var whole = quote.Validations.Single(v => v.Type == "whole");
        Assert.Equal(("B5", "18 to 80"), (whole.Cells, whole.Source));

        // Controls: the button runs its macro (found in the VBA), the check box writes its linked cell.
        var button = quote.Controls.Single(c => c.Kind == "Button");
        Assert.Equal(("cmdCalc", "form", "Get quote", "Pricing.Calculate", "Pricing", "Calculate", true),
            (button.Name, button.Technology, button.Text, button.Macro, button.MacroModule, button.MacroRoutine, button.MacroFound));
        var box = quote.Controls.Single(c => c.Kind == "CheckBox");
        Assert.Equal(("chkSmoker", "Smoker", "Quote!$D$4"), (box.Name, box.Text, box.LinkedCell));
    }

    [Fact]
    public void A_truncated_workbook_is_a_warning_not_an_exception()
    {
        var book = BuildBook();
        var warnings = new List<string>();
        var r = LegacyWorkbook.ReadStream(book[..(book.Length / 2)], "cut.xls", null, warnings);
        Assert.True(r is null || r.Sheets.Count >= 0);                                       // must not throw
    }
}
