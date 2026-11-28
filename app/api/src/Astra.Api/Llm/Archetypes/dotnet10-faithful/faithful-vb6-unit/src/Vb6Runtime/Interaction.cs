// VB6 compatibility runtime — reviewed platform code, shipped in every faithful VB6 package.
//
// What VB6 gave every program for free and .NET does not: a message box that can be answered (in a web app, by a
// dialog the page shows), the App object, default form instances that any code can Show, the Err object behind
// On Error, and DoEvents. String, date, conversion and file functions are NOT here — .NET's own Microsoft.VisualBasic
// library (Strings.Mid, Information.IsNumeric, Conversion.Val, FileSystem.FileOpen/LineInput/Print, DateAndTime…)
// already implements them with VB6's semantics, and converted code uses it directly.
using Microsoft.AspNetCore.Components;

namespace Faithful.Vb6;

/// <summary>VB6 MsgBox button and icon flags (same values as VB6).</summary>
[Flags]
public enum VbMsgBoxStyle
{
    vbOKOnly = 0, vbOKCancel = 1, vbAbortRetryIgnore = 2, vbYesNoCancel = 3, vbYesNo = 4, vbRetryCancel = 5,
    vbCritical = 16, vbQuestion = 32, vbExclamation = 48, vbInformation = 64,
    vbDefaultButton2 = 256, vbDefaultButton3 = 512, vbApplicationModal = 0, vbSystemModal = 4096,
}

/// <summary>VB6 MsgBox answers (same values as VB6).</summary>
public enum VbMsgBoxResult { vbOK = 1, vbCancel = 2, vbAbort = 3, vbRetry = 4, vbIgnore = 5, vbYes = 6, vbNo = 7 }

/// <summary>Who answers a message box. A web host shows a dialog; a test scripts the answers.</summary>
public interface IVbDialogs
{
    VbMsgBoxResult MsgBox(string prompt, VbMsgBoxStyle buttons, string title);
    string InputBox(string prompt, string title, string defaultResponse);
}

/// <summary>
/// The default when nothing else answers: every message is recorded and the default button is "pressed"
/// (the first button, or the second/third when the style says so). Tests read <see cref="Messages"/> and can queue
/// answers with <see cref="Answers"/>.
/// </summary>
public sealed class RecordingDialogs : IVbDialogs
{
    public List<(string Prompt, VbMsgBoxStyle Buttons, string Title)> Messages { get; } = new();
    public Queue<VbMsgBoxResult> Answers { get; } = new();
    public Queue<string> InputAnswers { get; } = new();

    public VbMsgBoxResult MsgBox(string prompt, VbMsgBoxStyle buttons, string title)
    {
        Messages.Add((prompt, buttons, title));
        if (Answers.Count > 0) return Answers.Dequeue();
        var set = (int)buttons & 7;
        var second = ((int)buttons & 256) != 0;
        var third = ((int)buttons & 512) != 0;
        return set switch
        {
            1 => second ? VbMsgBoxResult.vbCancel : VbMsgBoxResult.vbOK,
            2 => third ? VbMsgBoxResult.vbIgnore : second ? VbMsgBoxResult.vbRetry : VbMsgBoxResult.vbAbort,
            3 => third ? VbMsgBoxResult.vbCancel : second ? VbMsgBoxResult.vbNo : VbMsgBoxResult.vbYes,
            4 => second ? VbMsgBoxResult.vbNo : VbMsgBoxResult.vbYes,
            5 => second ? VbMsgBoxResult.vbCancel : VbMsgBoxResult.vbRetry,
            _ => VbMsgBoxResult.vbOK,
        };
    }

    public string InputBox(string prompt, string title, string defaultResponse) =>
        InputAnswers.Count > 0 ? InputAnswers.Dequeue() : defaultResponse;
}

/// <summary>A VB6 run-time error (<c>Err.Raise</c>, or an error a converted statement reports with its VB6 number).</summary>
public sealed class VbException(int number, string description, string source = "") : Exception(description)
{
    public int Number { get; } = number;
    public string VbSource { get; } = source;
}

/// <summary>VB6's <c>Err</c> object. In a <c>catch</c> that replaces <c>On Error GoTo</c>, call <see cref="Set"/> first.</summary>
public static class Err
{
    [ThreadStatic] private static int _number;
    [ThreadStatic] private static string? _description;
    [ThreadStatic] private static string? _source;

    public static int Number { get => _number; set => _number = value; }
    public static string Description { get => _description ?? ""; set => _description = value; }
    public static string Source { get => _source ?? ""; set => _source = value; }

