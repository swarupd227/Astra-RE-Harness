"""
Tests for parser_sidecar.php_facts — routes, tables, settings and includes a PHP code base states.

The pure helpers (SQL table reading, handler forms) need no PHP. The detector tests run the real scanner on
small samples of each style (Laravel, Lumen/Slim/CodeIgniter router calls, Symfony attributes, a procedural front
controller, active-record and raw SQL) and are skipped where PHP is not installed (they run in the sidecar image).
"""
from __future__ import annotations

import textwrap

import pytest

from parser_sidecar import php_facts, php_native
from parser_sidecar.php_facts import handler_of, tables_in_sql

needs_php = pytest.mark.skipif(not php_native.available(), reason="PHP / nikic/php-parser not installed here")


# ──────────────────────────────────────────────────────────────────────
# Pure helpers
# ──────────────────────────────────────────────────────────────────────


def test_tables_in_sql_reads_and_writes():
    assert dict(tables_in_sql("SELECT i.id FROM invoices i JOIN customers c ON c.id = i.cid")) == {
        "invoices": "reads", "customers": "reads"}
    assert dict(tables_in_sql("INSERT INTO audit_log (a) SELECT a FROM staging")) == {"audit_log": "writes", "staging": "reads"}
    assert dict(tables_in_sql("UPDATE `orders` SET total = 1 WHERE id = ?")) == {"orders": "writes"}
    assert dict(tables_in_sql("DELETE FROM sessions WHERE expired = 1")) == {"sessions": "writes"}
    assert dict(tables_in_sql("SELECT * FROM dbo.Accounts")) == {"dbo.Accounts": "reads"}
    assert tables_in_sql("SELECT NOW() FROM DUAL") == []


@pytest.mark.parametrize("arg,expected", [
    ({"arr": [{"v": {"cls": "App\\Http\\Controllers\\InvoiceController"}}, {"v": {"s": "show"}}]}, ("App\\Http\\Controllers\\InvoiceController", "show")),
    ({"s": "InvoiceController@store"}, ("InvoiceController", "store")),
    ({"s": "Home::index"}, ("Home", "index")),
    ({"s": "App\\Action\\Ping:handle"}, ("App\\Action\\Ping", "handle")),
    ({"cls": "App\\Http\\Invokable"}, ("App\\Http\\Invokable", "__invoke")),
    ({"expr": "UserController::class . ':list'"}, ("UserController", "list")),
])
def test_handler_forms(arg, expected):
    h = handler_of(arg)
    assert (h.cls, h.method) == expected


def test_closure_handler_is_labelled_and_has_no_target():
    h = handler_of({"closure": True})
    assert h.closure and h.label == "closure"


# ──────────────────────────────────────────────────────────────────────
# Detectors on real scans
# ──────────────────────────────────────────────────────────────────────


def facts(*files):
    files = [(name, textwrap.dedent(src).strip() + "\n") for name, src in files]
    scans = php_native.scan(files)
    by_file, warnings = php_facts.facts_for([(n, sc) for (n, _), sc in zip(files, scans)])
    assert warnings == []
    return by_file


def entities(f, type_):
    return {e["name"]: e for e in f["entities"] if e["type"] == type_}


def links(f, relation=None):
    return [(l["from"]["name"], l["relation"], l["to"]["name"]) for l in f["links"] if relation in (None, l["relation"])]


@needs_php
def test_laravel_routes_with_controllers_strings_closures_and_resources():
    out = facts(("routes/api.php", r'''
        <?php
        use App\Http\Controllers\InvoiceController;
        use Illuminate\Support\Facades\Route;
        Route::get('/invoices/{id}', [InvoiceController::class, 'show'])->name('invoices.show');
        Route::post('invoices', 'InvoiceController@store');
        Route::match(['get', 'post'], '/search', [InvoiceController::class, 'search']);
        Route::middleware('auth')->delete('/invoices/{id}', [InvoiceController::class, 'destroy']);
        Route::get('/health', function () { return 'ok'; });
        Route::apiResource('customers', CustomerController::class);
    '''))["routes/api.php"]
    routes = entities(out, "route")
    assert routes["GET /invoices/{id}"]["attrs"]["handler"] == "InvoiceController::show"
    assert routes["GET /invoices/{id}"]["locator"] == "L4-4"
    assert "POST /invoices" in routes
    assert "GET|POST /search" in routes
    assert "DELETE /invoices/{id}" in routes
    assert routes["GET /health"]["attrs"]["handler"] == "closure"
    for r in ("GET /customers", "POST /customers", "GET /customers/{id}", "PUT|PATCH /customers/{id}", "DELETE /customers/{id}"):
        assert r in routes, r
    assert "GET /customers/create" not in routes      # apiResource has no form routes
    assert ("GET /invoices/{id}", "triggers", "InvoiceController") in links(out)
    assert ("GET /health", "triggers", "InvoiceController") not in links(out)


@needs_php
def test_router_variables_of_slim_lumen_and_codeigniter4():
    out = facts(("app/routes.php", r'''
        <?php
        $app->get('/users/{id}', UserController::class . ':show');
        $router->post('/users', 'UserController@store');
        $routes->get('dashboard', 'Home::index');
        $cache->get('users');        // not a router: no route
    '''))["app/routes.php"]
    routes = entities(out, "route")
    assert routes["GET /users/{id}"]["attrs"]["handler"] == "UserController::show"
    assert routes["POST /users"]["attrs"]["handler"] == "UserController::store"
    assert routes["GET /dashboard"]["attrs"]["handler"] == "Home::index"
    assert len(routes) == 3


