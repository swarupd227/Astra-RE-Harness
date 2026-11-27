namespace Astra.Api.Validation.WorkbookEquivalence;

/// <summary>The two Python programs the workbook equivalence gate runs, kept here so the gate ships as one unit.</summary>
internal static class WorkbookEquivalenceScripts
{
    /// <summary>Pinned formula engine for the workbook side (static evaluation: no Excel, no macros).</summary>
    public const string FormulaRequirements = "formulas[excel]==1.3.4\nopenpyxl==3.1.5\n";

    /// <summary>
    /// Workbook side. argv: job.json out.json. job = {path, book, outputs:[{sheet,cell}], vectors:[{id, cells:[{sheet,cell,value}]}]}.
    /// Loads the workbook once, sets each case's input cells, recalculates and reads the output cells.
    /// </summary>
    public const string Formulas = """
        import json, sys, traceback
        import formulas

        job = json.load(open(sys.argv[1], encoding="utf-8"))
        book = job["book"]

        def ref(sheet, cell):
            return "'[%s]%s'!%s" % (book, sheet.upper(), cell.upper())

        def plain(x):
            try:
                v = x.value[0, 0] if hasattr(x, "value") else x
            except Exception:
                v = x
            if hasattr(v, "item"):
                try:
                    v = v.item()
                except Exception:
                    pass
            if isinstance(v, (bool, int, float, str)) or v is None:
                return v
            return str(v)

        out = {"results": []}
        try:
            model = formulas.ExcelModel().loads(job["path"]).finish()
        except Exception as e:
            out["fatal"] = "%s: %s" % (type(e).__name__, e)
            json.dump(out, open(sys.argv[2], "w", encoding="utf-8"))
            sys.exit(0)
        for v in job["vectors"]:
            inputs = {ref(c["sheet"], c["cell"]): c["value"] for c in v["cells"]}
            try:
                sol = model.calculate(inputs=inputs)
                values = {}
                for o in job["outputs"]:
                    k = ref(o["sheet"], o["cell"])
                    values["%s!%s" % (o["sheet"], o["cell"])] = plain(sol[k]) if k in sol else None
                out["results"].append({"id": v["id"], "values": values})
            except Exception as e:
                out["results"].append({"id": v["id"], "error": "%s: %s" % (type(e).__name__, e), "trace": traceback.format_exc()[-1200:]})
        json.dump(out, open(sys.argv[2], "w", encoding="utf-8"))
        """;

    /// <summary>
    /// Package side, run in the package's own virtual environment from backend/. argv: job.json out.json.
    /// job = {live, canned, vectors:[{id, inputs}]}. Outbound HTTP through urllib, requests and httpx is recorded; offline
    /// it is answered with the canned body and any other socket connection is refused, live it goes through.
    /// Then app.contract.run(inputs) is called for each case.
    /// </summary>
    public const string Package = """
        import io, json, sys, traceback, importlib
        sys.path.insert(0, ".")
        job = json.load(open(sys.argv[1], encoding="utf-8"))
        live = bool(job.get("live"))
        canned = json.dumps(job.get("canned") or {}).encode("utf-8")
        captured = []

        def record(method, url, body):
            parsed = body
            if isinstance(body, (bytes, bytearray)):
                body = body.decode("utf-8", "replace")
            if isinstance(body, str):
                try:
                    parsed = json.loads(body) if body.strip() else None
                except Exception:
                    parsed = body
            captured.append({"method": (method or "").upper(), "url": str(url), "body": parsed})

        import urllib.request as _ur
        _orig_urlopen = _ur.urlopen

        class _Resp(io.BytesIO):
            status = 200
            code = 200
            reason = "OK"
            def __init__(self, data):
                super().__init__(data)
                self.headers = {"Content-Type": "application/json"}
            def getcode(self):
                return 200
            def info(self):
                return self.headers
            def __enter__(self):
                return self
            def __exit__(self, *a):
                return False

        def _urlopen(req, data=None, *a, **k):
            if isinstance(req, str):
                record("POST" if data is not None else "GET", req, data)
            else:
                record(req.get_method(), req.full_url, req.data if req.data is not None else data)
            if live:
                return _orig_urlopen(req, data, *a, **k)
            return _Resp(canned)

        _ur.urlopen = _urlopen

        try:
            import requests
            _orig_request = requests.sessions.Session.request
            def _request(self, method, url, **kw):
                body = kw.get("json")
                record(method, url, json.dumps(body) if body is not None else kw.get("data"))
                if live:
                    return _orig_request(self, method, url, **kw)
                r = requests.models.Response()
                r.status_code = 200
                r._content = canned
                r.headers["Content-Type"] = "application/json"
                r.url = url
                return r
            requests.sessions.Session.request = _request
        except ImportError:
            pass

        try:
            import httpx
            _orig_send = httpx.Client.send
            _orig_asend = httpx.AsyncClient.send
            def _send(self, request, **kw):
                record(request.method, request.url, request.content)
                return _orig_send(self, request, **kw) if live else httpx.Response(200, content=canned, headers={"Content-Type": "application/json"}, request=request)
            async def _asend(self, request, **kw):
                record(request.method, request.url, request.content)
                if live:
                    return await _orig_asend(self, request, **kw)
                return httpx.Response(200, content=canned, headers={"Content-Type": "application/json"}, request=request)
            httpx.Client.send = _send
            httpx.AsyncClient.send = _asend
        except ImportError:
            pass

        if not live:
            import socket
            def _refuse(self, *a, **k):
                raise ConnectionRefusedError("the equivalence gate runs offline: outbound connections are answered by the gate, not sent")
            socket.socket.connect = _refuse
            socket.socket.connect_ex = _refuse

        def plain(x):
            if hasattr(x, "model_dump"):
                x = x.model_dump()
            return json.loads(json.dumps(x, default=str))

        out = {"results": []}
        try:
            contract = importlib.import_module("app.contract")
            run = getattr(contract, "run")
        except Exception as e:
            out["fatal"] = "app.contract.run could not be loaded: %s: %s" % (type(e).__name__, e)
            out["trace"] = traceback.format_exc()[-1500:]
            json.dump(out, open(sys.argv[2], "w", encoding="utf-8"))
            sys.exit(0)
        for v in job["vectors"]:
            captured.clear()
            res = {"id": v["id"]}
            try:
                res["outputs"] = plain(run(json.loads(json.dumps(v["inputs"]))))
            except Exception as e:
                res["error"] = "%s: %s" % (type(e).__name__, e)
                res["trace"] = traceback.format_exc()[-1500:]
            res["requests"] = list(captured)
            out["results"].append(res)
        json.dump(out, open(sys.argv[2], "w", encoding="utf-8"))
        """;
}
