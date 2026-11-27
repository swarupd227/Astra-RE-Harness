// SHAPE EXEMPLAR — never shipped in a generated package.
//
// Faithful 1:1 conversion of a small VB6 standard module (.bas), showing every convention the converter
// follows. A class module (.cls) converts the same way except that it becomes an instance class (shown at the end).
// The VB6 source it mirrors (modRates.bas):
//
//    1  Attribute VB_Name = "modRates"
//    2  Option Explicit
//    3  Public Type RateRec
//    4      Code As String * 4
//    5      Amount As Currency
//    6  End Type
//    7  Public gsDataPath As String
//    8  Private mlCount As Long
//    9  Public Const MAX_RATES = 50
//   10
//   11  Public Function LoadRates(ByVal sFile As String, aRates() As RateRec) As Integer
//   12      Dim iFile As Integer, sLine As String, n As Integer
//   13      On Error GoTo ErrLoad
//   14      iFile = FreeFile
//   15      Open gsDataPath & "\" & sFile For Input As #iFile
//   16      Do While Not EOF(iFile)
//   17          Line Input #iFile, sLine
//   18          If Len(Trim$(sLine)) > 0 Then
//   19              n = n + 1
//   20              ReDim Preserve aRates(1 To n)
//   21              aRates(n).Code = Left$(sLine, 4)
//   22              aRates(n).Amount = CCur(Val(Mid$(sLine, 5)))
//   23          End If
//   24      Loop
//   25      Close #iFile
//   26      mlCount = n
//   27      LoadRates = n
//   28      Exit Function
//   29  ErrLoad:
//   30      MsgBox "Cannot read " & sFile & ": " & Err.Description, vbExclamation
//   31      LoadRates = -1
//   32  End Function
//   33
//   34  Public Sub ShowRates()
//   35      frmRates.Show vbModal
//   36  End Sub
//
// Conventions, in the order a reviewer meets them:
//   1. One unit → one file. Every unit of a VB6 project shares ONE namespace, `Faithful.Vb6App`, because VB6 has
//      one project-wide scope. A .bas module becomes `public static partial class <VB_Name>`; a .cls becomes
//      `public partial class <VB_Name>`; names keep their VB6 spelling exactly (gsDataPath, mlCount, LoadRates,
//      RateRec). Locals and parameters keep their names too (sFile, iFile). VB6 lets code call a public member of
//      any module without naming the module: add `using static Faithful.Vb6App.<Module>;` for each module whose
//      members the unit calls that way, so the call sites read the same as the VB6.
//   2. Members appear in source order. Every member cites its lines with [SourceRoutine]; every type has [SourceUnit].
//   3. Types: Integer → short, Long → int, Single → float, Double → double, Currency → decimal, Byte → byte,
//      Boolean → bool, String / String * n → string (a fixed-length string is padded/cut with Strings.LSet when
//      assigned, comment `// String * 4`), Date → DateTime, Variant / Object → object.
//      User-defined Type → `public struct` with public fields (value semantics, as VB6).
//   4. VB6 semantics are kept, not "improved": arrays keep their declared bounds — `(1 To n)` arrays are allocated
//      n+1 long and index 1..n is used, index 0 left unused, with `// 1-based`; ReDim Preserve → Array.Resize;
//      `\` integer division → `/` on integers; `Mod` → `%`; `&` → string concatenation; `=` comparisons of strings
//      are ordinal (Option Compare Binary); And/Or on Booleans → `&`/`|` (VB6 does not short-circuit).
//   5. Built-in functions use .NET's Microsoft.VisualBasic library, which has VB6's semantics: Strings.Mid/Left/
//      Right/Len/Trim/InStr/Replace/UCase/Format, Conversion.Val/Int/Fix, Information.IsNumeric/IsDate/UBound,
//      DateAndTime.DateAdd/DateDiff/Now, and FileSystem.FreeFile/FileOpen/LineInput/PrintLine/EOF/FileClose for
//      VB-style file I/O (record and binary files too: FileGet/FilePut). Never re-implement them.
//   6. What .NET lacks off Windows comes from Faithful.Vb6 (src/Vb6Runtime, already in the package): Vb.MsgBox /
//      Vb.InputBox (answered by App.Dialogs), App.Path / App.Title, Err (Number, Description, Raise, Clear, Set),
//      Vb.DoEvents, default form instances Forms.Get<frmX>(), VbException.
//   7. Error handling: `On Error GoTo L` → try { body } catch (Exception ex) { Err.Set(ex); handler body };
//      `Resume Next` inside a handler or `On Error Resume Next` → a try/catch around each statement that can fail,
//      with `// On Error Resume Next`; `Exit Function` inside the body → return.
//   8. A function's return value: VB6 assigns to the function name; C# keeps a local named after the function's
//      result (`short loadRates = 0;`) and returns it at every exit, so the order of assignments is unchanged.
//   9. ByRef is VB6's default: a parameter without ByVal that the routine assigns becomes `ref`; arrays are ref when
//      the routine ReDims them. Optional parameters keep their defaults; `IsMissing` → a nullable parameter.
//  10. Foreign things are stubbed, not guessed: a routine, variable or class of another unit of this project →
//      a stub under src/Stubs/Units/<VB_Name>.cs in namespace Faithful.Vb6App with the class name and kind the
//      real conversion will have (static partial class for a module, partial class for a class, a VbForm for a
//      form), members limited to what this unit uses, bodies throwing NotImplementedException. A type from a
//      referenced DLL or OCX → a stub under src/Stubs/<Library>/<Type>.cs in namespace Faithful.External.<Library>.
//      The assembled application replaces each Units stub with the real conversion.
namespace Faithful.Vb6App;

