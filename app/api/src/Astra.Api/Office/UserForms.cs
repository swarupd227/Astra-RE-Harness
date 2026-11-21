using System.Buffers.Binary;
using System.Text;

namespace Astra.Api.Office;

/// <param name="Kind">TextBox, ComboBox, ListBox, CheckBox, OptionButton, ToggleButton, CommandButton, Label,
/// Image, Frame, MultiPage, Page, TabStrip, SpinButton, ScrollBar.</param>
/// <param name="Left">Position and size in points (1/72 inch), relative to the container.</param>
/// <param name="Value">The design-time value (a check box's 0/1, a text box's default text).</param>
/// <param name="GroupName">Option buttons with the same group name are one choice.</param>
/// <param name="ControlSource">The worksheet cell the control is bound to, if any.</param>
/// <param name="RowSource">The worksheet range a list or combo box takes its items from, if any.</param>
/// <param name="Children">The controls inside a Frame, or the pages of a MultiPage (each a "Page" with its own controls).</param>
public sealed record FormControl(
    string Name, string Kind, string? Caption, int? TabIndex,
    double Left, double Top, double? Width, double? Height,
    string? Value, string? GroupName, int? MaxLength,
    string? ControlSource, string? RowSource, string? Tag, string? ToolTip,
    IReadOnlyList<FormControl> Children);

/// <param name="Complete">False when part of the designer data could not be read; what was read is still listed.</param>
public sealed record UserFormLayout(double? Width, double? Height, IReadOnlyList<FormControl> Controls, bool Complete);

/// <summary>
/// Reads a UserForm's controls from its designer storage ([MS-OFORMS]): the <c>f</c> stream lists the
/// controls (name, kind, tab order, position) and the <c>o</c> stream holds each one's own data (caption,
/// size, value), in the same order; a Frame or a MultiPage page is a nested storage <c>i&lt;id&gt;</c> with
/// its own pair. Every record declares its byte size, and the reader checks it consumed exactly that, so a
/// misread structure becomes a warning (and <see cref="UserFormLayout.Complete"/> = false), never a wrong layout
/// presented as right. Nothing is rendered or run.
/// </summary>
public static class UserForms
{
    private const double PointsPerHimetric = 72.0 / 2540.0;
    private const int MaxDepth = 8;

    private static readonly Dictionary<int, string> ClassCache = new()
    {
        [7] = "Page", [12] = "Image", [14] = "Frame", [15] = "MorphData", [16] = "SpinButton", [17] = "CommandButton",
        [18] = "TabStrip", [21] = "Label", [23] = "TextBox", [24] = "ListBox", [25] = "ComboBox", [26] = "CheckBox",
        [27] = "OptionButton", [28] = "ToggleButton", [47] = "ScrollBar", [57] = "MultiPage",
    };

    private static readonly Dictionary<int, string> MorphStyles = new()
    {
        [1] = "TextBox", [2] = "ListBox", [3] = "ComboBox", [4] = "CheckBox", [5] = "OptionButton", [6] = "ToggleButton", [7] = "ComboBox",
    };

    public static UserFormLayout? Read(CompoundFile cf, CompoundFile.Entry designer, string formName, List<string> warnings)
    {
        var problems = new List<string>();
        var (w, h, controls) = ReadContainer(cf, designer, problems, 0);
        foreach (var p in problems.Distinct().Take(5)) warnings.Add($"form {formName}: {p}");
        if (controls is null) return null;
        return new UserFormLayout(w, h, controls, problems.Count == 0);
    }

    private static (double? W, double? H, List<FormControl>? Controls) ReadContainer(
        CompoundFile cf, CompoundFile.Entry storage, List<string> problems, int depth)
    {
        var fEntry = cf.Child(storage, "f");
        if (fEntry is null) { problems.Add("no control list (f stream)"); return (null, null, null); }
        var f = cf.ReadStream(fEntry);
        var o = cf.Child(storage, "o") is { } oEntry ? cf.ReadStream(oEntry) : Array.Empty<byte>();
        return ReadLevel(f, o, id => cf.Child(storage, $"i{id:00}") is { IsStorage: true } sub ? ReadContainer(cf, sub, problems, depth + 1).Controls : null,
            problems, depth);
    }

