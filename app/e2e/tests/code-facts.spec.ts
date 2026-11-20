/**
 * Code facts, linking rules and the inventory (WS7 K5).
 *
 *   - C# is read for what each class reads from configuration, which web endpoints and queues it uses and
 *     which procedures and tables its SQL names; each fact cites the routine (or the lines) it was found in.
 *   - Rules join what separate files say: a setting that holds an endpoint's address in every environment,
 *     a ticket or a transcript turn that names a thing is listed on it as narrative evidence.
 *   - The inventory counts what was found next to what the project expects, and says what does not meet up:
 *     a setting that nothing reads, a procedure that is called and defined nowhere.
 *   - A re-sync reads the code again; everyone can read all of it.
 */
import { expect, test, type Page } from '@playwright/test';
import { API_BASE, JSON_ENG, attach, getEvidence } from './helpers/projects';

const PREFIX = 'codefacts-e2e';

const PROGRAM = `var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ReceiptOptions>(builder.Configuration.GetSection("ReceiptPrinter"));
builder.Services.AddHttpClient<OfacScreeningClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Ofac:BaseUrl"]!);
});
var app = builder.Build();
app.Run();
`;

const OFAC = `using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace Teller.Services
{
    public class OfacScreeningClient
    {
        private readonly HttpClient _http;

        public OfacScreeningClient(HttpClient http)
        {
            _http = http;
            _http.Timeout = TimeSpan.FromSeconds(5);
        }

        public async Task<bool> ScreenAsync(string name)
        {
            var response = await _http.PostAsJsonAsync("/screen", new { name });
            return response.IsSuccessStatusCode;
        }
    }
}
`;

const LOAN = `using System.Net.Http;
using Microsoft.Extensions.Configuration;

namespace Teller.Services
{
    public class LoanLookupClient
    {
        private readonly HttpClient _http;

        public LoanLookupClient(HttpClient http, IConfiguration config)
        {
            _http = http;
            _http.BaseAddress = new Uri(config["LoanLookup:BaseUrl"] ?? "https://loanserv.brfc.example");
        }

        public async Task<string> GetLoanAsync(string loanRef)
        {
            return await _http.GetStringAsync($"/api/loans/{loanRef}");
        }
    }
}
`;

const POSTING = `using RabbitMQ.Client;
using System.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Teller.Services
{
    public class TellerPostingService
    {
        private readonly IConfiguration _config;

        public TellerPostingService(IConfiguration config)
        {
            _config = config;
        }

        public void PublishPosting(IModel channel, byte[] body)
        {
            channel.BasicPublish(exchange: _config["RabbitMq:Exchange"], routingKey: "gl.posting.in", basicProperties: null, body: body);
        }

        public void Stage(SqlConnection conn)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var cmd = new SqlCommand("EXEC dbo.usp_PostStage", conn);
                cmd.ExecuteNonQuery();
            }
        }

        public void Purge(SqlConnection conn)
        {
            using var cmd = new SqlCommand("EXEC dbo.usp_PurgeStage @TxnRef = 'x'", conn);
            cmd.ExecuteNonQuery();
        }
    }
}
`;

const PRINTER = `using Microsoft.Extensions.Options;

namespace Teller.Extensions
{
    public class ReceiptOptions
    {
        public string Url { get; set; }
    }

    public class ReceiptPrinterExtension
    {
        private readonly ReceiptOptions _options;

        public ReceiptPrinterExtension(IOptions<ReceiptOptions> options)
        {
            _options = options.Value;
        }

        public string Target()
        {
            return _options.Url;
        }
    }
}
`;

const SESSION = `using Microsoft.Extensions.Configuration;

namespace Teller.Extensions
{
    public class SessionExtension
    {
        private readonly IConfiguration _config;

        public SessionExtension(IConfiguration config)
        {
            _config = config;
        }

        public bool IsTeller(string group)
        {
            return group == _config["Auth:TellerAdGroup"];
        }
    }
}
`;

const CODE = [
  { path: 'src/Program.cs', content: PROGRAM },
  { path: 'src/services/OfacScreeningClient.cs', content: OFAC },
  { path: 'src/services/LoanLookupClient.cs', content: LOAN },
  { path: 'src/services/TellerPostingService.cs', content: POSTING },
  { path: 'src/extensions/ReceiptPrinterExtension.cs', content: PRINTER },
  { path: 'src/extensions/SessionExtension.cs', content: SESSION },
];

