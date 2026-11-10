/**
 * The entity graph (WS7 K4): what a project's database scripts, scheduler exports, packages and settings
 * say exists, how those things relate, and the evidence that shows each.
 *
 *   - SQL is cut into the objects it defines (a table is one chunk, with its keys) and states what each
 *     procedure reads, writes and calls; a job plan has a chunk per task; jobs say what their commands
 *     run; a package says what its data flow reads and writes; a setting is a thing too.
 *   - A job that runs a procedure and the script that defines it meet on one entity; something only
 *     mentioned is kept, marked as not defined anywhere.
 *   - Replacing or removing a file rebuilds the graph; a re-sync carries it forward under the same
 *     references and evidence ids.
 *   - Everyone can read it.
 */
import { expect, test, type Page } from '@playwright/test';
import { API_BASE, FORTRAN, JSON_ENG, attach, byPath, getEvidence, listEvidence, newProject } from './helpers/projects';

const PREFIX = 'graph-e2e';

const DDL = `-- Journal of every payment and cash advance taken at a branch.
CREATE TABLE dbo.TLR_TXN (
    TXN_ID INT IDENTITY(1,1) NOT NULL,
    TXN_REF VARCHAR(40) NOT NULL,
    AMOUNT DECIMAL(12,2) NOT NULL,
    POSTED BIT NOT NULL DEFAULT 0,
    CONSTRAINT PK_TLR_TXN PRIMARY KEY (TXN_ID)
);
GO

-- Staged transactions waiting to be posted.
CREATE TABLE dbo.TLR_STAGE (
    STAGE_ID INT IDENTITY(1,1) NOT NULL,
    TXN_REF VARCHAR(40) NOT NULL,
    AMOUNT DECIMAL(12,2) NOT NULL,
    CONSTRAINT PK_TLR_STAGE PRIMARY KEY (STAGE_ID),
    CONSTRAINT UQ_TLR_STAGE_TXN_REF UNIQUE (TXN_REF)
);
GO

CREATE TABLE dbo.TLR_POSTED (POSTED_ID INT IDENTITY(1,1) NOT NULL, TXN_REF VARCHAR(40) NOT NULL, AMOUNT DECIMAL(12,2) NOT NULL);
GO

-- Shared with the Branch Ops application.
CREATE VIEW dbo.vw_DrawerLimits AS SELECT d.DRAWER_ID FROM dbo.TLR_DRAWER d;
GO
`;

const PROC = `-- Posts staged transactions. Stages them one row at a time.
CREATE PROCEDURE dbo.usp_PostStage AS
BEGIN
    INSERT INTO TLR_STAGE (TXN_REF, AMOUNT) SELECT TXN_REF, AMOUNT FROM TLR_TXN WHERE POSTED = 0;
    INSERT INTO TLR_POSTED (TXN_REF, AMOUNT) SELECT TXN_REF, AMOUNT FROM TLR_STAGE;
END
GO
`;

const PLAN = [
  '<?xml version="1.0" encoding="UTF-8"?>',
  '<JOBP name="TLR_EOD">',
  '  <Title>Teller end of day</Title>',
  '  <Host>UC4AGENT-TLR01</Host>',
  '  <Schedule>Mon-Fri 20:30</Schedule>',
  '  <Notify onError="teller-ops@brfc.example" page="oncall"/>',
  '  <Tasks>',
  '    <Task Num="1" Job="TLR_EOD_LOCK"/>',
  '    <Task Num="2" Job="TLR_EOD_COUNT" After="1"/>',
  '    <Task Num="3" Job="TLR_EOD_POST" After="2" Restart="true"/>',
  '    <Task Num="4" Job="TLR_DW_EXTRACT" After="3"/>',
  '    <Task Num="5" Job="TLR_EOD_REPORT" After="3"/>',
  '  </Tasks>',
  '</JOBP>',
].join('\n') + '\n';

