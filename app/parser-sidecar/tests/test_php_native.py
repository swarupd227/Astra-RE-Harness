"""
Tests for parser_sidecar.php_native — PHP read with the real grammar (nikic/PHP-Parser).

The first group needs no PHP: it feeds canned scanner output through the conversion to the sidecar's routine
inventory. The second group runs the scanner itself and is skipped where PHP or the library is not installed
(it always runs inside the sidecar image, which has both).
"""
from __future__ import annotations

import textwrap

import pytest

from parser_sidecar import php_native
from parser_sidecar.php_native import call_names, to_outcome

needs_php = pytest.mark.skipif(not php_native.available(), reason="PHP / nikic/php-parser not installed here")


# ──────────────────────────────────────────────────────────────────────
# Conversion (no PHP needed)
# ──────────────────────────────────────────────────────────────────────


def test_call_names_resolve_known_receivers_and_skip_guesses():
    calls = [
        {"kind": "method", "class": "App\\Services\\TaxCalculator", "method": "withTax"},
        {"kind": "method", "class": None, "method": "save"},                 # untyped receiver: not guessed
        {"kind": "static", "class": "App\\Models\\Invoice", "method": "where"},
        {"kind": "function", "name": "strlen", "builtin": True},            # PHP's own function: not a routine
        {"kind": "function", "name": "apply_discount", "builtin": False},
        {"kind": "new", "class": "App\\Services\\TaxCalculator"},
        {"kind": "include", "path": "db.php"},
        {"kind": "method", "class": "App\\Services\\TaxCalculator", "method": "withTax"},  # duplicate
    ]
    assert call_names(calls, None) == ["TaxCalculator.withTax", "Invoice.where", "apply_discount", "new:TaxCalculator"]


def test_outcome_has_methods_functions_and_the_scripts_own_statements():
    content = "\n".join(f"line {i}" for i in range(1, 31))
    summary = {
        "lineCount": 30,
        "ok": True,
        "errors": [],
        "routines": [
            {"name": "store", "class": "App\\Http\\Controllers\\InvoiceController", "lineStart": 5, "lineEnd": 9,
             "calls": [{"kind": "method", "class": "App\\Services\\TaxCalculator", "method": "withTax"}]},
            {"name": "helper", "class": None, "lineStart": 12, "lineEnd": 14, "calls": []},
        ],
        "classes": [{"name": "App\\Http\\Controllers\\InvoiceController"}],
        "top": {"lineStart": 20, "lineEnd": 28, "calls": [{"kind": "function", "name": "helper", "builtin": False}]},
    }
    out = to_outcome("legacy/api.php", content, summary)
    by = {s.name: s for s in out.subroutines}
    assert by["store"].common_block_refs == ("InvoiceController",)
    assert by["store"].called_subroutines == ("TaxCalculator.withTax",)
    assert by["store"].signature == "line 5"
    assert by["helper"].common_block_refs == ()
    # The file's own statements are a routine named after the file, so a front controller's dispatch is not lost.
    assert by["api"].line_start == 20 and by["api"].line_end == 28
    assert by["api"].called_subroutines == ("helper",)
    assert by["api"].signature == "PHP script legacy/api.php"


def test_syntax_errors_are_reported_as_a_warning():
    out = to_outcome("x.php", "<?php\n", {"lineCount": 1, "ok": False, "errors": ["Syntax error, unexpected '}' on line 9"],
                                           "routines": [], "classes": [], "top": None})
    assert out.warnings and "syntax error" in out.warnings[0]
    assert [s.name for s in out.subroutines] == ["x"]   # still catalogued, as one unit


def test_without_php_the_pattern_scanner_answers_and_says_so(monkeypatch):
    monkeypatch.setattr(php_native, "toolchain", lambda: None)
    outcomes, scans, warnings = php_native.parse_many([("a.php", "<?php\nfunction f() { return 1; }\n")])
    assert [s.name for s in outcomes[0].subroutines] == ["f"]
    assert scans == [{}]
    assert warnings and "pattern scanner" in warnings[0]


# ──────────────────────────────────────────────────────────────────────
# The real grammar (inside the sidecar image)
# ──────────────────────────────────────────────────────────────────────