@needs_php
def test_symfony_route_attributes_with_a_class_prefix():
    out = facts(("src/Controller/InvoiceController.php", r'''
        <?php
        namespace App\Controller;
        use Symfony\Component\Routing\Attribute\Route;
        #[Route('/api/invoices')]
        class InvoiceController
        {
            #[Route('/{id}', name: 'invoice_show', methods: ['GET'])]
            public function show(int $id) { return $id; }
            #[Route('', methods: ['POST'])]
            public function create() { return 1; }
        }
    '''))["src/Controller/InvoiceController.php"]
    routes = entities(out, "route")
    assert routes["GET /api/invoices/{id}"]["attrs"]["routeName"] == "invoice_show"
    assert routes["GET /api/invoices/{id}"]["attrs"]["handler"] == "InvoiceController::show"
    assert "POST /api/invoices" in routes
    assert ("GET /api/invoices/{id}", "triggers", "InvoiceController") in links(out)


@needs_php
def test_a_front_controller_serves_one_route_per_case():
    out = facts(("legacy/api.php", r'''
        <?php
        require_once 'db.php';
        function get_customer($id) {
            $r = mysqli_query($GLOBALS['db'], "SELECT * FROM customers WHERE id = " . intval($id));
            return mysqli_fetch_assoc($r);
        }
        switch ($_REQUEST['action']) {
            case 'getCustomer': echo json_encode(get_customer($_GET['id'])); break;
            case 'saveCustomer':
                mysqli_query($GLOBALS['db'], "UPDATE customers SET name = '" . $_POST['name'] . "'");
                break;
        }
        $op = $_GET['op'];
        switch ($op) { case 'ping': echo 'pong'; break; }
    '''))["legacy/api.php"]
    routes = entities(out, "route")
    assert routes["ANY /legacy/api.php?action=getCustomer"]["attrs"]["handler"] == "get_customer"
    assert "ANY /legacy/api.php?action=saveCustomer" in routes
    assert "ANY /legacy/api.php?op=ping" in routes
    trig = {l["from"]["name"]: l for l in out["links"] if l["relation"] == "triggers"}
    assert trig["ANY /legacy/api.php?action=getCustomer"]["to"] == {"type": "script", "name": "legacy/api.php"}
    assert trig["ANY /legacy/api.php?action=getCustomer"]["confidence"] == "high"
    assert trig["ANY /legacy/api.php?op=ping"]["confidence"] == "medium"     # a variable's name is only a hint
    # The SQL in the function and in the script itself, with the actor that runs it.
    assert ("legacy/api.php", "reads", "customers") in links(out)
    assert ("legacy/api.php", "writes", "customers") in links(out)
    assert ("legacy/api.php", "references", "db.php") in links(out)


@needs_php
def test_tables_from_query_builders_active_record_and_schema_migrations():
    out = facts(("app/Models/Report.php", r'''
        <?php
        namespace App\Models;
        use Illuminate\Support\Facades\DB;
        use Illuminate\Support\Facades\Schema;
        class Report
        {
            public function totals() { return DB::table('invoices')->sum('total'); }
            public function legacy() {
                $q = $this->db->where('open', 1)->get('orders');
                $this->db->insert('audit_log', ['x' => 1]);
                return $q;
            }
            public function up() { Schema::create('reports', function ($t) { $t->id(); }); }
        }
    '''))["app/Models/Report.php"]
    l = links(out)
    assert ("Report", "references", "invoices") in l
    assert ("Report", "reads", "orders") in l
    assert ("Report", "writes", "audit_log") in l
    assert ("Report", "contains", "reports") in l
    tables = entities(out, "table")
    assert tables["reports"]["attrs"]["definedBy"] == "schema-builder"
    cls = entities(out, "class")["Report"]
    assert cls["attrs"]["fqn"] == "App\\Models\\Report"


@needs_php
def test_settings_read_from_env_getenv_config_and_env_superglobal():
    out = facts(("config/app.php", r'''
        <?php
        class Settings {
            public function load() {
                $a = env('APP_KEY');
                $b = getenv('DB_HOST');
                $c = config('app.timezone');
                $d = $_ENV['MAIL_FROM'];
                return [$a, $b, $c, $d];
            }
        }
    '''))["config/app.php"]
    keys = entities(out, "config-key")
    assert set(keys) == {"APP_KEY", "DB_HOST", "app.timezone", "MAIL_FROM"}
    assert ("Settings", "reads", "DB_HOST") in links(out)


@needs_php
def test_every_fact_points_at_lines():
    out = facts(("routes/web.php", "<?php\nRoute::get('/a', [A::class, 'x']);\n"))["routes/web.php"]
    assert out["links"] and all(l.get("locator", "").startswith("L") for l in out["links"])
    assert all(e.get("locator") is None or e["locator"].startswith("L") for e in out["entities"])


def test_a_file_without_a_scan_has_no_facts():
    by_file, warnings = php_facts.facts_for([("a.php", {})])
    assert by_file == {"a.php": {"entities": [], "links": []}} and warnings == []