const job = (name: string, script: string, extra = '') =>
  `<JOBS name="${name}" type="windows">\n  <Title>${name} title</Title>\n  <Host>UC4AGENT-TLR01</Host>\n${extra}  <Script><![CDATA[\n${script}\n]]></Script>\n</JOBS>\n`;

const POST = job('TLR_EOD_POST', 'sqlcmd -S %TLR_SQL% -d TELLER -b -Q "EXEC dbo.usp_PostStage"');
const RECON = job(
  'TLR_AM_RECON',
  String.raw`dtexec /F D:\SSIS\DrawerRecon.dtsx`,
  '  <Schedule>daily 06:15</Schedule>\n' + String.raw`  <Trigger type="file" path="D:\Import\CashVault\cashvault_current.csv"/>` + '\n',
);
const EXTRACT = job('TLR_DW_EXTRACT', String.raw`bcp "SELECT TXN_REF, AMOUNT FROM TLR_POSTED" queryout \\dw-stage\teller\ -c -T`);

const DTSX = String.raw`<?xml version="1.0"?>
<DTS:Executable xmlns:DTS="www.microsoft.com/SqlServer/Dts"
  DTS:refId="Package" DTS:CreationName="Microsoft.Package" DTS:ObjectName="DrawerRecon" DTS:Description="Morning drawer reconciliation">
  <DTS:ConnectionManagers>
    <DTS:ConnectionManager DTS:refId="Package.ConnectionManagers[VaultFile]" DTS:CreationName="FLATFILE" DTS:ObjectName="VaultFile">
      <DTS:ObjectData>
        <DTS:ConnectionManager DTS:ConnectionString="D:\Import\CashVault\cashvault_current.csv" />
      </DTS:ObjectData>
    </DTS:ConnectionManager>
    <DTS:ConnectionManager DTS:refId="Package.ConnectionManagers[Teller]" DTS:CreationName="OLEDB" DTS:ObjectName="Teller">
      <DTS:ObjectData>
        <DTS:ConnectionManager DTS:ConnectionString="Data Source=SQLPRD01;Initial Catalog=TELLER;Provider=SQLNCLI11.1;Integrated Security=SSPI;" />
      </DTS:ObjectData>
    </DTS:ConnectionManager>
  </DTS:ConnectionManagers>
  <DTS:Executables>
    <DTS:Executable DTS:refId="Package\Read vault path" DTS:CreationName="Microsoft.ScriptTask" DTS:ObjectName="Read vault path">
      <DTS:ObjectData>
        <ScriptProject Name="ST_ReadPath">
          <ProjectItem Name="ScriptMain.cs"><![CDATA[
            var path = config["Reconciliation:VaultFilePath"];
            if (!File.Exists(path)) { Dts.TaskResult = (int)ScriptResults.Success; return; }
          ]]></ProjectItem>
        </ScriptProject>
      </DTS:ObjectData>
    </DTS:Executable>
    <DTS:Executable DTS:refId="Package\Import vault file" DTS:CreationName="Microsoft.Pipeline" DTS:ObjectName="Import vault file">
      <DTS:ObjectData>
        <pipeline>
          <components>
            <component refId="Package\Import vault file\Vault source" componentClassID="Microsoft.FlatFileSource" name="Vault source">
              <connections>
                <connection refId="Package\Import vault file\Vault source.Connections[FlatFileConnection]" connectionManagerID="Package.ConnectionManagers[VaultFile]" name="FlatFileConnection" />
              </connections>
            </component>
            <component refId="Package\Import vault file\Vault destination" componentClassID="Microsoft.OLEDBDestination" name="Vault destination">
              <properties>
                <property name="OpenRowset" dataType="System.String">[dbo].[TLR_VAULT_FILE]</property>
              </properties>
            </component>
          </components>
        </pipeline>
      </DTS:ObjectData>
    </DTS:Executable>
    <DTS:Executable DTS:refId="Package\Reconcile" DTS:CreationName="Microsoft.ExecuteSQLTask" DTS:ObjectName="Reconcile">
      <DTS:ObjectData>
        <SQLTask:SqlTaskData xmlns:SQLTask="www.microsoft.com/sqlserver/dts/tasks/sqltask" SQLTask:SqlStatementSource="EXEC dbo.usp_ReconcileDrawers @Mode='RECON'" />
      </DTS:ObjectData>
    </DTS:Executable>
  </DTS:Executables>
</DTS:Executable>
`;