_CONTROLLER = textwrap.dedent(r'''
    <?php
    namespace App\Http\Controllers;

    use App\Services\TaxCalculator;
    use App\Repositories\InvoiceRepository as Invoices;
    use Illuminate\Support\Facades\DB;

    #[Middleware('auth')]
    class InvoiceController extends Controller
    {
        public function __construct(private Invoices $invoices, private TaxCalculator $tax) {}

        public function store(Request $request)
        {
            $total = $this->tax->withTax($request->input('amount'), 'EU');
            $this->invoices->create(['total' => $total]);
            $audit = new AuditTrail();
            $audit->record('invoice.created');
            DB::table('invoices')->insert(['total' => $total]);
            return response()->json(['total' => strlen("x")], 201);
        }

        private function label(): string
        {
            $sql = <<<SQL
                SELECT { not a brace } FROM invoices
                SQL;
            return self::prefix() . $sql;
        }

        public static function prefix(): string { return 'INV-'; }
    }
''').strip() + "\n"


@needs_php
def test_methods_resolve_through_imports_typed_properties_and_locals():
    out = php_native.parse_one("app/Http/Controllers/InvoiceController.php", _CONTROLLER)
    by = {s.name: s for s in out.subroutines}
    assert set(by) >= {"__construct", "store", "label", "prefix"}
    calls = set(by["store"].called_subroutines)
    assert "TaxCalculator.withTax" in calls            # promoted constructor property, typed
    assert "InvoiceRepository.create" in calls         # `use ... as Invoices` alias resolved
    assert "new:AuditTrail" in calls
    assert "AuditTrail.record" in calls                # local assigned from `new`
    assert "DB.table" in calls                         # facade static call
    assert "response" in calls                         # a framework helper function (not PHP's own)
    assert "strlen" not in calls                       # PHP's own function
    assert "InvoiceController.prefix" in set(by["label"].called_subroutines)   # self::
    assert by["store"].common_block_refs == ("InvoiceController",)
    assert not out.warnings


@needs_php
def test_heredoc_braces_do_not_break_the_routine_boundaries():
    out = php_native.parse_one("C.php", _CONTROLLER)
    label = next(s for s in out.subroutines if s.name == "label")
    prefix = next(s for s in out.subroutines if s.name == "prefix")
    assert label.line_end < prefix.line_start


@needs_php
def test_a_front_controller_keeps_its_dispatch_as_a_routine():
    src = textwrap.dedent(r'''
        <?php
        require_once 'db.php';
        function get_customer($id) {
            $r = mysqli_query($GLOBALS['db'], "SELECT * FROM customers WHERE id = " . intval($id));
            return mysqli_fetch_assoc($r);
        }
        switch ($_REQUEST['action']) {
            case 'getCustomer': echo json_encode(get_customer($_GET['id'])); break;
            case 'ping': echo 'ok'; break;
        }
    ''').strip() + "\n"
    out = php_native.parse_one("legacy/api.php", src)
    by = {s.name: s for s in out.subroutines}
    assert "get_customer" in by
    assert "api" in by and by["api"].signature == "PHP script legacy/api.php"
    assert "get_customer" in by["api"].called_subroutines


@needs_php
def test_a_file_that_does_not_parse_is_still_catalogued_and_says_why():
    src = "<?php\nclass Broken {\n  public function ok() { return 1; }\n  public function bad( { \n}\n"
    out = php_native.parse_one("Broken.php", src)
    assert out.subroutines
    assert any("syntax" in w or "did not parse" in w for w in out.warnings)


@needs_php
def test_the_scan_states_sites_a_route_detector_can_read():
    src = textwrap.dedent(r'''
        <?php
        use App\Http\Controllers\InvoiceController;
        Route::get('/invoices/{id}', [InvoiceController::class, 'show'])->name('invoices.show');
        Route::post('/invoices', 'InvoiceController@store');
    ''').strip() + "\n"
    scans = php_native.scan([("routes/api.php", src)])
    top = scans[0]["top"]
    sites = [s for s in top["sites"] if s["type"] == "static"]
    get = next(s for s in sites if s["callee"].endswith("::get"))
    assert get["args"][0] == {"s": "/invoices/{id}"}
    assert get["args"][1]["arr"][0]["v"] == {"cls": "App\\Http\\Controllers\\InvoiceController"}
    assert get["args"][1]["arr"][1]["v"] == {"s": "show"}


@needs_php
def test_many_files_go_through_one_process_in_order():
    files = [(f"f{i}.php", f"<?php\nfunction f{i}() {{ return {i}; }}\n") for i in range(5)]
    outcomes, scans, warnings = php_native.parse_many(files)
    assert [o.subroutines[0].name for o in outcomes] == [f"f{i}" for i in range(5)]
    assert all(scans) and not warnings