    /// <summary>One container's controls from its two streams; <paramref name="child"/> reads a nested Frame or page by site id.</summary>
    internal static (double? W, double? H, List<FormControl>? Controls) ReadLevel(
        byte[] f, byte[] o, Func<int, List<FormControl>?> child, List<string> problems, int depth = 0)
    {
        FormHeader form;
        try { form = ReadForm(f); }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            problems.Add($"the control list could not be read ({ex.Message})");
            return (null, null, null);
        }

        var controls = new List<FormControl>();
        var offset = 0;
        foreach (var site in form.Sites)
        {
            var kind = site.ClassIndex is int ci && ClassCache.TryGetValue(ci, out var k) ? k : "Control";
            ControlData data = default;
            if (site.ObjectStreamSize > 0)
            {
                if (offset + site.ObjectStreamSize > o.Length) { problems.Add($"{site.Name}: its data runs past the end of the o stream"); break; }
                try
                {
                    data = kind switch
                    {
                        "CommandButton" => ReadCaptioned(o, offset, button: true),
                        "Label" => ReadCaptioned(o, offset, button: false),
                        "TextBox" or "ListBox" or "ComboBox" or "CheckBox" or "OptionButton" or "ToggleButton" or "MorphData" => ReadMorph(o, offset),
                        _ => default,
                    };
                    if (data.Consumed > site.ObjectStreamSize)
                    {
                        problems.Add($"{site.Name}: read {data.Consumed} bytes of data declared as {site.ObjectStreamSize}");
                        data = default;
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
                {
                    problems.Add($"{site.Name}: its data could not be read ({ex.Message})");
                    data = default;
                }
                if (kind == "MorphData" && data.Style is int st && MorphStyles.TryGetValue(st, out var morph)) kind = morph;
                offset += (int)site.ObjectStreamSize;
            }

            var children = new List<FormControl>();
            if (site.Id is int id && depth < MaxDepth && child(id) is { } inner) children = inner;
            // A MultiPage's own storage holds a TabStrip and one Page per tab; the tab strip is plumbing.
            if (kind == "MultiPage") children = children.Where(c => c.Kind != "TabStrip").ToList();

            controls.Add(new FormControl(
                site.Name ?? $"Control{site.Id}", kind, data.Caption, site.TabIndex,
                Pt(site.Left), Pt(site.Top), data.Width is int dw ? Pt(dw) : null, data.Height is int dh ? Pt(dh) : null,
                data.Value, data.GroupName, data.MaxLength, site.ControlSource, site.RowSource, site.Tag, site.ToolTip, children));
        }
        if (offset != o.Length && o.Length > 0 && !problems.Any())
            problems.Add($"the o stream holds {o.Length} bytes but the controls declare {offset}");
        return (form.Width is int fw ? Pt(fw) : null, form.Height is int fh ? Pt(fh) : null, controls);
    }

    private static double Pt(int himetric) => Math.Round(himetric * PointsPerHimetric, 1);

    // ── The f stream ──────────────────────────────────────────────────

    private sealed record Site(string? Name, int? Id, int? TabIndex, int? ClassIndex, uint ObjectStreamSize, int Left, int Top,
        string? Tag, string? ToolTip, string? ControlSource, string? RowSource);

    private sealed record FormHeader(int? Width, int? Height, List<Site> Sites);

    private static FormHeader ReadForm(byte[] f)
    {
        var r = new Reader(f, 0);
        if (r.U8() != 0 || r.U8() != 4) throw new InvalidDataException("not a version 4.0 form");
        var cbForm = r.U16(align: false);
        var mask = r.U32(align: false);
        bool Has(int bit) => (mask >> bit & 1) != 0;

        uint boolProps = 0, captionLen = 0;
        if (Has(1)) r.U32();                 // BackColor
        if (Has(2)) r.U32();                 // ForeColor
        if (Has(3)) r.U32();                 // NextAvailableID
        if (Has(6)) boolProps = r.U32();     // BooleanProperties
        if (Has(7)) r.U8();                  // BorderStyle
        if (Has(8)) r.U8();                  // MousePointer
        if (Has(9)) r.U8();                  // ScrollBars
        if (Has(13)) r.U32();                // GroupCnt
        if (Has(15)) r.U16();                // MouseIcon
        if (Has(16)) r.U8();                 // Cycle
        if (Has(17)) r.U8();                 // SpecialEffect
        if (Has(18)) r.U32();                // BorderColor
        if (Has(19)) captionLen = r.U32();   // Caption length
        if (Has(20)) r.U16();                // Font
        if (Has(21)) r.U16();                // Picture
        if (Has(22)) r.U32();                // Zoom
        if (Has(23)) r.U8();                 // PictureAlignment
        if (Has(25)) r.U8();                 // PictureSizeMode
        if (Has(26)) r.U32();                // ShapeCookie
        if (Has(27)) r.U32();                // DrawBuffer
        r.Align(4);
        int? width = null, height = null;
        if (Has(10)) { width = (int)r.U32(); height = (int)r.U32(); }   // DisplayedSize
        if (Has(11)) { r.U32(); r.U32(); }                               // LogicalSize
        if (Has(12)) { r.U32(); r.U32(); }                               // ScrollPosition
        if (Has(19)) r.String(captionLen);
        if (r.Pos != 4 + cbForm) throw new InvalidDataException($"form header ends at {r.Pos}, declared {4 + cbForm}");

        if (Has(15)) SkipPicture(r);
        if (Has(20)) SkipFont(r);
        if (Has(21)) SkipPicture(r);

        const uint DontSaveClassTable = 0x8000;
        if ((boolProps & DontSaveClassTable) == 0)
        {
            var classes = r.U16(align: false);
            if (classes != 0) throw new InvalidDataException($"a class table of {classes} entries (custom ActiveX controls) is not read yet");
        }
        var count = r.U32(align: false);
        var bytes = r.U32(align: false);
        if (count > 10_000) throw new InvalidDataException("implausible control count");
        var begin = r.Pos;
        var seen = 0u;
        while (seen < count)
        {
            r.U8();                                          // depth
            var typeOrCount = r.U8();
            if ((typeOrCount & 0x80) != 0) { r.U8(); seen += (uint)(typeOrCount & 0x7F); }
            else seen++;
        }
        while ((r.Pos - begin) % 4 != 0) r.Pos++;
        var sites = new List<Site>((int)count);
        for (var i = 0; i < count; i++)
        {
            var (site, size) = ReadSite(f, r.Pos);
            sites.Add(site);
            r.Pos += size;
        }
        if (r.Pos - begin != bytes) throw new InvalidDataException($"the controls take {r.Pos - begin} bytes, declared {bytes}");
        return new FormHeader(width, height, sites);
    }

    private static void SkipFont(Reader r)
    {
        var guid = new Guid(r.Bytes(16));
        if (guid == new Guid("0BE35203-8F91-11CE-9DE3-00AA004BB851"))   // StdFont
        {
            r.Pos += 1 + 2 + 1 + 2 + 4;
            var faceLen = r.U8();
            r.Pos += faceLen;
        }
        else                                                             // TextProps
        {
            r.Pos += 2;
            var cb = r.U16(align: false);
            r.Pos += cb;
        }
    }

    private static void SkipPicture(Reader r)
    {
        r.Pos += 16;
        if (r.U32(align: false) != 0x0000746C) throw new InvalidDataException("picture preamble not found");
        var size = r.U32(align: false);
        r.Pos += checked((int)size);
    }

    private static (Site Site, int Size) ReadSite(byte[] f, int at)
    {
        var r = new Reader(f, at);
        r.U16(align: false);
        var cb = r.U16(align: false);
        var mask = r.U32(align: false);
        bool Has(int bit) => (mask >> bit & 1) != 0;

        uint nameLen = 0, tagLen = 0, tipLen = 0, licLen = 0, srcLen = 0, rowLen = 0, objSize = 0;
        int? id = null, tab = null, cls = null;
        if (Has(0)) nameLen = r.U32();
        if (Has(1)) tagLen = r.U32();
        if (Has(2)) id = (int)r.U32();
        if (Has(3)) r.U32();                      // HelpContextID
        if (Has(4)) r.U32();                      // BitFlags
        if (Has(5)) objSize = r.U32();
        if (Has(6)) tab = r.U16();
        if (Has(7)) cls = r.U16();
        if (Has(9)) r.U16();                      // GroupID
        if (Has(11)) tipLen = r.U32();
        if (Has(12)) licLen = r.U32();
        if (Has(13)) srcLen = r.U32();
        if (Has(14)) rowLen = r.U32();
        r.Align(4);
        var name = Has(0) ? r.String(nameLen) : null;
        var tag = Has(1) ? r.String(tagLen) : null;
        int left = 0, top = 0;
        if (Has(8)) { left = r.I32(); top = r.I32(); }
        var tip = Has(11) ? r.String(tipLen) : null;
        if (Has(12)) r.String(licLen);
        var src = Has(13) ? r.String(srcLen) : null;
        var rows = Has(14) ? r.String(rowLen) : null;
        if (r.Pos != at + 4 + cb) throw new InvalidDataException($"control {name} ends at {r.Pos - at}, declared {4 + cb}");
        return (new Site(name, id, tab, cls, objSize, left, top, Blank(tag), Blank(tip), Blank(src), Blank(rows)), 4 + cb);
    }

    private static string? Blank(string? s) => string.IsNullOrEmpty(s) ? null : s;

    // ── The o stream: each control's own data ─────────────────────────

    private struct ControlData
    {
        public string? Caption, Value, GroupName;
        public int? Width, Height, Style, MaxLength;
        public int Consumed;
    }

    /// <summary>CommandButton and Label: DataBlock with the caption's length, then Caption and Size.</summary>
    private static ControlData ReadCaptioned(byte[] o, int at, bool button)
    {
        var r = new Reader(o, at);
        r.U16(align: false);
        var cb = r.U16(align: false);
        var mask = r.U32(align: false);
        bool Has(int bit) => (mask >> bit & 1) != 0;
        uint captionLen = 0;
        if (Has(0)) r.U32();                      // ForeColor
        if (Has(1)) r.U32();                      // BackColor
        if (Has(2)) r.U32();                      // VariousPropertyBits
        if (Has(3)) captionLen = r.U32();
        if (Has(4)) r.U32();                      // PicturePosition
        if (Has(6)) r.U8();                       // MousePointer
        if (button)
        {
            if (Has(7)) r.U16();                  // Picture
            if (Has(8)) r.U16();                  // Accelerator
            if (Has(10)) r.U16();                 // MouseIcon
        }
        else
        {
            if (Has(7)) r.U32();                  // BorderColor
            if (Has(8)) r.U16();                  // BorderStyle
            if (Has(9)) r.U16();                  // SpecialEffect
            if (Has(10)) r.U16();                 // Picture
            if (Has(11)) r.U16();                 // Accelerator
            if (Has(12)) r.U16();                 // MouseIcon
        }
        r.Align(4);
        var d = new ControlData();
        if (Has(3)) d.Caption = r.String(captionLen);
        if (Has(5)) { d.Width = r.I32(); d.Height = r.I32(); }
        d.Consumed = r.Pos - at;
        if (d.Consumed != 4 + cb) throw new InvalidDataException($"{(button ? "button" : "label")} data ends at {d.Consumed}, declared {4 + cb}");
        return d;
    }

    /// <summary>TextBox, ListBox, ComboBox, CheckBox, OptionButton, ToggleButton (MorphData, a 64-bit mask).</summary>
    private static ControlData ReadMorph(byte[] o, int at)
    {
        var r = new Reader(o, at);
        r.U16(align: false);
        var cb = r.U16(align: false);
        var mask = r.U64(align: false);
        bool Has(int bit) => (mask >> bit & 1) != 0;
        uint valueLen = 0, captionLen = 0, groupLen = 0;
        var d = new ControlData();
        if (Has(0)) r.U32();                          // VariousPropertyBits
        if (Has(1)) r.U32();                          // BackColor
        if (Has(2)) r.U32();                          // ForeColor
        if (Has(3)) d.MaxLength = (int)r.U32();
        if (Has(4)) r.U8();                           // BorderStyle
        if (Has(5)) r.U8();                           // ScrollBars
        if (Has(6)) d.Style = r.U8();                 // DisplayStyle
        if (Has(7)) r.U8();                           // MousePointer
        if (Has(9)) r.U16();                          // PasswordChar
        if (Has(10)) r.U32();                         // ListWidth
        if (Has(11)) r.U16();                         // BoundColumn
        if (Has(12)) r.U16();                         // TextColumn
        if (Has(13)) r.U16();                         // ColumnCount
        if (Has(14)) r.U16();                         // ListRows
        if (Has(15)) r.U16();                         // cColumnInfo
        if (Has(16)) r.U8();                          // MatchEntry
        if (Has(17)) r.U8();                          // ListStyle
        if (Has(18)) r.U8();                          // ShowDropButtonWhen
        if (Has(20)) r.U8();                          // DropButtonStyle
        if (Has(21)) r.U8();                          // MultiSelect
        if (Has(22)) valueLen = r.U32();
        if (Has(23)) captionLen = r.U32();
        if (Has(24)) r.U32();                         // PicturePosition
        if (Has(25)) r.U32();                         // BorderColor
        if (Has(26)) r.U32();                         // SpecialEffect
        if (Has(27)) r.U16();                         // MouseIcon
        if (Has(28)) r.U16();                         // Picture
        if (Has(29)) r.U16();                         // Accelerator
        if (Has(32)) groupLen = r.U32();
        r.Align(4);
        if (Has(8)) { d.Width = r.I32(); d.Height = r.I32(); }
        if (Has(22)) d.Value = r.String(valueLen);
        if (Has(23)) d.Caption = r.String(captionLen);
        if (Has(32)) d.GroupName = r.String(groupLen);
        d.Consumed = r.Pos - at;
        // Column info and stream data (pictures, fonts) may follow; the site's object size covers them.
        return d;
    }

    /// <summary>Little-endian reads with [MS-OFORMS] alignment: an N-byte value sits at an offset divisible by N from the record's start.</summary>
    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1;

    private sealed class Reader
    {
        private readonly byte[] _b;
        private readonly int _base;
        public int Pos;

        public Reader(byte[] b, int at) { _b = b; _base = at; Pos = at; }

        public void Align(int n) { while ((Pos - _base) % n != 0) Pos++; }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (Pos < 0 || Pos + n > _b.Length) throw new InvalidDataException("read past the end of the stream");
            var s = _b.AsSpan(Pos, n);
            Pos += n;
            return s;
        }

        public byte[] Bytes(int n) => Take(n).ToArray();
        public byte U8() => Take(1)[0];
        public ushort U16(bool align = true) { if (align) Align(2); return BinaryPrimitives.ReadUInt16LittleEndian(Take(2)); }
        public uint U32(bool align = true) { if (align) Align(4); return BinaryPrimitives.ReadUInt32LittleEndian(Take(4)); }
        public int I32() { Align(4); return BinaryPrimitives.ReadInt32LittleEndian(Take(4)); }
        public ulong U64(bool align = true) { if (align) Align(8); return BinaryPrimitives.ReadUInt64LittleEndian(Take(8)); }

        /// <summary>A string whose length word carries a compression flag in its top bit: compressed = one byte per character.</summary>
        public string String(uint lengthAndFlag)
        {
            var n = (int)(lengthAndFlag & 0x7FFFFFFF);
            var compressed = (lengthAndFlag & 0x80000000) != 0;
            if (n > 1 << 20) throw new InvalidDataException("implausible string length");
            var raw = Take(n);
            var s = compressed ? Windows1252.GetString(raw) : Encoding.Unicode.GetString(raw);
            Align(4);
            return s;
        }
    }
}
