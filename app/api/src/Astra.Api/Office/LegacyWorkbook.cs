using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Astra.Api.Office;

/// <summary>
/// The surface of a legacy <c>.xls</c> workbook (BIFF8, [MS-XLS]) in the same shape as the .xlsx reader gives:
/// sheets (state, VBA code name, used range, value/formula counts), defined names with the label beside their
/// cell, formulas (rendered, with what they read), data-validation rules, and form controls with the macro each
/// runs, its caption, linked cell or input range. Read straight from the Workbook stream: nothing is calculated
/// or run. Anything it cannot read becomes a warning, never a guess. Excel 5/95 (BIFF5) books are noted, not read.
/// </summary>
internal static class LegacyWorkbook
{
    private const int MaxFormulasPerSheet = WorkbookSurface.MaxFormulasPerSheet;

    // Record numbers, [MS-XLS] 2.3.2.
    private const int Bof = 0x0809, Eof = 0x000A, Continue = 0x003C, BoundSheet = 0x0085, Lbl = 0x0018, SupBook = 0x01AE,
        ExternSheet = 0x0017, ExternName = 0x0023, Sst = 0x00FC, CodeName = 0x01BA, Dimensions = 0x0200,
        LabelSst = 0x00FD, Label = 0x0204, Number = 0x0203, Rk = 0x027E, MulRk = 0x00BD, BoolErr = 0x0205,
        Formula = 0x0006, ShrFmla = 0x04BC, ArrayFmla = 0x0221, Dv = 0x01BE, Obj = 0x005D, Txo = 0x01B6, MsoDrawing = 0x00EC;

    private readonly record struct Rec(int Id, int Pos, int Len);

    private sealed record RawName(string Name, int Flags, int Itab, byte[] Rgce);

    private sealed class Control
    {
        public int Ot, Id;
        public string? Caption, Macro, Link, InputRange, ShapeName;
        public byte[]? MacroRgce, LinkRgce, RangeRgce;
    }

    private sealed class Shape { public uint Spid, Flags; public string? Name; }

    public static WorkbookSurfaceResult? Read(CompoundFile cf, string fileName, VbaProject? vba, List<string> warnings)
    {
        var entry = cf.Find("Workbook");
        if (entry is null)
        {
            if (cf.Find("Book") is not null)
                warnings.Add($"{fileName} is an Excel 5/95 workbook (BIFF5): its VBA was read, but its sheets, names and controls are not.");
            else
                warnings.Add($"{fileName} has no workbook stream; only its VBA was read.");
            return null;
        }
        return ReadStream(cf.ReadStream(entry), fileName, vba, warnings, cf.Find("Ctls") is not null);
    }