const CONFIG = JSON.stringify({ Reconciliation: { VaultFilePath: '\\\\vault-prd\\export\\' }, Ofac: { TimeoutSeconds: 15 } }, null, 2) + '\n';

const VAULT_FILE = String.raw`D:\Import\CashVault\cashvault_current.csv`;
// An ordinary string: a raw template cannot end in a backslash (it would escape the closing backtick).
const SHARE = '\\\\dw-stage\\teller\\';

type Node = { ref: string; type: string; name: string; declared: boolean; attrs: Record<string, any> | null; evidenceIds: string[]; paths: string[] };
type Edge = { relation: string; from: string; to: string; confidence: string; attrs: Record<string, any> | null; evidenceIds: string[] };
type GraphBody = { nodes: Node[]; edges: Edge[]; types: { type: string; label: string; count: number }[] };

async function graph(page: Page, id: string, query = '', persona = 'engineer'): Promise<GraphBody> {
  const res = await page.request.get(`${API_BASE}/api/v1/corpora/${id}/entity-graph${query}`, { headers: { 'X-Dev-Persona': persona } });
  expect(res.status()).toBe(200);
  return res.json();
}

const triple = (e: Edge) => `${e.relation} | ${e.from} | ${e.to}`;
const node = (g: GraphBody, ref: string) => g.nodes.find((n) => n.ref === ref);

async function fullProject(page: Page) {
  const id = await newProject(page, ['sql', 'job', 'etl', 'config'], { prefix: PREFIX });
  const r = await attach(id, [
    { path: 'db/ddl.sql', data: DDL },
    { path: 'db/procs/usp_PostStage.sql', data: PROC },
    { path: 'batch/uc4/TLR_EOD.xml', data: PLAN },
    { path: 'batch/uc4/TLR_EOD_POST.xml', data: POST },
    { path: 'batch/uc4/TLR_AM_RECON.xml', data: RECON },
    { path: 'batch/uc4/TLR_DW_EXTRACT.xml', data: EXTRACT },
    { path: 'batch/ssis/DrawerRecon.dtsx', data: DTSX },
    { path: 'config/appsettings.PROD.json', data: CONFIG },
  ]);
  return { id, files: r.body.files as { path: string; status: string; chunks: number; notes?: string[] }[] };
}

