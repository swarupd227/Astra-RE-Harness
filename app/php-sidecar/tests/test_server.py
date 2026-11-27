"""PHP sidecar suite — runs inside the sidecar image (`docker build --target test`), against the real php binary."""

from __future__ import annotations

from pathlib import Path

from fastapi.testclient import TestClient

from php_sidecar import server

client = TestClient(server.app)

QTY = """<?php
namespace Shop;

use Shop\\Lib\\Money;

echo "TOP-LEVEL OUTPUT";
$GLOBALS['ran_top_level'] = true;

function resolveQty($input) {
    $qty = $input['qty'] ?? 1;
    if (empty($qty)) $qty = 1;
    return (int)$qty;
}

function postedQty() {
    return isset($_POST['qty']) ? (int)$_POST['qty'] : -1;
}

function redirect_home() {
    header('Location: /home');
    http_response_code(302);
    echo "moved";
}

function addLine(array &$cart, string $sku, int $qty = 1): int {
    $cart[$sku] = ($cart[$sku] ?? 0) + $qty;
    $_SESSION['cart'] = $cart;
    return count($cart);
}

function topLevelRan() { return isset($GLOBALS['ran_top_level']); }

function boom($x) { if ($x < 0) throw new \\InvalidArgumentException("negative"); return $x; }

function forever() { while (true) {} }

function hog() { return strlen(str_repeat('x', 1024 * 1024 * 1024)); }

function shell() { return shell_exec('id'); }

function remote() { return @file_get_contents('http://example.com/'); }

function secrets() { return @file_get_contents('/etc/passwd'); }

function quit() { echo "bye"; exit; }

function money($a, $b) { return Money::format($a + $b); }

class Cart {
    public function __construct(private string $currency) {}
    public function total(array $prices): string { return Money::format(array_sum($prices)) . ' ' . ($this->currency ?? '?'); }
    public static function lines(): array { return ['a' => 1, 'b' => [1, 2]]; }
}
"""

MONEY = """<?php
namespace Shop\\Lib;

final class Money {
    public static function format(float $v): string { return number_format($v, 2, '.', ''); }
}
"""

GUARDED = """<?php
if (!function_exists('legacy_round')) {
    function legacy_round($v) { return round($v, 1); }
}
"""


def run(name: str, vectors: list[dict], **kw) -> dict:
    body = {
        "files": [{"path": "src/qty.php", "content": QTY}, {"path": "src/lib/money.php", "content": MONEY},
                  {"path": "legacy.php", "content": GUARDED}],
        "entry": {"name": name, "path": "src/qty.php"},
        "vectors": vectors,
        **kw,
    }
    r = client.post("/run", json=body)
    assert r.status_code == 200, r.text
    return r.json()


def test_health_reports_php_84_and_the_cgi_sapi():
    h = client.get("/health").json()
    assert h["service"] == "astra-php"
    assert h["php"].startswith("8.4")
    assert h["sapi"] == "cgi"


def test_runs_a_function_with_named_inputs_lifted_into_its_one_array_parameter():
    res = run("resolveQty", [{"id": "a", "named": {"qty": "3"}}, {"id": "b", "named": {"qty": "0"}}, {"id": "c", "named": {}}])
    by = {r["id"]: r for r in res["results"]}
    assert by["a"]["returnValue"] == 3
    assert by["b"]["returnValue"] == 1          # empty("0") is true in PHP
    assert by["a"]["notes"] == ["inputs passed as the keys of $input"]
    assert res["routine"]["params"][0]["name"] == "input"


def test_top_level_statements_never_run():
    res = run("topLevelRan", [{"id": "t", "args": []}])
    r = res["results"][0]
    assert r["returnValue"] is False
    assert "TOP-LEVEL" not in r["output"]


def test_positional_args_and_superglobals():
    res = run("resolveQty", [{"id": "p", "args": [{"qty": "7"}]}])
    assert res["results"][0]["returnValue"] == 7
    res = run("postedQty", [{"id": "s", "args": [], "superglobals": {"post": {"qty": "5"}}}, {"id": "n", "args": []}])
    assert [r["returnValue"] for r in res["results"]] == [5, -1]


def test_status_headers_and_echoed_output_are_observed():
    r = run("redirect_home", [{"id": "r", "named": {}}])["results"][0]
    assert r["responseCode"] == 302
    assert "Location: /home" in r["headers"]
    assert r["output"] == "moved"
    assert r["returnValue"] is None


def test_by_reference_outputs_and_session_writes():
    r = run("addLine", [{"id": "c", "named": {"cart": {"A": 1}, "sku": "B", "qty": 2}}])["results"][0]
    assert r["returnValue"] == 2
    assert r["byRef"]["cart"] == {"A": 1, "B": 2}
    assert r["session"] == {"cart": {"A": 1, "B": 2}}
    assert r["sessionChanged"] is True


def test_missing_required_input_is_reported_not_guessed():
    r = run("addLine", [{"id": "m", "named": {"sku": "B"}}])["results"][0]
    assert r["missingInput"] == "$cart"


def test_exceptions_are_captured():
    r = run("boom", [{"id": "e", "args": [-1]}])["results"][0]
    assert r["ok"] is True
    assert r["exception"]["class"] == "InvalidArgumentException"
    assert r["exception"]["message"] == "negative"


def test_exit_inside_the_routine_still_reports():
    r = run("quit", [{"id": "q", "args": []}])["results"][0]
    assert r["exited"] is True
    assert r["output"] == "bye"