const DDL = `CREATE TABLE dbo.TLR_STAGE (
    STAGE_ID INT IDENTITY(1,1) NOT NULL,
    TXN_REF VARCHAR(40) NOT NULL,
    CONSTRAINT PK_TLR_STAGE PRIMARY KEY (STAGE_ID),
    CONSTRAINT UQ_TLR_STAGE_TXN_REF UNIQUE (TXN_REF)
);
GO
`;

const PROC = `CREATE PROCEDURE dbo.usp_PostStage AS
BEGIN
    INSERT INTO TLR_STAGE (TXN_REF) SELECT 'x';
END
GO
`;

const config = (ofac: string) =>
  JSON.stringify(
    {
      Ofac: { BaseUrl: ofac, TimeoutSeconds: 15 },
      LoanLookup: { BaseUrl: 'https://loanserv.brfc.example/' },
      RabbitMq: { Exchange: 'tlr.gl' },
      ReceiptPrinter: { Url: 'http://localhost:9100' },
    },
    null,
    2,
  ) + '\n';

const TICKETS = [
  'number,opened_at,priority,state,short_description,description,cmdb_ci',
  'INC-T-0412,2026-03-02,2,Closed,EOD post fails,"Job failed with error 2627: violation of UQ_TLR_STAGE_TXN_REF. Ran usp_PurgeStage and restarted.",usp_PostStage',
  'INC-T-0500,2026-04-01,4,Closed,Screening slow,"Teller blocked while the check ran",OfacScreeningClient',
  'INC-T-0501,2026-04-02,4,Closed,Printer,Receipt printer jammed,Teller printer',
].join('\n') + '\n';

const VTT = `WEBVTT

00:00:05.000 --> 00:00:12.000
<v Dale Henry>When the post fails with 2627, run usp_PurgeStage and rerun the step.

00:00:20.000 --> 00:00:30.000
<v Dale Henry>The screening call in OfacScreeningClient ignores Ofac:TimeoutSeconds, whatever PROD says.
`;

type Node = { ref: string; type: string; name: string; declared: boolean; attrs: Record<string, any> | null; evidenceIds: string[]; paths: string[] };
type Edge = { relation: string; from: string; to: string; confidence: string; origin: string; attrs: Record<string, any> | null; evidenceIds: string[] };
type GraphBody = { nodes: Node[]; edges: Edge[] };
type Measure = { key: string; label: string; found: number | null; expected: number | null };
type Finding = { code: string; title: string; items: { ref: string; name: string; evidenceIds: string[] }[] };
type Inventory = {
  files: { kind: string; files: number; chunks: number }[];
  code: { files: number; routines: number; languages: { language: string; files: number; routines: number }[] };
  things: { type: string; declared: number; referencedOnly: number }[];
  measures: Measure[];
  candidates: { ref: string }[];
  findings: Finding[];
};

async function graph(page: Page, id: string, persona = 'engineer'): Promise<GraphBody> {
  const res = await page.request.get(`${API_BASE}/api/v1/corpora/${id}/entity-graph`, { headers: { 'X-Dev-Persona': persona } });
  expect(res.status()).toBe(200);
  return res.json();
}

async function inventory(page: Page, id: string, persona = 'engineer'): Promise<Inventory> {
  const res = await page.request.get(`${API_BASE}/api/v1/corpora/${id}/inventory`, { headers: { 'X-Dev-Persona': persona } });
  expect(res.status()).toBe(200);
  return res.json();
}

const node = (g: GraphBody, ref: string) => g.nodes.find((n) => n.ref === ref);
const edge = (g: GraphBody, relation: string, from: string, to: string) =>
  g.edges.find((e) => e.relation === relation && e.from === from && e.to === to);

async function ingestCode(page: Page, files = CODE): Promise<string> {
  const res = await page.request.post(`${API_BASE}/api/v1/ingest/text`, {
    headers: JSON_ENG,
    data: { name: `${PREFIX}-${Date.now()}-${Math.floor(Math.random() * 1e4)}`, files },
  });
  expect(res.ok()).toBe(true);
  const { corpusId } = await res.json();
  return corpusId as string;
}