test.describe('Entity graph · API', () => {
  test('every file says what was read from it', async ({ page }) => {
    const { files } = await fullProject(page);
    const note = (path: string) => byPath(files as never, path).notes?.[0];
    expect(note('db/ddl.sql')).toBe('Read 3 tables and 1 view.');
    expect(note('db/procs/usp_PostStage.sql')).toBe('Read 1 stored procedure.');
    expect(note('batch/uc4/TLR_EOD.xml')).toBe('Read 1 job plan with 5 tasks.');
    expect(note('batch/uc4/TLR_EOD_POST.xml')).toBe('Read 1 job.');
    expect(note('batch/ssis/DrawerRecon.dtsx')).toBe('Read 1 package with 2 connections and 3 tasks.');
    expect(note('config/appsettings.PROD.json')).toBe('Read 2 settings.');
  });

  test('scripts, scheduler exports, a package and settings become one graph', async ({ page }) => {
    const { id } = await fullProject(page);
    const g = await graph(page, id);

    expect(g.types.map((t) => [t.type, t.count])).toEqual([
      ['config-key', 2], ['file', 2], ['job', 7], ['package', 1], ['procedure', 2], ['table', 5], ['view', 1],
    ]);

    const edges = g.edges.map(triple);
    for (const expected of [
      'runs | job:TLR_EOD_POST | procedure:usp_PostStage',
      'writes | procedure:usp_PostStage | table:TLR_STAGE',
      'writes | procedure:usp_PostStage | table:TLR_POSTED',
      'reads | procedure:usp_PostStage | table:TLR_TXN',
      'contains | job:TLR_EOD | job:TLR_EOD_POST',
      'precedes | job:TLR_EOD_POST | job:TLR_DW_EXTRACT',
      'precedes | job:TLR_EOD_POST | job:TLR_EOD_REPORT',
      'precedes | job:TLR_EOD_LOCK | job:TLR_EOD_COUNT',
      'runs | job:TLR_AM_RECON | package:DrawerRecon',
      `triggers | file:${VAULT_FILE} | job:TLR_AM_RECON`,
      `writes | job:TLR_DW_EXTRACT | file:${SHARE}`,
      `reads | job:TLR_DW_EXTRACT | table:TLR_POSTED`,
      `reads | package:DrawerRecon | file:${VAULT_FILE}`,
      'writes | package:DrawerRecon | table:TLR_VAULT_FILE',
      'runs | package:DrawerRecon | procedure:usp_ReconcileDrawers',
      'configures | config-key:Reconciliation:VaultFilePath | package:DrawerRecon',
      'reads | view:vw_DrawerLimits | table:TLR_DRAWER',
    ]) expect(edges).toContain(expected);
  });

  test('a thing only mentioned is kept, and marked as defined nowhere', async ({ page }) => {
    const { id } = await fullProject(page);
    const g = await graph(page, id);

    // Defined by an attached file.
    for (const ref of ['table:TLR_STAGE', 'procedure:usp_PostStage', 'job:TLR_EOD_POST', 'job:TLR_EOD', 'package:DrawerRecon', 'view:vw_DrawerLimits'])
      expect(node(g, ref)?.declared, ref).toBe(true);
    // Named by something else only: the job export for these is not attached, nor the DDL for the package's table.
    for (const ref of ['job:TLR_EOD_LOCK', 'job:TLR_EOD_COUNT', 'job:TLR_EOD_REPORT', 'table:TLR_VAULT_FILE', 'table:TLR_DRAWER', 'procedure:usp_ReconcileDrawers'])
      expect(node(g, ref)?.declared, ref).toBe(false);
    // A job that runs a procedure and the script that defines it are one procedure, with both pieces of evidence.
    const proc = node(g, 'procedure:usp_PostStage')!;
    expect(proc.paths).toEqual(['db/procs/usp_PostStage.sql']);
    expect(proc.evidenceIds).toHaveLength(1);
  });

  test('the plan states its restart point on the task, and that task is a chunk of its own', async ({ page }) => {
    const { id } = await fullProject(page);
    const g = await graph(page, id);

    const contains = g.edges.find((e) => triple(e) === 'contains | job:TLR_EOD | job:TLR_EOD_POST')!;
    expect(contains.attrs).toMatchObject({ task: 3, restartPoint: true });
    expect(g.edges.filter((e) => e.relation === 'contains' && e.attrs?.restartPoint)).toHaveLength(1);

    const [chunk] = contains.evidenceIds;
    const detail = (await getEvidence(page, id, chunk!)).body;
    expect(detail.locator).toBe('xpath:/JOBP/Tasks/Task[3]');
    expect(detail.text).toContain('Restart="true"');
    expect(detail.view).toMatchObject({ highlightStart: 10, highlightEnd: 10 });

    const plan = node(g, 'job:TLR_EOD')!;
    expect(plan.attrs).toMatchObject({ kind: 'jobplan', schedule: 'Mon-Fri 20:30', host: 'UC4AGENT-TLR01', restartTask: 3, restartJob: 'TLR_EOD_POST', taskCount: 5 });
    expect(plan.attrs?.notify).toEqual(['teller-ops@brfc.example']);
    expect(plan.attrs?.pages).toBe('oncall');
  });

  test('a table is a chunk with its keys, and the entity points at it', async ({ page }) => {
    const { id } = await fullProject(page);
    const hit = (await listEvidence(page, id, { q: 'UQ_TLR_STAGE_TXN_REF', kind: 'sql' })).data;
    expect(hit.map((e) => e.label)).toEqual(['TABLE TLR_STAGE']);
    expect(hit[0]!.locator).toBe('L11-18');

    const d = (await getEvidence(page, id, hit[0]!.shortId)).body;
    expect(d.text).toContain('CONSTRAINT UQ_TLR_STAGE_TXN_REF UNIQUE (TXN_REF)');
    expect(d.structured.keys.map((k: { name: string; kind: string }) => [k.name, k.kind])).toEqual([['PK_TLR_STAGE', 'primary'], ['UQ_TLR_STAGE_TXN_REF', 'unique']]);

    const g = await graph(page, id);
    const table = node(g, 'table:TLR_STAGE')!;
    expect(table.evidenceIds).toEqual([hit[0]!.shortId]);
    expect(table.attrs?.uniqueKeys[0]).toMatchObject({ name: 'UQ_TLR_STAGE_TXN_REF', columns: ['TXN_REF'] });
    expect(table.attrs?.primaryKey).toEqual(['STAGE_ID']);
  });

  test('every edge and entity cites evidence that resolves', async ({ page }) => {
    const { id } = await fullProject(page);
    const g = await graph(page, id);
    const ids = new Set<string>([...g.nodes.flatMap((n) => n.evidenceIds), ...g.edges.flatMap((e) => e.evidenceIds)]);
    expect(ids.size).toBeGreaterThan(15);
    const res = await page.request.post(`${API_BASE}/api/v1/corpora/${id}/evidence/resolve`, { headers: JSON_ENG, data: { ids: [...ids].slice(0, 200) } });
    const body = await res.json();
    expect(body.missing).toEqual([]);
    // Every declared thing has at least one piece of evidence behind it.
    for (const n of g.nodes.filter((x) => x.declared)) expect(n.evidenceIds.length, n.ref).toBeGreaterThan(0);
  });

  test('a setting is a thing, with a value for each environment', async ({ page }) => {
    const { id } = await fullProject(page);
    await attach(id, [{ path: 'config/appsettings.DEV.json', data: JSON.stringify({ Reconciliation: { VaultFilePath: '\\\\vault-dev\\exports\\' } }) + '\n' }]);
    const g = await graph(page, id, '?type=config-key');
    const key = node(g, 'config-key:Reconciliation:VaultFilePath')!;
    expect(key.declared).toBe(true);
    expect(key.evidenceIds).toHaveLength(2);
    expect(key.attrs?.values.map((v: { file: string; value: string }) => [v.file, v.value])).toEqual([
      ['config/appsettings.DEV.json', '\\\\vault-dev\\exports\\'],
      ['config/appsettings.PROD.json', '\\\\vault-prd\\export\\'],
    ]);
  });

  test('the entity detail lists what is linked to it, in both directions', async ({ page }) => {
    const { id } = await fullProject(page);
    const res = await page.request.get(`${API_BASE}/api/v1/corpora/${id}/entities/job/TLR_EOD_POST`);
    expect(res.status()).toBe(200);
    const d = await res.json();
    expect(d.entity.ref).toBe('job:TLR_EOD_POST');
    const links = d.links.map((l: { direction: string; relation: string; other: string }) => `${l.direction} ${l.relation} ${l.other}`);
    expect(links).toEqual(expect.arrayContaining([
      'in contains job:TLR_EOD',
      'in precedes job:TLR_EOD_COUNT',
      'out precedes job:TLR_DW_EXTRACT',
      'out precedes job:TLR_EOD_REPORT',
      'out runs procedure:usp_PostStage',
    ]));
  });

  test('a name that is a path is found by its path', async ({ page }) => {
    const { id } = await fullProject(page);
    const res = await page.request.get(`${API_BASE}/api/v1/corpora/${id}/entities/file/${encodeURIComponent(VAULT_FILE)}`);
    expect(res.status()).toBe(200);
    const d = await res.json();
    expect(d.entity.name).toBe(VAULT_FILE);
    expect(d.links.map((l: { direction: string; relation: string; other: string }) => `${l.direction} ${l.relation} ${l.other}`)).toEqual(
      expect.arrayContaining(['out triggers job:TLR_AM_RECON', 'in reads package:DrawerRecon']),
    );
  });

  test('type filters, search and paging; a missing thing and a missing project are 404', async ({ page }) => {
    const { id } = await fullProject(page);

    const onlyJobs = await graph(page, id, '?type=job');
    expect(onlyJobs.nodes.every((n) => n.type === 'job')).toBe(true);
    expect(onlyJobs.edges.every((e) => e.from.startsWith('job:') && e.to.startsWith('job:'))).toBe(true);
    expect(onlyJobs.types.length).toBeGreaterThan(1); // the facets still describe the whole graph

    const list = async (qs: string) => (await page.request.get(`${API_BASE}/api/v1/corpora/${id}/entities${qs}`)).json();
    const jobs = await list('?type=job');
    expect(jobs.total).toBe(7);
    expect(jobs.data.map((n: Node) => n.name)).toEqual(
      ['TLR_AM_RECON', 'TLR_DW_EXTRACT', 'TLR_EOD', 'TLR_EOD_COUNT', 'TLR_EOD_LOCK', 'TLR_EOD_POST', 'TLR_EOD_REPORT'],
    );
    expect((await list('?q=staging')).total).toBe(0);
    expect((await list('?q=usp_post')).data.map((n: Node) => n.ref)).toEqual(['procedure:usp_PostStage']);
    // LIKE wildcards are text, not patterns.
    expect((await list('?q=%25')).total).toBe(0);
    expect((await list('?q=TLR_STAG_')).total).toBe(0);
    const p1 = await list('?limit=4&offset=0');
    const p2 = await list('?limit=4&offset=4');
    expect(p1.total).toBe(20);
    expect(p1.data).toHaveLength(4);
    expect(new Set([...p1.data, ...p2.data].map((n: Node) => n.ref)).size).toBe(8);

    expect((await page.request.get(`${API_BASE}/api/v1/corpora/${id}/entities/table/NO_SUCH_TABLE`)).status()).toBe(404);
    expect((await page.request.get(`${API_BASE}/api/v1/corpora/00000000-0000-0000-0000-000000000000/entity-graph`)).status()).toBe(404);
  });

  test('anyone can read the graph, including an Observer', async ({ page }) => {
    const { id } = await fullProject(page);
    const g = await graph(page, id, '', 'observer');
    expect(g.nodes).toHaveLength(20);
  });

  test('replacing a file rebuilds the graph; removing one takes what only it said away', async ({ page }) => {
    const id = await newProject(page, ['sql', 'job'], { prefix: PREFIX });
    await attach(id, [
      { path: 'db/procs/usp_PostStage.sql', data: PROC },
      { path: 'batch/uc4/TLR_EOD_POST.xml', data: POST },
    ]);
    let g = await graph(page, id);
    expect(node(g, 'procedure:usp_PostStage')?.declared).toBe(true);
    expect(g.edges.map(triple)).toContain('writes | procedure:usp_PostStage | table:TLR_STAGE');

    // The job now runs another procedure: the old link is gone, the new one is there.
    await attach(id, [{ path: 'batch/uc4/TLR_EOD_POST.xml', data: job('TLR_EOD_POST', 'sqlcmd -Q "EXEC dbo.usp_Other"') }]);
    g = await graph(page, id);
    expect(g.edges.map(triple)).toContain('runs | job:TLR_EOD_POST | procedure:usp_Other');
    expect(g.edges.map(triple)).not.toContain('runs | job:TLR_EOD_POST | procedure:usp_PostStage');
    expect(node(g, 'procedure:usp_Other')?.declared).toBe(false);

    // Remove the script that defines usp_PostStage: it is no longer a thing the project has.
    await page.request.delete(`${API_BASE}/api/v1/corpora/${id}/artefacts?path=${encodeURIComponent('db/procs/usp_PostStage.sql')}`, { headers: JSON_ENG });
    g = await graph(page, id);
    expect(node(g, 'procedure:usp_PostStage')).toBeUndefined();
    expect(node(g, 'table:TLR_STAGE')).toBeUndefined();
    expect(node(g, 'job:TLR_EOD_POST')?.declared).toBe(true);

    // Remove the job as well: the graph is empty.
    await page.request.delete(`${API_BASE}/api/v1/corpora/${id}/artefacts?path=${encodeURIComponent('batch/uc4/TLR_EOD_POST.xml')}`, { headers: JSON_ENG });
    g = await graph(page, id);
    expect(g.nodes).toEqual([]);
    expect(g.edges).toEqual([]);
  });

  test('a re-sync carries the graph forward under the same references and evidence ids', async ({ page }) => {
    const { id } = await fullProject(page);
    const before = await graph(page, id);

    const resync = await page.request.post(`${API_BASE}/api/v1/corpora/${id}/reingest/text`, {
      headers: JSON_ENG,
      data: { files: [{ path: 'e2eart.f', content: FORTRAN + 'C     changed\n' }] },
    });
    expect(resync.status()).toBe(200);

    const after = await graph(page, id);
    expect(after.nodes.map((n) => n.ref)).toEqual(before.nodes.map((n) => n.ref));
    expect(after.edges.map(triple)).toEqual(before.edges.map(triple));
    expect(after.nodes.map((n) => n.evidenceIds)).toEqual(before.nodes.map((n) => n.evidenceIds));
    expect(after.nodes.map((n) => n.declared)).toEqual(before.nodes.map((n) => n.declared));
  });

  test('an XML file that is not a scheduler export is chunked, says so, and adds nothing to the graph', async ({ page }) => {
    const id = await newProject(page, ['job'], { prefix: PREFIX });
    const r = await attach(id, [{ path: 'batch/uc4/odd.xml', data: '<Settings><Item Name="a"/></Settings>\n' }]);
    const f = byPath(r.body.files, 'batch/uc4/odd.xml');
    expect(f.status).toBe('added');
    expect(f.chunks).toBe(1);
    expect(f.notes?.[0]).toContain('does not look like a scheduler export');
    expect((await graph(page, id)).nodes).toEqual([]);
  });

  test('SQL that cannot be parsed is read by pattern, says so, and its links are medium confidence', async ({ page }) => {
    const id = await newProject(page, ['sql'], { prefix: PREFIX });
    const r = await attach(id, [{
      path: 'db/procs/broken.sql',
      data: 'CREATE PROCEDURE usp_Broken AS\nBEGIN\n    SELEKT * FROM TLR_TXN WHERE;\nEND\n',
    }]);
    const f = byPath(r.body.files, 'db/procs/broken.sql');
    expect(f.chunks).toBe(1);
    expect(f.notes?.some((n: string) => n.includes('read by pattern') && n.includes('medium confidence'))).toBe(true);

    const g = await graph(page, id);
    expect(node(g, 'procedure:usp_Broken')?.attrs?.parsedBy).toBe('pattern');
    expect(g.edges.find((e) => triple(e) === 'reads | procedure:usp_Broken | table:TLR_TXN')?.confidence).toBe('medium');
  });
});
