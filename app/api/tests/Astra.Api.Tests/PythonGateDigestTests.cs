using Astra.Api.Office;
using Astra.Api.Validation;
using Xunit;

namespace Astra.Api.Tests;

/// <summary>
/// The react-python gates' failures, read the way a repair round needs them; and the host block naming
/// what sets a cell. Both came from the first real-model run on Azure (a pytest failure the digest could
/// not see; a spec asking whether cells were backed by check boxes that the workbook shows they are).
/// </summary>
public class PythonGateDigestTests
{
    [Fact]
    public void A_pytest_failure_is_named_with_its_reason_for_the_thread_and_the_repair()
    {
        const string log = """
            === backend (pip install + pytest) ===
            >       with pytest.raises(ValidationError):
            E       Failed: DID NOT RAISE <class 'pydantic_core._pydantic_core.ValidationError'>
            =========================== short test summary info ============================
            PASSED tests/test_service.py::test_total_is_rounded
            FAILED tests/test_service.py::test_non_boolean_input_is_refused - Failed: DID NOT RAISE <class 'pydantic_core._pydantic_core.ValidationError'>
            1 failed, 25 passed, 1 warning in 0.50s
            === frontend (npm install + npm test / jest) ===
            Tests:       5 passed, 5 total
            """;
        var d = GateFailureDigest.Parse("TEST_PACK", log);
        var t = Assert.Single(d.FailedTests);
        Assert.Equal("tests/test_service.py::test_non_boolean_input_is_refused", t.Name);
        Assert.StartsWith("Failed: DID NOT RAISE", t.Message);
        Assert.Contains("`test_non_boolean_input_is_refused`", d.ToMarkdown("Selections"));
        Assert.Contains("test_non_boolean_input_is_refused: Failed: DID NOT RAISE", d.ToRepairHint());
    }

    [Fact]
    public void A_module_that_fails_to_import_is_a_compile_error_with_its_exception()
    {
        const string log = """
            === import every module ===
            import ok   app
            import FAIL app.service
            Traceback (most recent call last):
              File "/tmp/x/backend/app/service.py", line 3, in <module>
                from app.tariffs import RatePort
            ModuleNotFoundError: No module named 'app.tariffs'
            import ok   app.models
            === exit 1 ===
            """;
        var d = GateFailureDigest.Parse("COMPILE", log);
        var e = Assert.Single(d.Errors);
        Assert.Equal(("app/service.py", "ModuleNotFoundError"), (e.File, e.Code));
        Assert.Contains("No module named 'app.tariffs'", e.Message);
    }

    [Fact]
    public void A_named_cell_says_which_control_sets_it()
    {
        var sheet = new WorkbookSheet("Install", "Install", "visible", "A1:D30", 10, 0,
            Array.Empty<WorkbookFormula>(), Array.Empty<WorkbookValidation>(),
            new[] { new WorkbookControl("Check Box 3", "form", "CheckBox", "Source code", null, null, null, false, "$C$12", null, Array.Empty<string>()) },
            Array.Empty<WorkbookTable>(), Array.Empty<string>());
        var r = new WorkbookSurfaceResult("Installer.xlsm", new[] { sheet },
            new[] { new WorkbookName("InstallSrc", "Install!$C$12", null, false, null) },
            Array.Empty<WorkbookEvent>(), Array.Empty<WorkbookCodeReach>(), Array.Empty<WorkbookExternal>(), null, Array.Empty<string>());
        Assert.Contains("- InstallSrc = Install!$C$12 — set by the CheckBox \"Source code\" on sheet \"Install\"", OfficeHostContext.Build(r, "Install.cls", "Selections"));
    }
}
