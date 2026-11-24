using System.Text;
using Astra.Api.Office;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// A small UserForm written byte by byte as [MS-OFORMS] lays it out — a label beside a text box — and
/// read back; then the same form with a size that lies, which must come back flagged, not misread.
/// The real-file check (every control a form's code handles is found) runs over local samples.
/// </summary>
public class UserFormsTests
{
    /// <summary>Little-endian writer that aligns an N-byte value to N from the record start, as the format does.</summary>
    private sealed class W
    {
        public readonly List<byte> B = new();
        private int _base;
        public W Start() { _base = B.Count; return this; }
        public W Align(int n) { while ((B.Count - _base) % n != 0) B.Add(0); return this; }
        public W U8(int v) { B.Add((byte)v); return this; }
        public W U16(int v, bool align = true) { if (align) Align(2); B.AddRange(BitConverter.GetBytes((ushort)v)); return this; }
        public W U32(uint v, bool align = true) { if (align) Align(4); B.AddRange(BitConverter.GetBytes(v)); return this; }
        public W U64(ulong v) { B.AddRange(BitConverter.GetBytes(v)); return this; }
        public W Str(string s) { B.AddRange(Encoding.ASCII.GetBytes(s)); return Align(4); }
        public static uint Len(string s) => (uint)s.Length | 0x80000000;   // compressed: one byte per character
    }

    private static byte[] Site(string name, int id, uint objSize, int tab, int cls, int left, int top)
    {
        var w = new W().Start();
        w.U16(0, false).U16(0, false).U32(0x1E5, false);                  // version, cb (patched), mask: name id objsize tab clsid pos
        w.U32(W.Len(name)).U32((uint)id).U32(objSize).U16(tab).U16(cls).Align(4);
        w.Str(name).U32((uint)left).U32((uint)top);
        var b = w.B.ToArray();
        BitConverter.GetBytes((ushort)(b.Length - 4)).CopyTo(b, 2);
        return b;
    }

    private static (byte[] F, byte[] O) Form(bool lie = false)
    {
        // o stream: a Label ("Age:", 1270 x 423) then a TextBox (MaxLength 3, 2540 x 635).
        var label = new W().Start();
        label.U16(0, false).U16(0, false).U32((1u << 3) | (1u << 5), false).U32(W.Len("Age:")).Align(4).Str("Age:").U32(1270).U32(423);
        var lb = label.B.ToArray(); BitConverter.GetBytes((ushort)(lb.Length - 4)).CopyTo(lb, 2);
        var text = new W().Start();
        text.U16(0, false).U16(0, false).U64((1ul << 3) | (1ul << 8)).U32(3).Align(4).U32(2540).U32(635);
        var tb = text.B.ToArray(); BitConverter.GetBytes((ushort)(tb.Length - 4)).CopyTo(tb, 2);

        var sites = Site("lblAge", 1, (uint)lb.Length, 1, 21, 500, 1000)
            .Concat(Site("txtAge", 2, lie ? (uint)tb.Length - 8 : (uint)tb.Length, 0, 23, 2000, 900)).ToArray();

        var f = new W().Start();
        f.U8(0).U8(4).U16(12, false).U32(1u << 10, false);                // version 0.4, cbForm, mask: DisplayedSize
        f.U32(7000).U32(3000);                                            // DisplayedSize
        f.U16(0, false);                                                   // no class table entries
        f.U32(2, false).U32((uint)(4 + sites.Length), false);              // CountOfSites, CountOfBytes
        f.U8(0).U8(0x82).U8(1).U8(0);                                      // depth 0: 2 controls of type 1, padding
        f.B.AddRange(sites);
        return (f.B.ToArray(), lb.Concat(tb).ToArray());
    }

    [Fact]
    public void Reads_controls_with_names_kinds_captions_tab_order_and_sizes_in_points()
    {
        var (f, o) = Form();
        var problems = new List<string>();
        var (w, h, controls) = UserForms.ReadLevel(f, o, _ => null, problems);
        Assert.Empty(problems);
        Assert.Equal((198.4, 85.0), (w!.Value, h!.Value));

        var label = controls!.Single(c => c.Name == "lblAge");
        Assert.Equal(("Label", "Age:", 1), (label.Kind, label.Caption, label.TabIndex));
        Assert.Equal((14.2, 28.3, 36.0, 12.0), (label.Left, label.Top, label.Width!.Value, label.Height!.Value));

        var box = controls.Single(c => c.Name == "txtAge");
        Assert.Equal(("TextBox", 3, 0), (box.Kind, box.MaxLength, box.TabIndex));
        Assert.Equal((72.0, 18.0), (box.Width!.Value, box.Height!.Value));
    }

    [Fact]
    public void A_size_that_does_not_add_up_is_reported_not_trusted()
    {
        var (f, o) = Form(lie: true);
        var problems = new List<string>();
        var (_, _, controls) = UserForms.ReadLevel(f, o, _ => null, problems);
        Assert.NotEmpty(problems);
        Assert.Null(controls!.Single(c => c.Name == "txtAge").Width);    // its data was not trusted
    }

    [Fact]
    public void Garbage_is_a_problem_not_an_exception()
    {
        var problems = new List<string>();
        var (_, _, controls) = UserForms.ReadLevel(new byte[] { 0, 4, 200, 0, 1, 2 }, Array.Empty<byte>(), _ => null, problems);
        Assert.Null(controls);
        Assert.NotEmpty(problems);
    }

    private static FormControl C(string name, string kind, double left, double top, double w, double h, string? caption = null) =>
        new(name, kind, caption, null, left, top, w, h, null, null, null, null, null, null, null, Array.Empty<FormControl>());

