// VB6 compatibility runtime — reviewed platform code, shipped in every faithful VB6 package.
//
// A converted form keeps VB6's shape: `txtName.Text = "x"`, `cboYear.AddItem "2024"`, `grd.TextMatrix(r, c)`.
// Each control on a form becomes one of these state objects (a field named exactly like the VB6 control); the
// .razor markup binds to them, and the converted event handlers read and write them as the VB6 code did. VB6's
// parenthesised indexed properties (`List(i)`, `TextMatrix(r, c)`, `ItemData(i)`, `Selected(i)`) become C#
// indexers: `cbo.List[i]`, `grd.TextMatrix[r, c]`. Nothing here draws anything; layout lives in the markup.
namespace Faithful.Vb6;

/// <summary>An indexed property with one index: VB6 <c>cbo.List(i)</c> → C# <c>cbo.List[i]</c>.</summary>
public sealed class Indexed<T>(Func<int, T> get, Action<int, T> set)
{
    public T this[int i] { get => get(i); set => set(i, value); }
}

/// <summary>An indexed property with two indexes: VB6 <c>grd.TextMatrix(r, c)</c> → C# <c>grd.TextMatrix[r, c]</c>.</summary>
public sealed class Indexed2<T>(Func<int, int, T> get, Action<int, int, T> set)
{
    public T this[int a, int b] { get => get(a, b); set => set(a, b, value); }
}

/// <summary>What every VB6 control has. Positions and sizes are kept in twips, as VB6 stored them.</summary>
public class VbControl
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool Visible { get; set; } = true;
    public string Tag { get; set; } = "";
    public string ToolTipText { get; set; } = "";
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int TabIndex { get; set; }
    public int ForeColor { get; set; }
    public int BackColor { get; set; }
    public int Index { get; set; } = -1;
    /// <summary>The UI asks for focus here; the markup moves it. A no-op outside a page.</summary>
    public bool FocusRequested { get; private set; }
    public void SetFocus() => FocusRequested = true;
    public void Refresh() { }
    public void ZOrder(int position = 0) { }
    public void Move(int left, int top = -1, int width = -1, int height = -1)
    {
        Left = left;
        if (top >= 0) Top = top;
        if (width >= 0) Width = width;
        if (height >= 0) Height = height;
    }
}

public class VbLabel : VbControl
{
    public string Caption { get; set; } = "";
    public int Alignment { get; set; }
}

public class VbTextBox : VbControl
{
    public string Text { get; set; } = "";
    public bool Locked { get; set; }
    public int MaxLength { get; set; }
    public int SelStart { get; set; }
    public int SelLength { get; set; }
    public string SelText { get; set; } = "";
}

public class VbCommandButton : VbControl
{
    public string Caption { get; set; } = "";
    public bool Value { get; set; }
    public bool Default { get; set; }
    public bool Cancel { get; set; }
}

/// <summary>VB6 CheckBox: Value is 0 (unchecked), 1 (checked) or 2 (grayed).</summary>
public class VbCheckBox : VbControl
{
    public string Caption { get; set; } = "";
    public int Value { get; set; }
}

public class VbOptionButton : VbControl
{
    public string Caption { get; set; } = "";
    public bool Value { get; set; }
}

public class VbFrame : VbControl
{
    public string Caption { get; set; } = "";
}

public class VbPictureBox : VbControl
{
    public string Picture { get; set; } = "";
    public void Cls() { }
}

public class VbTimer : VbControl
{
    public int Interval { get; set; }
}

public class VbProgressBar : VbControl
{
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public double Value { get; set; }
}

/// <summary>VB6 ListBox and ComboBox: an ordered list of strings, each with an ItemData number and a Selected flag.</summary>
public class VbListBox : VbControl
{
    private readonly List<string> _items = new();
    private readonly List<int> _data = new();
    private readonly List<bool> _selected = new();

    public VbListBox()
    {
        List = new Indexed<string>(i => _items[i], (i, v) => _items[i] = v);
        ItemData = new Indexed<int>(i => _data[i], (i, v) => _data[i] = v);
        Selected = new Indexed<bool>(i => _selected[i], (i, v) => _selected[i] = v);
    }

    public Indexed<string> List { get; }
    public Indexed<int> ItemData { get; }
    public Indexed<bool> Selected { get; }
    public int ListCount => _items.Count;
    /// <summary>-1 when nothing is selected, as in VB6.</summary>
    public int ListIndex { get; set; } = -1;
    public int NewIndex { get; private set; } = -1;
    public bool Sorted { get; set; }
    public int MultiSelect { get; set; }
    public int SelCount => _selected.Count(s => s);
    public IReadOnlyList<string> Items => _items;

    /// <summary>The selected item's text (VB6 <c>Text</c>); setting it selects a matching item.</summary>
    public virtual string Text
    {
        get => ListIndex >= 0 && ListIndex < _items.Count ? _items[ListIndex] : "";
        set => ListIndex = _items.FindIndex(x => string.Equals(x, value, StringComparison.Ordinal));
    }

    public void AddItem(string item, int index = -1)
    {
        if (index < 0 || index > _items.Count)
        {
            if (Sorted)
            {
                index = _items.FindIndex(x => string.Compare(x, item, StringComparison.OrdinalIgnoreCase) > 0);
                if (index < 0) index = _items.Count;
            }
            else index = _items.Count;
        }
        _items.Insert(index, item);
        _data.Insert(index, 0);
        _selected.Insert(index, false);
        NewIndex = index;
    }