def test_methods_classes_across_files_and_constructorless_instances():
    res = run("total", [{"id": "t", "named": {"prices": [1.5, 2.25]}}])
    r = res["results"][0]
    assert r["returnValue"].startswith("3.75")
    assert any("without running its constructor" in n for n in r["notes"])
    assert res["routine"]["class"] == "Shop\\Cart"
    r = run("lines", [{"id": "l", "args": []}])["results"][0]
    assert r["returnValue"] == {"a": 1, "b": [1, 2]}
    assert run("money", [{"id": "m", "args": [1, 2.5]}])["results"][0]["returnValue"] == "3.50"


def test_a_base_class_outside_the_supplied_files_is_stubbed_and_a_wrong_class_hint_is_ignored():
    src = """<?php
require_once DOL_DOCUMENT_ROOT.'/core/class/commonobject.class.php';
class Societe extends CommonObject implements Lockable
{
    public $db;
    public function __construct($db) { $this->db = $db; }
    public function check_code($code) { return preg_match('/^[A-Z]{2}[0-9]{4}$/', $code) ? 0 : -1; }
    public function load($id) { return $this->db->query("SELECT 1"); }
}
"""
    body = {"files": [{"path": "htdocs/societe/class/societe.class.php", "content": src}],
            "entry": {"name": "check_code", "path": "htdocs/societe/class/societe.class.php", "line": 7, "className": "Societe extends CommonObject"},
            "vectors": [{"id": "ok", "args": ["CU0001"]}, {"id": "bad", "args": ["x"]}]}
    res = client.post("/run", json=body).json()
    by = {r["id"]: r for r in res["results"]}
    assert by["ok"]["returnValue"] == 0 and by["bad"]["returnValue"] == -1
    assert any("CommonObject" in n for n in by["ok"]["notes"])
    assert sorted(by["ok"]["stubs"]) == ["class CommonObject", "interface Lockable"]
    body["entry"]["name"] = "load"
    body["vectors"] = [{"id": "db", "args": [1]}]
    r = client.post("/run", json=body).json()["results"][0]
    assert "on null" in r["exception"]["message"]


def test_a_boot_time_constant_in_a_class_declaration_stands_in_as_its_own_name():
    src = """<?php
class Ticket {
    static $meta = array('table' => TICKET_TABLE, 'pk' => array('ticket_id'));
    var $ticket_pid;
    function isChild($pid=false) { return ($this->ticket_pid || $pid); }
    function table() { return static::$meta['table']; }
}
"""
    files = [{"path": "include/class.ticket.php", "content": src}]
    res = client.post("/run", json={"files": files, "entry": {"name": "isChild"},
                                    "vectors": [{"id": "a", "named": {"pid": 5}}, {"id": "b", "named": {"pid": 0}}]}).json()
    assert [r["returnValue"] for r in res["results"]] == [True, False]
    assert any("TICKET_TABLE" in n for n in res["results"][0]["notes"])
    r = client.post("/run", json={"files": files, "entry": {"name": "table"}, "vectors": [{"args": []}]}).json()["results"][0]
    assert r["returnValue"] == "TICKET_TABLE"


def test_guarded_function_declarations_are_kept():
    body = {"files": [{"path": "legacy.php", "content": GUARDED}], "entry": {"name": "legacy_round"},
            "vectors": [{"id": "g", "args": [2.345]}]}
    r = client.post("/run", json=body).json()["results"][0]
    assert r["returnValue"] == 2.3


def test_time_limit_stops_a_runaway_routine():
    r = run("forever", [{"id": "f", "args": []}], timeoutMs=1000)["results"][0]
    assert r["timedOut"] is True or r.get("fatal")
    assert r["durationMs"] < 5000   # the request's timeoutMs is honoured, not the 10 s default


def test_memory_limit_stops_a_hog():
    r = run("hog", [{"id": "h", "args": []}], memoryLimitMb=64)["results"][0]
    assert r.get("fatal") or r.get("ok") is False


def test_no_shell_no_network_no_files_outside_the_run():
    r = run("shell", [{"id": "s", "args": []}])["results"][0]
    assert "shell_exec" in r["exception"]["message"]
    assert run("remote", [{"id": "n", "args": []}])["results"][0]["returnValue"] is False
    assert run("secrets", [{"id": "x", "args": []}])["results"][0]["returnValue"] is False


def test_unknown_routine_is_a_load_error():
    res = run("nope", [{"id": "z", "args": []}])
    assert "not declared" in (res["loadError"] or "")
    assert res["results"][0]["loadError"]


def test_paths_outside_the_run_are_rejected():
    body = {"files": [{"path": "../evil.php", "content": "<?php"}], "entry": {"name": "x"}, "vectors": [{"args": []}]}
    assert client.post("/run", json=body).status_code == 400


def test_a_configured_token_is_required(monkeypatch):
    monkeypatch.setenv("PHP_SIDECAR_TOKEN", "s3cret")
    body = {"files": [{"path": "legacy.php", "content": GUARDED}], "entry": {"name": "legacy_round"}, "vectors": [{"args": [1.0]}]}
    assert client.post("/run", json=body).status_code == 401
    assert client.post("/run", json=body, headers={"X-Astra-Sidecar-Token": "nope"}).status_code == 401
    assert client.post("/run", json=body, headers={"X-Astra-Sidecar-Token": "s3cret"}).status_code == 200
    assert client.get("/health").status_code == 200


def test_inspect_describes_the_routine():
    body = {"files": [{"path": "src/qty.php", "content": QTY}], "entry": {"name": "addLine"}}
    info = client.post("/inspect", json=body).json()
    assert [p["name"] for p in info["routine"]["params"]] == ["cart", "sku", "qty"]
    assert info["routine"]["params"][0]["byRef"] is True


def test_the_driver_never_evaluates_request_data():
    driver = Path(server.DRIVER).read_text(encoding="utf-8")
    assert "eval(" not in driver.replace("eval().", "")   # the docblock says "passed to eval()" only