    /// <summary>Records the error a <c>catch</c> caught, so the converted handler reads it as VB6 code read Err.</summary>
    public static void Set(Exception ex)
    {
        _number = ex is VbException v ? v.Number : ex switch
        {
            DivideByZeroException => 11,
            OverflowException => 6,
            IndexOutOfRangeException or ArgumentOutOfRangeException => 9,
            InvalidCastException or FormatException => 13,
            FileNotFoundException => 53,
            DirectoryNotFoundException => 76,
            NullReferenceException => 91,
            UnauthorizedAccessException => 70,
            IOException => 57,
            _ => 5,
        };
        _description = ex.Message;
        _source = ex is VbException ve ? ve.VbSource : ex.Source ?? "";
    }

    public static void Clear()
    {
        _number = 0;
        _description = null;
        _source = null;
    }

    public static void Raise(int number, string source = "", string description = "") =>
        throw new VbException(number, description.Length > 0 ? description : $"Application-defined or object-defined error {number}", source);
}

/// <summary>VB6's <c>App</c> object and the program-wide services a converted unit reaches for.</summary>
public static class App
{
    /// <summary>Where the program's data files are (VB6 <c>App.Path</c>): set by the host, the executable's folder by default.</summary>
    public static string Path { get; set; } = AppContext.BaseDirectory.TrimEnd('/', '\\');
    public static string Title { get; set; } = "Application";
    public static string EXEName { get; set; } = "Application";
    public static int Major { get; set; } = 1;
    public static int Minor { get; set; }
    public static int Revision { get; set; }
    public static bool PrevInstance { get; set; }
    public static string HelpFile { get; set; } = "";
    public static string CompanyName { get; set; } = "";
    public static string ProductName { get; set; } = "";
    public static string FileDescription { get; set; } = "";
    public static string Comments { get; set; } = "";
    public static int hInstance => 0;
    public static int LogMode => 0;
    /// <summary>VB6 <c>App.LogEvent</c>: written to the process log (standard error).</summary>
    public static void LogEvent(string message, int eventType = 0) => Console.Error.WriteLine($"[{eventType}] {message}");
    public static IVbDialogs Dialogs { get; set; } = new RecordingDialogs();
    /// <summary>Raised when code shows a form (name, modal). The web host navigates or opens a dialog.</summary>
    public static event Action<string, bool>? FormShown;
    internal static void RaiseShown(string name, bool modal) => FormShown?.Invoke(name, modal);
}

/// <summary>Functions VB6 had built in that .NET's Microsoft.VisualBasic does not provide off Windows.</summary>
public static class Vb
{
    public const int vbModal = 1;
    public const int vbModeless = 0;

    public static VbMsgBoxResult MsgBox(object? prompt, VbMsgBoxStyle buttons = VbMsgBoxStyle.vbOKOnly, string? title = null) =>
        App.Dialogs.MsgBox(Convert.ToString(prompt) ?? "", buttons, title ?? App.Title);

    public static string InputBox(string prompt, string? title = null, string defaultResponse = "") =>
        App.Dialogs.InputBox(prompt, title ?? App.Title, defaultResponse);

    /// <summary>Lets the UI repaint in VB6; a web page repaints on its own, so nothing to do.</summary>
    public static void DoEvents() { }

    /// <summary>VB6 <c>Screen.MousePointer</c>: the host may show a busy cursor; kept so the converted code reads the same.</summary>
    public static int MousePointer { get; set; }
}

/// <summary>
/// A converted VB6 form's logic: a plain class whose fields are the form's controls and whose methods are its event
/// handlers, with the form's VB_Name (so tests drive it without a browser). <c>Form_Load</c> runs the first time the
/// form is loaded, as in VB6 (any reference to a default instance loads it). The page that draws it is a separate
/// component, <c>View_&lt;VB_Name&gt;</c>, deriving from <see cref="VbFormView{TForm}"/>.
/// </summary>
public abstract class VbForm
{
    public bool Loaded { get; private set; }

    /// <summary>VB6 <c>Load frmX</c>: runs Form_Load once.</summary>
    public void Load()
    {
        if (Loaded) return;
        Loaded = true;
        Form_Load();
    }