    public void RemoveItem(int index)
    {
        _items.RemoveAt(index);
        _data.RemoveAt(index);
        _selected.RemoveAt(index);
        if (ListIndex >= _items.Count) ListIndex = _items.Count - 1;
    }

    public void Clear()
    {
        _items.Clear();
        _data.Clear();
        _selected.Clear();
        ListIndex = -1;
    }
}

/// <summary>VB6 ComboBox: a list plus free text (Style 0/1) or a fixed drop-down list (Style 2).</summary>
public class VbComboBox : VbListBox
{
    private string _text = "";
    public int Style { get; set; }
    public bool Locked { get; set; }
    public int SelStart { get; set; }
    public int SelLength { get; set; }

    public override string Text
    {
        get => Style == 2 || ListIndex >= 0 ? base.Text : _text;
        set
        {
            base.Text = value;
            _text = value;
        }
    }
}

/// <summary>
/// VSFlexGrid / MSFlexGrid: a grid of strings with fixed header rows and columns. VB6 code addresses it by
/// <c>Rows</c>, <c>Cols</c>, <c>Row</c>, <c>Col</c>, <c>TextMatrix(r, c)</c> and <c>Text</c> (the current cell).
/// </summary>
public class VbFlexGrid : VbControl
{
    private readonly List<List<string>> _cells = new();
    private readonly List<int> _colWidths = new();
    private int _cols = 2;

    public VbFlexGrid()
    {
        TextMatrix = new Indexed2<string>((r, c) => _cells[r][c], (r, c, v) => _cells[r][c] = v);
        ColWidth = new Indexed<int>(c => _colWidths[c], (c, v) => _colWidths[c] = v);
        RowHeight = new Indexed<int>(_ => 240, (_, _) => { });
        ColAlignment = new Indexed<int>(_ => 0, (_, _) => { });
        Rows = 2;
    }

    public Indexed2<string> TextMatrix { get; }
    public Indexed<int> ColWidth { get; }
    public Indexed<int> RowHeight { get; }
    public Indexed<int> ColAlignment { get; }
    public int FixedRows { get; set; } = 1;
    public int FixedCols { get; set; } = 1;
    public int Row { get; set; } = 1;
    public int Col { get; set; } = 1;
    public int RowSel { get; set; }
    public int ColSel { get; set; }
    public int TopRow { get; set; }
    public bool Redraw { get; set; } = true;
    public int CellBackColor { get; set; }
    public int CellForeColor { get; set; }
    public bool CellFontBold { get; set; }

    public int Rows
    {
        get => _cells.Count;
        set
        {
            while (_cells.Count < value) _cells.Add(Enumerable.Repeat("", _cols).ToList());
            if (_cells.Count > value) _cells.RemoveRange(value, _cells.Count - value);
        }
    }

    public int Cols
    {
        get => _cols;
        set
        {
            _cols = value;
            foreach (var row in _cells)
            {
                while (row.Count < value) row.Add("");
                if (row.Count > value) row.RemoveRange(value, row.Count - value);
            }
            while (_colWidths.Count < value) _colWidths.Add(1000);
            if (_colWidths.Count > value) _colWidths.RemoveRange(value, _colWidths.Count - value);
        }
    }

    /// <summary>The current cell's text (<c>Row</c>, <c>Col</c>).</summary>
    public string Text
    {
        get => _cells[Row][Col];
        set => _cells[Row][Col] = value;
    }

    /// <summary>Adds a row; <paramref name="item"/> holds the cells separated by tabs, as in VB6.</summary>
    public void AddItem(string item, int index = -1)
    {
        var row = item.Split('\t').Take(_cols).ToList();
        while (row.Count < _cols) row.Add("");
        if (index < 0 || index > _cells.Count) _cells.Add(row);
        else _cells.Insert(index, row);
    }

    public void RemoveItem(int index) => _cells.RemoveAt(index);

    /// <summary>Empties every cell, keeping the size (VB6 <c>Clear</c>).</summary>
    public void Clear()
    {
        foreach (var row in _cells)
            for (var c = 0; c < row.Count; c++) row[c] = "";
    }

    public IReadOnlyList<IReadOnlyList<string>> Cells => _cells;
}

/// <summary>A VB6 control array (<c>txtField(3).Text</c> → <c>txtField[3].Text</c>), indexed as VB6 indexed it.</summary>
public sealed class VbControlArray<T> where T : VbControl, new()
{
    private readonly SortedDictionary<int, T> _items = new();

    public T this[int index]
    {
        get
        {
            if (!_items.TryGetValue(index, out var c)) _items[index] = c = new T { Index = index };
            return c;
        }
    }

    public int Count => _items.Count;
    public int LBound() => _items.Count == 0 ? 0 : _items.Keys.First();
    public int UBound() => _items.Count == 0 ? -1 : _items.Keys.Last();
    /// <summary>VB6 <c>Load ctl(i)</c>: adds an element at run time.</summary>
    public void Load(int index) => _ = this[index];
    /// <summary>VB6 <c>Unload ctl(i)</c>.</summary>
    public void Unload(int index) => _items.Remove(index);
    public IEnumerable<T> Items => _items.Values;
}