async function setSettings(page: Page, id: string, kinds: string[], expectedCounts: Record<string, number>) {
  const put = await page.request.put(`${API_BASE}/api/v1/corpora/${id}/settings`, {
    headers: JSON_ENG,
    data: { artefactKinds: kinds, pack: null, banner: null, metadata: { tier: null, knowledgeHolder: null, wave: null }, expectedCounts, demo: false },
  });
  expect(put.status()).toBe(200);
}

async function fullProject(page: Page) {
  const id = await ingestCode(page);
  await setSettings(page, id, ['sql', 'config', 'ticket', 'transcript'], { endpoints: 2, queues: 1, owners: 3 });
  const r = await attach(id, [
    { path: 'db/ddl.sql', data: DDL },
    { path: 'db/procs/usp_PostStage.sql', data: PROC },
    { path: 'config/appsettings.DEV.json', data: config('https://ofac-dev.brfc.example/') },
    { path: 'config/appsettings.PROD.json', data: config('https://ofac.brfc.example/') },
    { path: 'tickets/export.csv', data: TICKETS },
    { path: 'kt/session1.vtt', data: VTT },
  ]);
  expect(r.status).toBe(200);
  return id;
}

test.describe('code facts, linking rules and the inventory', () => {
  test('C# is read for what it reads, calls and publishes, and each fact cites its code', async ({ page }) => {
    const id = await ingestCode(page);
    const g = await graph(page, id);

    // Classes appear when they touch something outside themselves.
    for (const name of [
      'Teller.Services.OfacScreeningClient',
      'Teller.Services.LoanLookupClient',
      'Teller.Services.TellerPostingService',
      'Teller.Extensions.ReceiptPrinterExtension',
      'Teller.Extensions.SessionExtension',
      'Program',
    ]) expect(node(g, `class:${name}`), name).toBeTruthy();

    // A client registered in Program.cs and used in its own class: the endpoint has the registered base and the class's literal timeout.
    const ofac = 'endpoint:POST {Ofac:BaseUrl}/screen';
    expect(node(g, ofac)?.declared).toBe(true);
    const call = edge(g, 'calls', 'class:Teller.Services.OfacScreeningClient', ofac)!;
    expect(call.confidence).toBe('high');
    expect(call.origin).toBe('rule');
    const site = call.attrs!.sites[0];
    expect(site.routine).toBe('OfacScreeningClient.ScreenAsync');
    expect(site.timeoutSeconds).toBe(5);
    expect(site.timeoutSource).toBe('code');

    // A base address from a setting with a literal fallback: both are kept.
    const loan = node(g, 'endpoint:GET {LoanLookup:BaseUrl}/api/loans/{loanRef}')!;
    expect(loan.attrs!.baseKey).toBe('LoanLookup:BaseUrl');
    expect(loan.attrs!.fallbackUrl).toBe('https://loanserv.brfc.example');

    // A queue named by a setting; SQL text names procedures; an options class is read as its settings.
    expect(node(g, 'queue:{RabbitMq:Exchange}')?.attrs?.broker).toBe('rabbitmq');
    const posting = 'class:Teller.Services.TellerPostingService';
    expect(edge(g, 'writes', posting, 'queue:{RabbitMq:Exchange}')?.attrs?.sites[0].routingKey).toBe('gl.posting.in');
    expect(edge(g, 'calls', posting, 'procedure:usp_PostStage')?.confidence).toBe('high');
    expect(edge(g, 'calls', posting, 'procedure:usp_PostStage')?.attrs?.sites[0].retry).toBe('loop');
    expect(edge(g, 'reads', 'class:Teller.Extensions.ReceiptPrinterExtension', 'config-key:ReceiptPrinter:Url')?.confidence).toBe('high');
    expect(edge(g, 'reads', 'class:Teller.Extensions.SessionExtension', 'config-key:Auth:TellerAdGroup')).toBeTruthy();
    expect(edge(g, 'reads', 'class:Program', 'config-key:Ofac:BaseUrl')).toBeTruthy();

    // Nothing in the code reads the timeout setting.
    expect(g.edges.some((e) => e.relation === 'reads' && e.to === 'config-key:Ofac:TimeoutSeconds')).toBe(false);

    // Every one of these cites code that can be opened: a routine, or the lines of a top-level statement.
    expect(call.evidenceIds.length).toBeGreaterThan(0);
    const cited = (await getEvidence(page, id, call.evidenceIds[0])).body;
    expect(cited.kind).toBe('code');
    expect(cited.path).toBe('src/services/OfacScreeningClient.cs');
    expect(cited.text).toContain('PostAsJsonAsync');

    const top = edge(g, 'reads', 'class:Program', 'config-key:Ofac:BaseUrl')!;
    expect(top.evidenceIds.length).toBeGreaterThan(0);
    const topEvidence = (await getEvidence(page, id, top.evidenceIds[0])).body;
    expect(topEvidence.path).toBe('src/Program.cs');
    expect(topEvidence.text).toContain('Ofac:BaseUrl');
    for (const n of g.nodes.filter((x) => x.type === 'class')) expect(n.evidenceIds.length, n.ref).toBeGreaterThan(0);
  });

  test('rules join the files: a setting gives an endpoint its address per environment; text that names a thing is listed on it', async ({ page }) => {
    const id = await fullProject(page);
    const g = await graph(page, id);

    // Config key ↔ URL ↔ endpoint.
    const ofac = node(g, 'endpoint:POST {Ofac:BaseUrl}/screen')!;
    const resolved = (ofac.attrs!.resolved as { file: string; value: string }[]).map((r) => `${r.file}=${r.value}`).sort();
    expect(resolved).toEqual([
      'config/appsettings.DEV.json=https://ofac-dev.brfc.example/',
      'config/appsettings.PROD.json=https://ofac.brfc.example/',
    ]);
    const configures = edge(g, 'configures', 'config-key:Ofac:BaseUrl', 'endpoint:POST {Ofac:BaseUrl}/screen')!;
    expect(configures.confidence).toBe('high');
    expect(configures.attrs!.role).toBe('base-address');

    const queue = node(g, 'queue:{RabbitMq:Exchange}')!;
    expect((queue.attrs!.resolved as any[]).some((r) => r.value === 'tlr.gl')).toBe(true);
    expect(edge(g, 'configures', 'config-key:RabbitMq:Exchange', 'queue:{RabbitMq:Exchange}')?.attrs?.rule).toBe('config-name');

    // The call and the definition meet on one procedure; the one only called stays "not defined".
    expect(node(g, 'procedure:usp_PostStage')?.declared).toBe(true);
    expect(node(g, 'procedure:usp_PurgeStage')?.declared).toBe(false);

    // Narrative evidence is listed on the thing, apart from what declares it.
    const purge = node(g, 'procedure:usp_PurgeStage')!;
    const kinds = (purge.attrs!.mentions as { evidenceId: string; kind: string }[]).map((m) => m.kind).sort();
    expect(kinds).toEqual(['ticket', 'transcript']);
    expect(purge.attrs!.mentionCounts).toEqual({ ticket: 1, transcript: 1 });
    const ticketMention = (purge.attrs!.mentions as { evidenceId: string; kind: string }[]).find((m) => m.kind === 'ticket')!;
    const ticket = (await getEvidence(page, id, ticketMention.evidenceId)).body;
    expect(ticket.kind).toBe('ticket');
    expect(ticket.text).toContain('INC-T-0412');

    // A constraint the table owns names the table; a ticket's configuration item names the procedure or class it is for.
    const stage = node(g, 'table:TLR_STAGE')!;
    expect((stage.attrs!.mentions as { via?: string }[]).some((m) => m.via === 'constraint UQ_TLR_STAGE_TXN_REF')).toBe(true);
    expect((node(g, 'procedure:usp_PostStage')!.attrs!.mentions as { via?: string }[]).some((m) => m.via === 'cmdb_ci')).toBe(true);
    const client = node(g, 'class:Teller.Services.OfacScreeningClient')!;
    expect((client.attrs!.mentionCounts as Record<string, number>).ticket).toBe(1);
    expect((client.attrs!.mentionCounts as Record<string, number>).transcript).toBe(1);
    const timeout = node(g, 'config-key:Ofac:TimeoutSeconds')!;
    expect((timeout.attrs!.mentions as any[]).map((m) => m.kind)).toEqual(['transcript']);

    // What a transcript turn says is narrative: the entity's own evidence stays structured.
    for (const evidenceId of purge.evidenceIds) {
      const e = (await getEvidence(page, id, evidenceId)).body;
      expect(e.trustTier).toBe('structured');
    }
  });

  test('the inventory counts what was found beside what the project expects, and names what does not meet up', async ({ page }) => {
    const id = await fullProject(page);
    const inv = await inventory(page, id, 'observer');

    const measure = (key: string) => inv.measures.find((m) => m.key === key)!;
    expect(measure('endpoints').found).toBe(2);
    expect(measure('endpoints').expected).toBe(2);
    expect(measure('queues').found).toBe(1);
    expect(measure('classes').found).toBe(6);
    expect(measure('tables').found).toBe(1);
    expect(measure('procedures').found).toBe(1);
    expect(measure('routines').found).toBe(inv.code.routines);
    expect(inv.code.routines).toBeGreaterThan(5);
    expect(inv.code.files).toBe(6);
    expect(inv.code.languages.map((l) => l.language)).toContain('csharp');
    // Expected, but nothing here can count it: said, not dropped.
    expect(measure('owners').found).toBeNull();
    expect(measure('owners').expected).toBe(3);
    // Two endpoints and a queue; no job, package or file exchanges anything here.
    expect(measure('interfaces').found).toBe(3);
    expect(inv.candidates.map((c) => c.ref).sort()).toEqual([
      'endpoint:GET {LoanLookup:BaseUrl}/api/loans/{loanRef}',
      'endpoint:POST {Ofac:BaseUrl}/screen',
      'queue:{RabbitMq:Exchange}',
    ]);

    const finding = (code: string) => inv.findings.find((f) => f.code === code);
    expect(finding('setting-never-read')?.items.map((i) => i.ref)).toEqual(['config-key:Ofac:TimeoutSeconds']);
    expect(finding('setting-not-configured')?.items.map((i) => i.ref)).toEqual(['config-key:Auth:TellerAdGroup']);
    expect(finding('referenced-not-defined')?.items.map((i) => i.ref)).toEqual(['procedure:usp_PurgeStage']);
    expect(inv.files.find((f) => f.kind === 'code')?.files).toBe(6);
    expect(inv.files.find((f) => f.kind === 'ticket')?.chunks).toBe(3);
  });

  test('a re-sync reads the code again; what was attached is carried forward and still joined', async ({ page }) => {
    const id = await fullProject(page);
    const before = await graph(page, id);
    expect(node(before, 'endpoint:POST {Ofac:BaseUrl}/screen')).toBeTruthy();

    // The screening call is removed from the code.
    const changed = CODE.map((f) =>
      f.path === 'src/services/OfacScreeningClient.cs'
        ? { ...f, content: f.content.replace('var response = await _http.PostAsJsonAsync("/screen", new { name });\n            return response.IsSuccessStatusCode;', 'return true;') }
        : f,
    );
    const resync = await page.request.post(`${API_BASE}/api/v1/corpora/${id}/reingest/text`, { headers: JSON_ENG, data: { files: changed } });
    expect(resync.status()).toBe(200);

    const after = await graph(page, id);
    expect(node(after, 'endpoint:POST {Ofac:BaseUrl}/screen')).toBeUndefined();
    // Everything else, including what only the artefacts say, is where it was.
    expect(node(after, 'endpoint:GET {LoanLookup:BaseUrl}/api/loans/{loanRef}')).toBeTruthy();
    expect(node(after, 'procedure:usp_PostStage')?.declared).toBe(true);
    expect((node(after, 'procedure:usp_PurgeStage')!.attrs!.mentionCounts as Record<string, number>).ticket).toBe(1);
    expect(edge(after, 'reads', 'class:Program', 'config-key:Ofac:BaseUrl')).toBeTruthy();
  });

  test('everyone can read the inventory; an unknown project is not found', async ({ page }) => {
    const id = await ingestCode(page);
    for (const persona of ['observer', 'sme', 'engineer', 'admin']) {
      const inv = await inventory(page, id, persona);
      expect(inv.measures.length).toBeGreaterThan(5);
    }
    const missing = await page.request.get(`${API_BASE}/api/v1/corpora/00000000-0000-0000-0000-000000000001/inventory`, { headers: { 'X-Dev-Persona': 'observer' } });
    expect(missing.status()).toBe(404);
  });
});