    public string Caption { get; set; } = "";
    public bool Visible { get; private set; }
    public bool Enabled { get; set; } = true;
    public int WindowState { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Tag { get; set; } = "";
    public bool KeyPreview { get; set; }
    public int BackColor { get; set; } = VbColor.vbButtonFace;
    public int ForeColor { get; set; } = VbColor.vbButtonText;
    public int BorderStyle { get; set; } = 2;
    public bool ControlBox { get; set; } = true;
    public bool MinButton { get; set; } = true;
    public bool MaxButton { get; set; } = true;
    public bool Moveable { get; set; } = true;
    public bool ShowInTaskbar { get; set; } = true;
    public int StartUpPosition { get; set; }
    public int ScaleMode { get; set; } = 1;
    public int ScaleWidth { get; set; }
    public int ScaleHeight { get; set; }
    public int MousePointer { get; set; }
    public string FontName { get; set; } = "MS Sans Serif";
    public float FontSize { get; set; } = 8.25f;
    public bool FontBold { get; set; }
    public string Icon { get; set; } = "";
    public int HelpContextID { get; set; }
    /// <summary>The control that has the focus (the page sets it; VB6 code reads it, e.g. in Form_KeyDown).</summary>
    public VbControl? ActiveControl { get; set; }

    /// <summary>VB6 <c>Me.Controls</c>: every control field of the form, found by reflection, in declaration order.</summary>
    public IReadOnlyList<VbControl> Controls =>
        GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Select(f => f.GetValue(this)).OfType<VbControl>().ToList();

    public void SetFocus() { }
    public void Cls() { }
    public void PrintForm() { }

    /// <summary>The form's VB_Name, which is also its route segment (<c>/forms/frmName</c>).</summary>
    public virtual string FormName => GetType().Name;

    /// <summary>Called once, as VB6 called <c>Form_Load</c>; a converted form overrides it.</summary>
    protected virtual void Form_Load() { }

    /// <summary>Called by VB6's <c>Unload</c>; a converted form overrides Form_Unload when it had one.</summary>
    protected virtual void Form_Unload(ref short cancel) { }

    public void Show(int modal = 0)
    {
        Load();
        Visible = true;
        App.RaiseShown(FormName, modal == Vb.vbModal);
    }

    public void Hide() => Visible = false;

    /// <summary>VB6 <c>Unload Me</c>: Form_Unload may cancel; otherwise the form is hidden and the next use loads it again.</summary>
    public void Unload()
    {
        short cancel = 0;
        Form_Unload(ref cancel);
        if (cancel != 0) return;
        Visible = false;
        Loaded = false;
    }

    /// <summary>Raised when the form's state changed and its page should redraw (VB6 repainted on its own).</summary>
    public event Action? Changed;
    public void Refresh() => Changed?.Invoke();
}

/// <summary>
/// The page that draws a converted form: <c>View_&lt;VB_Name&gt;.razor</c> inherits this, reads and binds
/// <see cref="Form"/>'s controls, and calls its handlers. Razor component names must start with a capital letter,
/// which VB6 form names (frmX) do not, so the view carries the prefix and the form keeps its own name.
/// </summary>
public abstract class VbFormView<TForm> : ComponentBase, IDisposable where TForm : VbForm, new()
{
    protected TForm Form { get; } = Forms.Get<TForm>();

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Form.Load();
        Form.Changed += Redraw;
    }

    private void Redraw() => InvokeAsync(StateHasChanged);

    public void Dispose() => Form.Changed -= Redraw;
}

/// <summary>VB6's built-in constants that .NET's Microsoft.VisualBasic does not define, with VB6's values. Plain
/// int constants, so they assign to int properties and compare with ints exactly as in VB6.</summary>
public static class VbMousePointer
{
    public const int vbDefault = 0, vbNormal = 0, vbArrow = 1, vbCrosshair = 2, vbIbeam = 3, vbIconPointer = 4,
        vbSizePointer = 5, vbSizeNESW = 6, vbSizeNS = 7, vbSizeNWSE = 8, vbSizeWE = 9, vbUpArrow = 10,
        vbHourglass = 11, vbNoDrop = 12, vbArrowHourglass = 13, vbArrowQuestion = 14, vbSizeAll = 15, vbCustom = 99;
}