    /// <summary>The workbook from its raw <c>Workbook</c> stream (also how the tests feed it hand-built books).</summary>
    internal static WorkbookSurfaceResult? ReadStream(byte[] b, string fileName, VbaProject? vba, List<string> warnings, bool hasCtls = false)
    {
        var host = new VbaHost(vba, fileName);
        try { return new Parser(b, fileName, vba, host, warnings, hasCtls).Run(); }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            warnings.Add($"{fileName}: the workbook stream could not be read ({ex.Message}); only its VBA was read.");
            return null;
        }
    }

    private static int U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p, 2));
    private static int S16(byte[] b, int p) => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(p, 2));
    private static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4));

    private static List<Rec> Records(byte[] b, int start)
    {
        var list = new List<Rec>();
        var p = start; var depth = 0;
        while (p + 4 <= b.Length)
        {
            var id = U16(b, p); var len = U16(b, p + 2);
            if (p + 4 + len > b.Length) break;
            list.Add(new Rec(id, p + 4, len));
            p += 4 + len;
            if (id == Bof) depth++;
            else if (id == Eof && --depth <= 0) break;
        }
        return list;
    }

    /// <summary>A string after its length: one flag byte (bit 0: two bytes per character), then the characters.</summary>
    private static string XlChars(byte[] b, ref int p, int cch)
    {
        var wide = (b[p] & 1) != 0; p++;
        var n = wide ? cch * 2 : cch;
        if (p + n > b.Length) throw new InvalidDataException("a string runs past the end of the stream");
        var s = wide ? Encoding.Unicode.GetString(b, p, n) : Encoding.Latin1.GetString(b, p, n);
        p += n;
        return s;
    }

    private sealed class Parser
    {
        private readonly byte[] _b;
        private readonly string _file;
        private readonly VbaProject? _vba;
        private readonly VbaHost _host;
        private readonly List<string> _warnings;
        private readonly bool _hasCtls;

        private readonly List<(string Name, int State, int Type, int Pos)> _sheets = new();
        private readonly List<RawName> _rawNames = new();
        private readonly List<string> _sst = new();
        private readonly List<(bool Self, string? Name, List<string> Sheets, List<string> ExternNames)> _supBooks = new();
        private readonly List<(int Sup, int First, int Last)> _xti = new();
        private string? _bookCodeName;
        private BiffFormulaContext _ctx = new();
        private readonly Dictionary<string, Dictionary<(int Row, int Col), string>> _cellText = new(StringComparer.OrdinalIgnoreCase);

        public Parser(byte[] b, string file, VbaProject? vba, VbaHost host, List<string> warnings, bool hasCtls)
        {
            _b = b; _file = file; _vba = vba; _host = host; _warnings = warnings; _hasCtls = hasCtls;
        }

        public WorkbookSurfaceResult Run()
        {
            ReadGlobals();
            var sheetNames = _sheets.Select(s => s.Name).ToList();
            _ctx = Context(0, 0);

            // Texts first (labels need every sheet), then everything that reads the sheets.
            var parsed = new List<(int Index, List<Rec> Recs)>();
            for (var i = 0; i < _sheets.Count; i++)
            {
                if (_sheets[i].Type != 0) continue;
                var recs = Records(_b, _sheets[i].Pos);
                parsed.Add((i, recs));
                CollectTexts(_sheets[i].Name, recs);
            }

            var names = BuildNames(sheetNames);
            var nameSet = new HashSet<string>(names.Select(n => n.Name), StringComparer.OrdinalIgnoreCase);
            var sheets = new List<WorkbookSheet>();
            var byIndex = parsed.ToDictionary(x => x.Index, x => x.Recs);
            for (var i = 0; i < _sheets.Count; i++)
            {
                var s = _sheets[i];
                var state = s.State switch { 1 => "hidden", 2 => "veryHidden", _ => "visible" };
                if (!byIndex.TryGetValue(i, out var recs))
                {
                    if (s.Type == 1) _warnings.Add($"{_file}: sheet \"{s.Name}\" is an Excel 4 macro sheet; its commands are not read.");
                    sheets.Add(new WorkbookSheet(s.Name, null, state, null, 0, 0, Array.Empty<WorkbookFormula>(), Array.Empty<WorkbookValidation>(),
                        Array.Empty<WorkbookControl>(), Array.Empty<WorkbookTable>(), Array.Empty<string>()));
                    continue;
                }
                sheets.Add(ReadSheet(s.Name, state, recs, sheetNames, nameSet));
            }

            if (_hasCtls)
                _warnings.Add($"{_file}: the workbook holds ActiveX controls on its sheets; they are not read (form controls are).");

            var externals = _supBooks.Where(s => !s.Self && s.Name is not null)
                .Select(s => new WorkbookExternal("workbook", Path.GetFileName(s.Name!.Replace('\\', '/')), WorkbookSurface.Redact(s.Name!)))
                .ToList();
            return new WorkbookSurfaceResult(_file, sheets, names, _host.ReadEvents(sheets), _host.ReadCodeReach(sheets, names), externals, _vba, _warnings);
        }

        private BiffFormulaContext Context(int row, int col) => new()
        {
            SheetNames = _sheets.Select(s => s.Name).ToList(),
            DefinedNames = _rawNames.Select(n => n.Name).ToList(),
            Xti = _xti,
            SupBooks = _supBooks.Select(s => (s.Self, s.Name, (IReadOnlyList<string>)s.Sheets, (IReadOnlyList<string>)s.ExternNames)).ToList(),
            Row = row, Col = col,
        };

        // ── Workbook globals ──────────────────────────────────────────────

        private void ReadGlobals()
        {
            var recs = Records(_b, 0);
            if (recs.Count == 0 || recs[0].Id != Bof) throw new InvalidDataException("the workbook stream does not start with a BOF record");
            var bofType = U16(_b, recs[0].Pos + 2);
            if (U16(_b, recs[0].Pos) != 0x0600) throw new InvalidDataException("not a BIFF8 workbook");
            if (bofType != 0x0005) throw new InvalidDataException("the first substream is not the workbook globals");

            for (var i = 0; i < recs.Count; i++)
            {
                var r = recs[i]; var p = r.Pos;
                switch (r.Id)
                {
                    case BoundSheet:
                    {
                        var pos = (int)U32(_b, p); var state = _b[p + 4] & 3; var type = _b[p + 5];
                        var q = p + 6; var cch = _b[q]; q++;
                        _sheets.Add((XlChars(_b, ref q, cch), state, type, pos));
                        break;
                    }
                    case Sst: ReadSst(recs, i); break;
                    case Lbl: _rawNames.Add(ReadLbl(r)); break;
                    case SupBook: _supBooks.Add(ReadSupBook(r)); break;
                    case ExternName:
                        if (_supBooks.Count > 0) _supBooks[^1].ExternNames.Add(ReadExternName(r));
                        break;
                    case ExternSheet:
                    {
                        var n = U16(_b, p);
                        for (var k = 0; k < n && p + 2 + 6 * (k + 1) <= p + r.Len; k++)
                            _xti.Add((U16(_b, p + 2 + 6 * k), S16(_b, p + 4 + 6 * k), S16(_b, p + 6 + 6 * k)));
                        break;
                    }
                    case CodeName when _bookCodeName is null:
                    {
                        var q = p + 2; _bookCodeName = XlChars(_b, ref q, U16(_b, p));
                        break;
                    }
                }
            }
        }

        private (bool, string?, List<string>, List<string>) ReadSupBook(Rec r)
        {
            var p = r.Pos; var ctab = U16(_b, p); var cch = U16(_b, p + 2); p += 4;
            if (cch == 0x0401) return (true, null, new List<string>(), new List<string>());
            if (cch == 0x3A01) return (false, null, new List<string>(), new List<string>());     // add-in functions
            var sheets = new List<string>();
            string? path = null;
            if (cch is >= 1 and <= 0xFF)
            {
                path = DecodeVirtPath(XlChars(_b, ref p, cch));
                for (var k = 0; k < ctab && p + 3 <= r.Pos + r.Len; k++)
                {
                    var n = U16(_b, p); p += 2;
                    sheets.Add(XlChars(_b, ref p, n));
                }
                if (path.Length == 0) path = null;                                           // a single char marks "unused" / "same sheet"
            }
            return (false, path, sheets, new List<string>());
        }

        /// <summary>
        /// An external workbook's path as the file encodes it: 0x01 introduces a drive letter, 0x03 separates
        /// directories, 0x04 stands for "up one level"; the file name sits in [brackets]. A single character
        /// (0x20 unused, 0x00 same sheet) is not a path.
        /// </summary>
        private static string DecodeVirtPath(string raw)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < raw.Length; i++)
            {
                var c = raw[i];
                if (c == '\u0001' && i + 1 < raw.Length) { sb.Append(raw[i + 1]).Append(':'); i++; }
                else if (c == '\u0003') sb.Append('\\');
                else if (c == '\u0004') sb.Append("..\\");
                else if (c < ' ' || c is '[' or ']') continue;
                else sb.Append(c);
            }
            var s = sb.ToString().Trim();
            return s.Length <= 1 ? "" : s;
        }

        private string ReadExternName(Rec r)
        {
            // flags (2) + 4 reserved bytes, then the name as a short string.
            var p = r.Pos + 6;
            if (p + 2 > r.Pos + r.Len) return "(external name)";
            var cch = _b[p]; p++;
            return XlChars(_b, ref p, cch);
        }

        private RawName ReadLbl(Rec r)
        {
            var p = r.Pos;
            var flags = U16(_b, p); var cch = _b[p + 3]; var cce = U16(_b, p + 4); var itab = U16(_b, p + 8);
            p += 14;
            string name;
            if ((flags & 0x20) != 0)                                                         // built-in: one code byte after the flag
            {
                var code = _b[p + 1 > _b.Length ? p : p + 1];
                name = "_xlnm." + code switch
                {
                    0x00 => "Consolidate_Area", 0x01 => "Auto_Open", 0x02 => "Auto_Close", 0x03 => "Extract", 0x04 => "Database",
                    0x05 => "Criteria", 0x06 => "Print_Area", 0x07 => "Print_Titles", 0x08 => "Recorder", 0x09 => "Data_Form",
                    0x0A => "Auto_Activate", 0x0B => "Auto_Deactivate", 0x0C => "Sheet_Title", 0x0D => "_FilterDatabase", _ => $"Builtin{code}",
                };
                p += 1 + cch;
            }
            else name = XlChars(_b, ref p, cch);
            var rgce = p + cce <= r.Pos + r.Len ? _b.AsSpan(p, cce).ToArray() : Array.Empty<byte>();
            return new RawName(name, flags, itab, rgce);
        }

        private void ReadSst(List<Rec> recs, int index)
        {
            // The string table may continue over several CONTINUE records; a string cut at a boundary restarts with a flag byte.
            var segs = new List<(int Pos, int Len)> { (recs[index].Pos, recs[index].Len) };
            for (var k = index + 1; k < recs.Count && recs[k].Id == Continue; k++) segs.Add((recs[k].Pos, recs[k].Len));
            var seg = 0; var p = segs[0].Pos + 8;                                             // after cstTotal, cstUnique
            var unique = (int)U32(_b, segs[0].Pos + 4);
            int End(int s) => segs[s].Pos + segs[s].Len;

            bool Next() { if (seg + 1 >= segs.Count) return false; seg++; p = segs[seg].Pos; return true; }
            void Skip(int n)
            {
                while (n > 0)
                {
                    var take = Math.Min(n, End(seg) - p);
                    p += take; n -= take;
                    if (n > 0 && !Next()) return;
                }
            }

            for (var s = 0; s < unique; s++)
            {
                if (p >= End(seg) && !Next()) break;
                if (p + 3 > End(seg)) break;
                var cch = U16(_b, p); var flags = _b[p + 2]; p += 3;
                var wide = (flags & 1) != 0; var rich = (flags & 8) != 0; var ext = (flags & 4) != 0;
                var cRun = 0; var cbExt = 0;
                if (rich) { cRun = U16(_b, p); p += 2; }
                if (ext) { cbExt = (int)U32(_b, p); p += 4; }
                var sb = new StringBuilder(cch);
                var remaining = cch;
                while (remaining > 0)
                {
                    var bpc = wide ? 2 : 1;
                    var take = Math.Min(remaining, (End(seg) - p) / bpc);
                    if (take > 0)
                    {
                        sb.Append(wide ? Encoding.Unicode.GetString(_b, p, take * 2) : Encoding.Latin1.GetString(_b, p, take));
                        p += take * bpc; remaining -= take;
                    }
                    if (remaining > 0)
                    {
                        if (!Next()) break;
                        wide = (_b[p] & 1) != 0; p++;                                        // the continued characters start with their own flag
                    }
                }
                Skip(cRun * 4 + cbExt);
                _sst.Add(sb.ToString());
            }
        }

        // ── Defined names ─────────────────────────────────────────────────

        private List<WorkbookName> BuildNames(List<string> sheetNames)
        {
            var list = new List<WorkbookName>();
            foreach (var n in _rawNames)
            {
                const int Hidden = 0x01, Func = 0x02, Proc = 0x08, Builtin = 0x20;
                if ((n.Flags & (Builtin | Func | Proc)) != 0 || n.Rgce.Length == 0) continue;   // Excel's own names and macro registrations
                if (n.Name.StartsWith("_xl", StringComparison.Ordinal)) continue;                // bookkeeping names (_xlnm., _xleta.), as in the .xlsx reader
                var rendered = BiffFormula.Render(n.Rgce, _ctx);
                var scope = n.Itab >= 1 && n.Itab <= sheetNames.Count ? sheetNames[n.Itab - 1] : null;
                list.Add(new WorkbookName(n.Name, rendered.Text, scope, (n.Flags & Hidden) != 0, LabelForRef(rendered.Text, scope)));
            }
            return list;
        }

        // ── One worksheet ─────────────────────────────────────────────────

        private void CollectTexts(string sheet, List<Rec> recs)
        {
            var map = new Dictionary<(int, int), string>();
            foreach (var r in recs)
            {
                try
                {
                    if (r.Id == LabelSst)
                    {
                        var idx = (int)U32(_b, r.Pos + 6);
                        if (idx >= 0 && idx < _sst.Count && !string.IsNullOrWhiteSpace(_sst[idx])) map[(U16(_b, r.Pos), U16(_b, r.Pos + 2))] = _sst[idx].Trim();
                    }
                    else if (r.Id == Label)
                    {
                        var q = r.Pos + 6; var cch = U16(_b, q); q += 2;
                        var s = XlChars(_b, ref q, cch);
                        if (!string.IsNullOrWhiteSpace(s)) map[(U16(_b, r.Pos), U16(_b, r.Pos + 2))] = s.Trim();
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException) { }
            }
            _cellText[sheet] = map;
        }

        private static readonly Regex SingleCell = new(@"^(?:'?(?<sheet>(?:[^'!]|'')+)'?!)?\$?(?<col>[A-Z]{1,3})\$?(?<row>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private string? LabelForRef(string refersTo, string? scope)
        {
            var m = SingleCell.Match(refersTo.Trim());
            if (!m.Success) return null;
            var sheet = m.Groups["sheet"].Success ? m.Groups["sheet"].Value.Replace("''", "'") : scope;
            if (sheet is null) return null;
            var col = m.Groups["col"].Value.ToUpperInvariant().Aggregate(0, (n, ch) => n * 26 + ch - 'A' + 1) - 1;
            return LabelBeside(sheet, int.Parse(m.Groups["row"].Value) - 1, col);
        }

        /// <summary>The nearest text to the left on the same row (within three cells), else the cell above.</summary>
        private string? LabelBeside(string sheet, int row, int col)
        {
            if (!_cellText.TryGetValue(sheet, out var texts)) return null;
            for (var c = col - 1; c >= Math.Max(0, col - 3); c--)
                if (texts.TryGetValue((row, c), out var t)) return Clip(t);
            return row > 0 && texts.TryGetValue((row - 1, col), out var above) ? Clip(above) : null;
            static string Clip(string t) => t.Length > 80 ? t[..80] + "…" : t;
        }

        private WorkbookSheet ReadSheet(string name, string state, List<Rec> recs, List<string> sheetNames, HashSet<string> nameSet)
        {
            string? codeName = null, used = null;
            int values = 0, formulaCells = 0;
            var formulas = new List<WorkbookFormula>();
            var reads = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var validations = new List<WorkbookValidation>();
            var controls = new List<WorkbookControl>();

            // Shared and array formulas hold their text once, in a record after the first cell that uses it.
            var shared = new Dictionary<(int, int), (string Range, byte[] Rgce, bool IsArray)>();
            foreach (var r in recs)
            {
                try
                {
                    if (r.Id == ShrFmla || r.Id == ArrayFmla)
                    {
                        var rwFirst = U16(_b, r.Pos); var rwLast = U16(_b, r.Pos + 2); var cFirst = _b[r.Pos + 4]; var cLast = _b[r.Pos + 5];
                        var cceAt = r.Id == ShrFmla ? r.Pos + 8 : r.Pos + 12;
                        var cce = U16(_b, cceAt);
                        if (cceAt + 2 + cce > r.Pos + r.Len) continue;
                        shared[(rwFirst, cFirst)] = (BiffFormula.Cell(rwFirst, cFirst) + ":" + BiffFormula.Cell(rwLast, cLast),
                            _b.AsSpan(cceAt + 2, cce).ToArray(), r.Id == ArrayFmla);
                    }
                }
                catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException) { }
            }

            var objs = new List<Control>();
            var shapes = new Queue<Shape>();
            Control? lastObj = null;
            var truncated = false;

            for (var i = 0; i < recs.Count; i++)
            {
                var r = recs[i]; var p = r.Pos;
                try
                {
                    switch (r.Id)
                    {
                        case Dimensions:
                        {
                            var rwMic = (int)U32(_b, p); var rwMac = (int)U32(_b, p + 4); var cMic = U16(_b, p + 8); var cMac = U16(_b, p + 10);
                            if (rwMac > rwMic && cMac > cMic) used = BiffFormula.Cell(rwMic, cMic) + ":" + BiffFormula.Cell(rwMac - 1, cMac - 1);
                            break;
                        }
                        case CodeName: { var q = p + 2; codeName = XlChars(_b, ref q, U16(_b, p)); break; }
                        case LabelSst or Label or Number or Rk or BoolErr: values++; break;
                        case MulRk: values += Math.Max(0, (r.Len - 6) / 6); break;
                        case Formula:
                        {
                            formulaCells++;
                            var row = U16(_b, p); var col = U16(_b, p + 2);
                            var cce = U16(_b, p + 20);
                            if (22 + cce > r.Len) break;
                            var rgce = _b.AsSpan(p + 22, cce).ToArray();
                            string? range = null; var isArray = false;
                            if (rgce.Length >= 5 && rgce[0] == 0x01)                         // PtgExp: this cell uses a shared or array formula
                            {
                                var key = (U16(rgce, 1), U16(rgce, 3));
                                if ((key.Item1, key.Item2) != (row, col) || !shared.TryGetValue(key, out var sh)) break;   // a follower: its leader lists it
                                rgce = sh.Rgce; range = sh.Range; isArray = sh.IsArray;
                            }
                            if (formulas.Count >= MaxFormulasPerSheet) { truncated = true; break; }
                            var text = BiffFormula.Render(rgce, _ctx.At(row, col));
                            var refs = VbaHost.FormulaRefs(text.Text, name, sheetNames, nameSet, new HashSet<string>());
                            foreach (var x in refs.Where(x => x.StartsWith("sheet:", StringComparison.Ordinal))) reads.Add(x[6..]);
                            formulas.Add(new WorkbookFormula(BiffFormula.Cell(row, col), text.Complete ? text.Text : text.Text + "  (not fully readable)", range, isArray, refs));
                            break;
                        }
                        case Dv: validations.Add(ReadDv(name, r)); break;
                        case MsoDrawing: foreach (var s in ReadShapes(r)) shapes.Enqueue(s); break;
                        case Obj:
                        {
                            var o = ReadObj(r);
                            if (o is null) break;
                            o.ShapeName = shapes.Count > 0 ? shapes.Dequeue().Name : null;
                            objs.Add(o); lastObj = o;
                            break;
                        }
                        case Txo:
                        {
                            if (lastObj is not null && lastObj.Caption is null && i + 1 < recs.Count && recs[i + 1].Id == Continue)
                            {
                                var cchText = U16(_b, p + 10);
                                var c = recs[i + 1]; var q = c.Pos;
                                if (cchText > 0 && c.Len >= 1) lastObj.Caption = XlChars(_b, ref q, Math.Min(cchText, (c.Len - 1) / ((_b[c.Pos] & 1) != 0 ? 2 : 1))).Trim();
                            }
                            break;
                        }
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
                {
                    _warnings.Add($"{_file}: a record (0x{r.Id:X4}) on sheet \"{name}\" could not be read ({ex.Message}).");
                }
            }
            if (truncated) _warnings.Add($"{_file}: {name}: only the first {MaxFormulasPerSheet:N0} formulas are listed; all {formulaCells:N0} are counted.");

            foreach (var o in objs)
            {
                var c = ToControl(o, name);
                if (c is not null) controls.Add(c);
            }
            DeduplicateNames(controls);
            return new WorkbookSheet(name, codeName, state, used, values, formulaCells, formulas, validations, controls, Array.Empty<WorkbookTable>(), reads.ToList());
        }

        // ── Data validation ───────────────────────────────────────────────

        private WorkbookValidation ReadDv(string sheet, Rec r)
        {
            var p = r.Pos; var end = r.Pos + r.Len;
            var flags = U32(_b, p); p += 4;
            var type = (int)(flags & 0xF); var strLookup = (flags & 0x80) != 0; var op = (int)((flags >> 20) & 0xF);
            // A prompt or error title the author left alone is stored as a single NUL character.
            string Str() { var cch = U16(_b, p); p += 2; return XlChars(_b, ref p, cch).Trim('\0'); }
            var promptTitle = Str(); Str(); var prompt = Str(); Str();
            byte[] Fmla() { var cce = U16(_b, p); p += 4; var g = _b.AsSpan(p, cce).ToArray(); p += cce; return g; }
            var f1 = Fmla(); var f2 = Fmla();
            var cref = U16(_b, p); p += 2;
            var ranges = new List<string>();
            for (var k = 0; k < cref && p + 8 <= end; k++, p += 8)
            {
                var a = BiffFormula.Cell(U16(_b, p), U16(_b, p + 4)); var z = BiffFormula.Cell(U16(_b, p + 2), U16(_b, p + 6));
                ranges.Add(a == z ? a : a + ":" + z);
            }
            var typeName = type switch { 1 => "whole", 2 => "decimal", 3 => "list", 4 => "date", 5 => "time", 6 => "textLength", 7 => "custom", _ => "any" };

            string? source = null;
            if (f1.Length > 0)
            {
                source = (type == 3 ? ListLiteral(f1) : null) ?? BiffFormula.Render(f1, _ctx).Text;
            }
            if (type is not (0 or 3 or 7) && source is not null)
            {
                var upper = f2.Length > 0 ? BiffFormula.Render(f2, _ctx).Text : null;
                source = op switch
                {
                    0 => upper is null ? $">= {source}" : $"{source} to {upper}",
                    1 => upper is null ? $"< {source}" : $"not {source} to {upper}",
                    2 => $"= {source}", 3 => $"<> {source}", 4 => $"> {source}", 5 => $"< {source}", 6 => $">= {source}", 7 => $"<= {source}",
                    _ => source,
                };
            }
            var first = ranges.FirstOrDefault()?.Split(':')[0] ?? "";
            string? label = null;
            var m = Regex.Match(first, @"^([A-Z]+)(\d+)$");
            if (m.Success) label = LabelBeside(sheet, int.Parse(m.Groups[2].Value) - 1, m.Groups[1].Value.Aggregate(0, (n, ch) => n * 26 + ch - 'A' + 1) - 1);
            var title = string.IsNullOrEmpty(promptTitle) ? (string.IsNullOrEmpty(prompt) ? null : prompt) : promptTitle;
            return new WorkbookValidation(string.Join(" ", ranges), typeName, source, label, title);
        }

        /// <summary>A literal list ("Yes,No") is one string token whose items are separated by a NUL (or a comma).</summary>
        private static string? ListLiteral(byte[] rgce)
        {
            if (rgce[0] is not (0x17 or 0x37 or 0x57 or 0x77) || rgce.Length < 3) return null;
            var cch = rgce[1]; var wide = (rgce[2] & 1) != 0;
            if (3 + (wide ? cch * 2 : cch) != rgce.Length) return null;                      // exactly one string token
            var s = wide ? Encoding.Unicode.GetString(rgce, 3, cch * 2) : Encoding.Latin1.GetString(rgce, 3, cch);
            return string.Join(",", s.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        }

        // ── Form controls ─────────────────────────────────────────────────

        private Control? ReadObj(Rec r)
        {
            var p = r.Pos; var end = r.Pos + r.Len;
            var o = new Control { Ot = -1 };
            while (p + 4 <= end)
            {
                var ft = U16(_b, p); var cb = U16(_b, p + 2);
                if (ft == 0x0000) break;
                if (p + 4 + cb > end) break;
                switch (ft)
                {
                    case 0x0015: if (cb >= 4) { o.Ot = U16(_b, p + 4); o.Id = U16(_b, p + 6); } break;
                    case 0x0004: o.MacroRgce = ObjFmla(p + 4, cb); break;                    // FtMacro: ft, then an ObjFmla (cbFmla = cb)
                    case 0x000E or 0x0014: o.LinkRgce = ObjFmla(p + 4, cb); break;           // ObjLinkFmla
                    case 0x0013:                                                              // FtLbsData: its formula is the list's input range
                        if (p + 6 <= end) o.RangeRgce = ObjFmla(p + 6, U16(_b, p + 4));
                        p = end; continue;
                }
                p += 4 + cb;
            }
            return o.Ot < 0 ? null : o;
        }

        /// <summary>An ObjFmla's single token: cce (15 bits), four unused bytes, then the rgce.</summary>
        private byte[]? ObjFmla(int at, int cbFmla)
        {
            if (cbFmla < 2) return null;
            var cce = U16(_b, at) & 0x7FFF;
            if (cce == 0) return null;
            var start = cbFmla >= 2 + 4 + cce ? at + 6 : at + 2;
            return start + cce <= at + cbFmla ? _b.AsSpan(start, cce).ToArray() : null;
        }

        private static string KindOf(int ot) => ot switch
        {
            0x07 => "Button", 0x0B => "CheckBox", 0x0C => "OptionButton", 0x0D => "EditBox", 0x0E => "Label", 0x10 => "Spinner",
            0x11 => "ScrollBar", 0x12 => "ListBox", 0x13 => "GroupBox", 0x14 => "DropDown", 0x08 => "Picture", 0x06 => "TextBox",
            _ => "Shape",
        };

        private WorkbookControl? ToControl(Control o, string sheet)
        {
            var isForm = o.Ot is 0x07 or 0x0B or 0x0C or 0x0D or 0x0E or 0x10 or 0x11 or 0x12 or 0x13 or 0x14;
            string? macro = o.MacroRgce is { Length: > 0 } m ? BiffFormula.Render(m, _ctx).Text : null;
            if (!isForm && macro is null) return null;                                       // pictures, charts, notes: not controls unless they run something
            var linked = o.LinkRgce is { Length: > 0 } l ? BiffFormula.Render(l, _ctx).Text : null;
            var range = o.RangeRgce is { Length: > 0 } g ? BiffFormula.Render(g, _ctx).Text : null;
            var (module, routine, found) = _host.ResolveMacro(macro);
            var kind = KindOf(o.Ot);
            var name = !string.IsNullOrEmpty(o.ShapeName) ? o.ShapeName! : $"{kind} {o.Id}";
            return new WorkbookControl(name, isForm ? "form" : "shape", kind, string.IsNullOrWhiteSpace(o.Caption) ? null : o.Caption,
                macro, module, routine, found, linked, range, Array.Empty<string>());
        }

        private static void DeduplicateNames(List<WorkbookControl> list)
        {
            foreach (var g in list.Select((c, i) => (c, i)).GroupBy(x => x.c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                var n = 1;
                foreach (var (c, i) in g.Skip(1)) list[i] = c with { Name = $"{c.Name} ({++n})" };
            }
        }

        // ── Drawing: the shape's own name ─────────────────────────────────

        /// <summary>The shapes in one MSODRAWING record, in order, with the name each carries (the one the VBA editor calls it).</summary>
        private List<Shape> ReadShapes(Rec r)
        {
            var shapes = new List<Shape>();
            try { Escher(_b, r.Pos, r.Pos + r.Len, null, shapes); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException) { }
            return shapes;
        }

        private static void Escher(byte[] b, int start, int end, Shape? current, List<Shape> shapes)
        {
            var p = start;
            while (p + 8 <= end)
            {
                var verInst = U16(b, p); var type = U16(b, p + 2); var len = (int)U32(b, p + 4);
                var ver = verInst & 0xF; var inst = verInst >> 4;
                var bodyEnd = Math.Min(end, p + 8 + len);
                if (ver == 0xF)
                {
                    if (type == 0xF004)                                                       // SpContainer: one shape
                    {
                        var s = new Shape();
                        Escher(b, p + 8, bodyEnd, s, shapes);
                        if ((s.Flags & 0x4) == 0) shapes.Add(s);                              // not the group's own "patriarch" shape
                    }
                    else Escher(b, p + 8, bodyEnd, current, shapes);
                }
                else if (current is not null && type == 0xF00A && bodyEnd - (p + 8) >= 8) { current.Spid = U32(b, p + 8); current.Flags = U32(b, p + 12); }
                else if (current is not null && type == 0xF00B)                               // OPT: properties; complex ones' data follows the table
                {
                    var dataAt = p + 8 + 6 * inst;
                    for (var k = 0; k < inst; k++)
                    {
                        var opid = U16(b, p + 8 + 6 * k); var value = (int)U32(b, p + 10 + 6 * k);
                        if ((opid & 0x8000) == 0) continue;
                        if (dataAt + value > bodyEnd || value < 0) break;
                        if ((opid & 0x3FFF) == 0x0380) current.Name = Encoding.Unicode.GetString(b, dataAt, value).TrimEnd('\0');
                        dataAt += value;
                    }
                }
                p = bodyEnd;
            }
        }
    }
}