    [Fact]
    public void An_input_takes_the_label_on_its_first_line_not_the_one_above()
    {
        // Two combos under two labels that end at the same x; a tall list whose label sits by its top line.
        var month = C("lbExpMonth", "Label", 12, 66, 66, 18, "Expiration Month:");
        var year = C("lbExpYear", "Label", 18, 90, 60, 18, "Expiration Year:");
        var cbxM = C("cbxExpM", "ComboBox", 84, 60, 96, 18);
        var cbxY = C("cbxExpY", "ComboBox", 84, 84, 96, 18);
        var reg = C("lbRegNo", "Label", 42, 42, 78, 12, "Registration Number:");
        var brand = C("lbBrand", "Label", 90, 66, 30, 12, "Brand:");
        var list = C("lstBrand", "ListBox", 126, 60, 101, 53);
        var all = new[] { month, year, cbxM, cbxY, reg, brand, list };

        Assert.Equal("Expiration Month:", Astra.Api.Evidence.WorkbookAdapter.LabelForTest(cbxM, all));
        Assert.Equal("Expiration Year:", Astra.Api.Evidence.WorkbookAdapter.LabelForTest(cbxY, all));
        Assert.Equal("Brand:", Astra.Api.Evidence.WorkbookAdapter.LabelForTest(list, all));
        Assert.Null(Astra.Api.Evidence.WorkbookAdapter.LabelForTest(month, all));          // labels have no label
    }

    /// <summary>A form with one custom ActiveX control: its class (by CLSID) is in the form's class table, its site points at it.</summary>
    private static (byte[] F, byte[] O) FormWithCustomControl(Guid clsid)
    {
        var cls = new W().Start();
        cls.U16(0, false).U16(20, false).U32(1, false);                       // Version, cbClassTable (4 + one GUID), PropMask: fClsID
        cls.B.AddRange(clsid.ToByteArray());
        var site = Site("dtpBirth", 1, 8, 0, 0x8000, 500, 900);                // ClsidCacheIndex 0x8000 = class table entry 0; 8 bytes of its own data
        var f = new W().Start();
        f.U8(0).U8(4).U16(12, false).U32(1u << 10, false);                    // version 0.4, cbForm, mask: DisplayedSize
        f.U32(7000).U32(3000);
        f.U16(1, false);                                                       // one class
        f.B.AddRange(cls.B);
        f.U32(1, false).U32((uint)(4 + site.Length), false);                  // CountOfSites, CountOfBytes
        f.U8(0).U8(1).U8(0).U8(0);                                             // depth 0, one control of type 1, padding
        f.B.AddRange(site);
        return (f.B.ToArray(), new byte[8]);
    }

    [Fact]
    public void A_custom_ActiveX_control_is_named_by_its_class_and_does_not_spoil_the_form()
    {
        var (f, o) = FormWithCustomControl(new Guid("20DD1B9E-87C4-11D1-8BE3-0000F8754DA1"));
        var problems = new List<string>();
        var (_, _, controls) = UserForms.ReadLevel(f, o, _ => null, problems);
        Assert.Empty(problems);
        var c = Assert.Single(controls!);
        Assert.Equal(("dtpBirth", "DatePicker", 0), (c.Name, c.Kind, c.TabIndex));
        Assert.Equal((14.2, 25.5), (c.Left, c.Top));
        Assert.Null(c.Width);                                                  // its size is in its own opaque data
    }

    [Fact]
    public void An_unknown_ActiveX_class_keeps_its_id_instead_of_a_guess()
    {
        var (f, o) = FormWithCustomControl(new Guid("11111111-2222-3333-4444-555555555555"));
        var problems = new List<string>();
        var (_, _, controls) = UserForms.ReadLevel(f, o, _ => null, problems);
        Assert.Empty(problems);
        Assert.Equal("ActiveX {11111111-2222-3333-4444-555555555555}", Assert.Single(controls!).Kind);
    }

    [Fact]
    public void A_date_picker_takes_the_label_beside_it()
    {
        var label = C("lbDob", "Label", 12, 60, 66, 18, "Date of birth:");
        var picker = C("dtpDob", "DatePicker", 84, 60, 96, 18) with { Width = null, Height = null };
        Assert.Equal("Date of birth:", UserForms.LabelFor(picker, new[] { label, picker }));
    }

    /// <summary>With ASTRA_VBA_SAMPLES set: every control a form's code handles is one the designer data lists.</summary>
    [Fact]
    public void Local_samples_forms_match_their_code()
    {
        var root = Environment.GetEnvironmentVariable("ASTRA_VBA_SAMPLES");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
        foreach (var file in Directory.GetFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(p => OfficeVba.Extensions.Contains(Path.GetExtension(p).ToLowerInvariant())))
        {
            var r = OfficeVba.Read(File.ReadAllBytes(file), Path.GetFileName(file));
            foreach (var form in r.Project?.Modules.Where(m => m.Kind == VbaModuleKind.Form) ?? Enumerable.Empty<VbaModule>())
            {
                Assert.NotNull(form.Layout);
                Assert.True(form.Layout!.Complete, $"{form.Name}: {string.Join("; ", r.Warnings)}");
                var names = new HashSet<string>(Flat(form.Layout.Controls).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
                var handled = System.Text.RegularExpressions.Regex.Matches(form.Source, @"Sub\s+(\w+?)_(Click|Change|AfterUpdate|Exit|Enter|DblClick|KeyPress)\b")
                    .Select(m => m.Groups[1].Value).Where(n => !n.Equals("UserForm", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
                Assert.All(handled, n => Assert.Contains(n, names));
            }
        }

        static IEnumerable<FormControl> Flat(IEnumerable<FormControl> cs) => cs.SelectMany(c => new[] { c }.Concat(Flat(c.Children)));
    }
}