public static class VbKey
{
    public const int vbKeyBack = 8, vbKeyTab = 9, vbKeyReturn = 13, vbKeyShift = 16, vbKeyControl = 17, vbKeyMenu = 18,
        vbKeyPause = 19, vbKeyEscape = 27, vbKeySpace = 32, vbKeyPageUp = 33, vbKeyPageDown = 34, vbKeyEnd = 35,
        vbKeyHome = 36, vbKeyLeft = 37, vbKeyUp = 38, vbKeyRight = 39, vbKeyDown = 40, vbKeyInsert = 45, vbKeyDelete = 46,
        vbKey0 = 48, vbKey1 = 49, vbKey2 = 50, vbKey3 = 51, vbKey4 = 52, vbKey5 = 53, vbKey6 = 54, vbKey7 = 55, vbKey8 = 56, vbKey9 = 57,
        vbKeyA = 65, vbKeyB = 66, vbKeyC = 67, vbKeyD = 68, vbKeyE = 69, vbKeyF = 70, vbKeyG = 71, vbKeyH = 72, vbKeyI = 73,
        vbKeyJ = 74, vbKeyK = 75, vbKeyL = 76, vbKeyM = 77, vbKeyN = 78, vbKeyO = 79, vbKeyP = 80, vbKeyQ = 81, vbKeyR = 82,
        vbKeyS = 83, vbKeyT = 84, vbKeyU = 85, vbKeyV = 86, vbKeyW = 87, vbKeyX = 88, vbKeyY = 89, vbKeyZ = 90,
        vbKeyF1 = 112, vbKeyF2 = 113, vbKeyF3 = 114, vbKeyF4 = 115, vbKeyF5 = 116, vbKeyF6 = 117, vbKeyF7 = 118,
        vbKeyF8 = 119, vbKeyF9 = 120, vbKeyF10 = 121, vbKeyF11 = 122, vbKeyF12 = 123;
    public const int vbShiftMask = 1, vbCtrlMask = 2, vbAltMask = 4;
    public const int vbLeftButton = 1, vbRightButton = 2, vbMiddleButton = 4;
}

public static class VbColor
{
    public const int vbBlack = 0x0, vbRed = 0xFF, vbGreen = 0xFF00, vbYellow = 0xFFFF, vbBlue = 0xFF0000,
        vbMagenta = 0xFF00FF, vbCyan = 0xFFFF00, vbWhite = 0xFFFFFF;
    // System colours (VB6 stores them as 0x80000000 | index).
    public const int vbButtonFace = unchecked((int)0x8000000F), vbButtonText = unchecked((int)0x80000012),
        vbWindowBackground = unchecked((int)0x80000005), vbWindowText = unchecked((int)0x80000008),
        vbHighlight = unchecked((int)0x8000000D), vbHighlightText = unchecked((int)0x8000000E),
        vbGrayText = unchecked((int)0x80000011), vbInfoBackground = unchecked((int)0x80000018);
}

/// <summary>Other VB6 constants: form/window states, check box values, alignment, query-unload reasons, drag.</summary>
public static class VbConst
{
    public const int vbNormal = 0, vbMinimized = 1, vbMaximized = 2;
    public const int vbUnchecked = 0, vbChecked = 1, vbGrayed = 2;
    public const int vbLeftJustify = 0, vbRightJustify = 1, vbCenter = 2;
    public const int vbFormControlMenu = 0, vbFormCode = 1, vbAppWindows = 2, vbAppTaskManager = 3, vbFormMDIForm = 4;
    public const int vbTwips = 1, vbPoints = 2, vbPixels = 3;
    public const int vbBeginDrag = 1, vbEndDrag = 2, vbCancel = 0;
    public const int vbModal = 1, vbModeless = 0;
}

/// <summary>VB6's <c>Screen</c> object: the mouse pointer and what is active, as the host reports them.</summary>
public static class Screen
{
    public static int MousePointer { get => Vb.MousePointer; set => Vb.MousePointer = value; }
    public static VbForm? ActiveForm { get; set; }
    public static VbControl? ActiveControl { get; set; }
    public static int Width { get; set; } = 1920 * 15;
    public static int Height { get; set; } = 1080 * 15;
    public static int TwipsPerPixelX => 15;
    public static int TwipsPerPixelY => 15;
}

/// <summary>A control's place on the page: VB6 twips (15 per pixel) as absolute CSS, so the form keeps its layout.</summary>
public static class Layout
{
    public static int Px(int twips) => (int)Math.Round(twips / 15.0);

    public static string Style(VbControl c) =>
        $"position:absolute;left:{Px(c.Left)}px;top:{Px(c.Top)}px;width:{Px(c.Width)}px;height:{Px(c.Height)}px;" +
        (c.Visible ? "" : "display:none;");

    public static string FormStyle(VbForm f) =>
        $"position:relative;width:{Px(f.Width)}px;height:{Px(f.Height)}px;";
}

/// <summary>VB6 default form instances: <c>frmX.Show</c> anywhere in the program means the one instance of frmX.</summary>
public static class Forms
{
    private static readonly Dictionary<Type, VbForm> Instances = new();

    public static T Get<T>() where T : VbForm, new()
    {
        lock (Instances)
        {
            if (!Instances.TryGetValue(typeof(T), out var f)) Instances[typeof(T)] = f = new T();
            f.Load();   // VB6 loads a form the first time anything refers to it
            return (T)f;
        }
    }

    /// <summary>VB6 <c>Set frmX = Nothing</c>: the next use gets a fresh instance.</summary>
    public static void Release<T>() where T : VbForm
    {
        lock (Instances) Instances.Remove(typeof(T));
    }
}