using Faithful.Provenance;
using Faithful.Vb6;
using Microsoft.VisualBasic;

[SourceUnit("Modules/modRates.bas")]
public struct RateRec
{
    public string Code;      // String * 4
    public decimal Amount;   // Currency
}

[SourceUnit("Modules/modRates.bas")]
public static partial class modRates
{
    public static string gsDataPath = "";
    private static int mlCount;
    public const short MAX_RATES = 50;

    [SourceRoutine("Modules/modRates.bas", "LoadRates", 11, 32)]
    public static short LoadRates(string sFile, ref RateRec[] aRates)
    {
        // TODO(faithful): no signed spec — behaviour taken from source only
        short loadRates = 0;
        short iFile = 0;
        string sLine = "";
        short n = 0;
        try
        {
            iFile = (short)FileSystem.FreeFile();
            FileSystem.FileOpen(iFile, gsDataPath + "\\" + sFile, OpenMode.Input);
            while (!FileSystem.EOF(iFile))
            {
                sLine = FileSystem.LineInput(iFile);
                if (Strings.Len(Strings.Trim(sLine)) > 0)
                {
                    n = (short)(n + 1);
                    Array.Resize(ref aRates, n + 1);   // ReDim Preserve aRates(1 To n) — 1-based
                    aRates[n].Code = Strings.LSet(Strings.Left(sLine, 4), 4);
                    aRates[n].Amount = (decimal)Conversion.Val(Strings.Mid(sLine, 5));
                }
            }
            FileSystem.FileClose(iFile);
            mlCount = n;
            loadRates = n;
            return loadRates;   // Exit Function
        }
        catch (Exception ex)
        {
            Err.Set(ex);   // ErrLoad:
            Vb.MsgBox("Cannot read " + sFile + ": " + Err.Description, VbMsgBoxStyle.vbExclamation);
            loadRates = -1;
        }
        return loadRates;
    }

    [SourceRoutine("Modules/modRates.bas", "ShowRates", 34, 36)]
    public static void ShowRates()
    {
        // TODO(faithful): no signed spec — behaviour taken from source only
        // TODO(faithful): modal form — in VB6 the next line waited for frmRates to close; the web host shows it.
        Forms.Get<frmRates>().Show(Vb.vbModal);
    }
}

// A class module (clsBasket.cls) converts to an instance class with the same members in the same order;
// Class_Initialize → constructor, Class_Terminate → Dispose (and IDisposable), Property Get/Let/Set → one C#
// property with get/set, Public variables → public fields, Private variables → private fields.
//
//   Attribute VB_Name = "clsBasket"
//   Private mcTotal As Currency
//   Private Sub Class_Initialize(): mcTotal = 0: End Sub
//   Public Property Get Total() As Currency: Total = mcTotal: End Property
//   Public Sub Add(ByVal cAmount As Currency): mcTotal = mcTotal + cAmount: End Sub
[SourceUnit("Classes/clsBasket.cls")]
public partial class clsBasket
{
    private decimal mcTotal;

    [SourceRoutine("Classes/clsBasket.cls", "Class_Initialize", 3, 3)]
    public clsBasket()
    {
        mcTotal = 0;
    }

    [SourceRoutine("Classes/clsBasket.cls", "Total", 4, 4)]
    public decimal Total => mcTotal;

    [SourceRoutine("Classes/clsBasket.cls", "Add", 5, 5)]
    public void Add(decimal cAmount)
    {
        mcTotal = mcTotal + cAmount;
    }
}

// frmRates is another unit of the project (its conversion is shown in frmRates.razor / frmRates.razor.cs). Had it
// not been converted, this package would carry src/Stubs/Units/frmRates.cs:
//
//   // TODO(faithful): stub for frmRates — replaced by the real conversion in the assembled application.
//   namespace Faithful.Vb6App;
//   using Faithful.Vb6;
//   public partial class frmRates : VbForm { }
