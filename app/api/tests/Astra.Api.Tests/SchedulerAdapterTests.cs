using System.Text.Json;
using Astra.Api.Evidence;
using Xunit;

namespace Astra.Api.Tests;

public class ScriptCommandReaderTests
{
    private static ScriptCommands Read(string script) => ScriptCommandReader.Read(script);

    [Fact]
    public void AStoredProcedureRun_ByASqlCommandLine_IsFoundWithItsArguments()
    {
        var c = Read("sqlcmd -S %TLR_SQL% -d TELLER -b -Q \"EXEC dbo.usp_CloseDrawer @Mode='EOD_LOCK'\"");
        var p = Assert.Single(c.Procedures);
        Assert.Equal(("usp_CloseDrawer", "@Mode='EOD_LOCK'"), (p.Name, p.Arguments));
    }

    [Theory]
    [InlineData("EXEC usp_PostStage")]
    [InlineData("exec [dbo].[usp_PostStage];")]
    [InlineData("EXECUTE dbo.usp_PostStage")]
    [InlineData("osql -E -Q \"execute usp_PostStage\"")]
    public void TheCommonWaysToExecuteAProcedure_AreRead(string script) =>
        Assert.Equal("usp_PostStage", Assert.Single(Read(script).Procedures).Name);

    [Fact]
    public void ASystemProcedureOrAVariable_IsNotAnApplicationProcedure()
    {
        var c = Read("EXEC sp_executesql @sql\nEXEC xp_cmdshell 'dir'\nEXEC @proc");
        Assert.Empty(c.Procedures);
    }

    [Fact]
    public void TheSameProcedureTwice_IsOnce() =>
        Assert.Single(Read("EXEC usp_A\nEXEC usp_A @x=1").Procedures);

    [Theory]
    [InlineData("dtexec /F D:\\SSIS\\DrawerRecon.dtsx", "DrawerRecon", "D:\\SSIS\\DrawerRecon.dtsx")]
    [InlineData("dtexec /File \"D:\\SSIS Packages\\Drawer Recon.dtsx\" /Reporting E", "Drawer Recon", "D:\\SSIS Packages\\Drawer Recon.dtsx")]
    [InlineData("dtexec.exe /F D:\\SSIS\\DrawerRecon.dtsx /Reporting E /MaxConcurrent 2", "DrawerRecon", "D:\\SSIS\\DrawerRecon.dtsx")]
    public void ADtexecPackageRun_NamesThePackageByItsFile(string script, string name, string path)
    {
        var p = Assert.Single(Read(script).Packages);
        Assert.Equal((name, path), (p.Name, p.Path));
    }

    [Fact]
    public void ADtexecOfAStoredPackage_NamesThePackage() =>
        Assert.Equal("DrawerRecon", Assert.Single(Read("dtexec /SQ \\Teller\\DrawerRecon /SERVER sql01").Packages).Name);

    [Fact]
    public void ABcpExportOfAQuery_IsAWriteToTheFile_AndAReadOfTheTables()
    {
        var c = Read("bcp \"SELECT TXN_REF, AMOUNT FROM TLR_POSTED p JOIN TLR_TXN t ON t.ID = p.ID\" queryout \\\\dw-stage\\teller\\posted.dat -c -T -S %TLR_SQL%");
        var f = Assert.Single(c.FilesWritten);
        Assert.Equal(("\\\\dw-stage\\teller\\posted.dat", "bcp"), (f.Path, f.Tool));
        Assert.Equal(new[] { "TLR_POSTED", "TLR_TXN" }, c.TablesRead.OrderBy(t => t, StringComparer.Ordinal));
    }

    [Fact]
    public void ABcpExportToAShareFolder_KeepsTheFolderAsTheTarget()
    {
        var f = Assert.Single(Read("bcp \"SELECT 1 FROM TLR_POSTED\" queryout \\\\dw-stage\\teller\\ -c -T").FilesWritten);
        Assert.Equal("\\\\dw-stage\\teller\\", f.Path);
    }

    [Fact]
    public void ABcpOfATable_IsAReadOrAWriteOfIt()
    {
        var outbound = Read("bcp TELLER.dbo.TLR_POSTED out D:\\x\\posted.dat -c -T");
        Assert.Equal("D:\\x\\posted.dat", Assert.Single(outbound.FilesWritten).Path);
        Assert.Equal(new[] { "TELLER.dbo.TLR_POSTED" }, outbound.TablesRead);

        var inbound = Read("bcp TLR_STAGE in D:\\x\\stage.dat -c -T");
        Assert.Equal("D:\\x\\stage.dat", Assert.Single(inbound.FilesRead).Path);
        Assert.Equal(new[] { "TLR_STAGE" }, inbound.TablesWritten);
    }

    [Theory]
    [InlineData("powershell D:\\Scripts\\EodReport.ps1", "D:\\Scripts\\EodReport.ps1")]
    [InlineData("powershell.exe -NoProfile -File \"\\\\fs01\\ops\\run.ps1\"", "\\\\fs01\\ops\\run.ps1")]
    [InlineData("C:\\Jobs\\nightly.bat", "C:\\Jobs\\nightly.bat")]
    [InlineData("/opt/jobs/nightly.sh --full", "/opt/jobs/nightly.sh")]
    public void AScriptTheCommandStarts_IsFound(string script, string path) =>
        Assert.Equal(path, Assert.Single(Read(script).Scripts));

    [Fact]
    public void ATableInAQueryOnTheCommandLine_IsRead()
    {
        var c = Read("sqlcmd -S s -Q \"INSERT INTO TLR_LOG SELECT * FROM TLR_STAGE\"");
        Assert.Equal(new[] { "TLR_STAGE" }, c.TablesRead);
        Assert.Equal(new[] { "TLR_LOG" }, c.TablesWritten);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("echo hello")]
    public void NothingRecognisable_IsNothing(string? script) => Assert.True(Read(script!).IsEmpty);
}

public class JobAdapterTests
{
    private static AdapterResult Build(string xml, string path = "batch/uc4/TLR_EOD.xml") => AdapterTestKit.Build(path, "job", xml);

    private static readonly string Plan = AdapterTestKit.Lines(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
        "<JOBP name=\"TLR_EOD\">",
        "  <Title>Teller end of day</Title>",
        "  <Host>UC4AGENT-TLR01</Host>",
        "  <Schedule>Mon-Fri 20:30</Schedule>",
        "  <Notify onError=\"teller-ops@brfc.example\" page=\"oncall\"/>",
        "  <Tasks>",
        "    <Task Num=\"1\" Job=\"TLR_EOD_LOCK\"/>",
        "    <Task Num=\"2\" Job=\"TLR_EOD_COUNT\" After=\"1\"/>",
        "    <Task Num=\"3\" Job=\"TLR_EOD_POST\" After=\"2\" Restart=\"true\"/>",
        "    <Task Num=\"4\" Job=\"TLR_DW_EXTRACT\" After=\"3\"/>",
        "    <Task Num=\"5\" Job=\"TLR_EOD_REPORT\" After=\"3\"/>",
        "  </Tasks>",
        "</JOBP>");

    private static JsonElement Attr(EntityFact e, string key) => JsonSerializer.SerializeToElement(e.Attrs![key]);

    private static IEnumerable<EntityFact> PlanFacts(AdapterResult r) =>
        r.Facts!.Entities.Where(e => e.Type == "job" && e.Name == "TLR_EOD");

    private static JsonElement PlanAttr(AdapterResult r, string key) => Attr(PlanFacts(r).First(e => e.Attrs!.ContainsKey(key)), key);

    [Fact]
    public void EveryTaskOfAPlan_IsAChunkOfItsOwn_NextToOneForEachHeaderElement()
    {
        var r = Build(Plan);
        Assert.Equal(new[]
        {
            "xpath:/JOBP", "xpath:/JOBP/Title", "xpath:/JOBP/Host", "xpath:/JOBP/Schedule", "xpath:/JOBP/Notify", "xpath:/JOBP/Tasks",
            "xpath:/JOBP/Tasks/Task[1]", "xpath:/JOBP/Tasks/Task[2]", "xpath:/JOBP/Tasks/Task[3]", "xpath:/JOBP/Tasks/Task[4]", "xpath:/JOBP/Tasks/Task[5]",
        }, r.Chunks.Select(c => c.Locator));
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void TheRestartPointTask_IsCitableOnItsOwn_WithItsLine()
    {
        var task = Build(Plan).Chunks.Single(c => c.Locator == "xpath:/JOBP/Tasks/Task[3]");
        Assert.Equal((10, 10), (task.LineStart, task.LineEnd));
        Assert.Contains("Restart=\"true\"", task.Text);
        Assert.Contains("TLR_EOD_POST", task.Text);
    }

    [Fact]
    public void ThePlanEntity_CarriesWhatEachHeaderElementSays_EachFromItsOwnChunk()
    {
        var r = Build(Plan);
        Assert.Equal("Teller end of day", PlanAttr(r, "title").GetString());
        Assert.Equal("UC4AGENT-TLR01", PlanAttr(r, "host").GetString());
        Assert.Equal("Mon-Fri 20:30", PlanAttr(r, "schedule").GetString());
        Assert.Equal(new[] { "teller-ops@brfc.example" }, PlanAttr(r, "notify").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("oncall", PlanAttr(r, "pages").GetString());

        string LocatorOf(string key) => PlanFacts(r).First(e => e.Attrs!.ContainsKey(key)).Locator!;
        Assert.Equal("xpath:/JOBP/Schedule", LocatorOf("schedule"));
        Assert.Equal("xpath:/JOBP/Host", LocatorOf("host"));
        Assert.Equal("xpath:/JOBP/Notify", LocatorOf("notify"));
        Assert.Equal("jobplan", PlanAttr(r, "kind").GetString());
    }

    [Fact]
    public void TheRestartPoint_IsFactOfThePlan_ShownByTheTaskThatCarriesTheMark()
    {
        var r = Build(Plan);
        var fact = PlanFacts(r).First(e => e.Attrs!.ContainsKey("restartTask"));
        Assert.Equal(3, Attr(fact, "restartTask").GetInt32());
        Assert.Equal("TLR_EOD_POST", Attr(fact, "restartJob").GetString());
        Assert.Equal("xpath:/JOBP/Tasks/Task[3]", fact.Locator);
    }

    [Fact]
    public void ThePlanContainsItsJobs_InTaskOrder_AndOnlyTheRestartTaskSaysSo()
    {
        var r = Build(Plan);
        var contains = r.Facts!.Links.Where(l => l.Relation == "contains").ToList();
        Assert.Equal(new[] { "TLR_EOD_LOCK", "TLR_EOD_COUNT", "TLR_EOD_POST", "TLR_DW_EXTRACT", "TLR_EOD_REPORT" }, contains.Select(l => l.To.Name));
        Assert.All(contains, l => Assert.Equal("TLR_EOD", l.From.Name));
        var restart = contains.Where(l => l.Attrs!.ContainsKey("restartPoint")).ToList();
        Assert.Equal("TLR_EOD_POST", Assert.Single(restart).To.Name);
        Assert.Equal("xpath:/JOBP/Tasks/Task[3]", restart[0].Locator);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, contains.Select(l => JsonSerializer.SerializeToElement(l.Attrs!["task"]).GetInt32()));
    }

    [Fact]
    public void ATaskRunsAfterTheTasksItNames_ResolvedByNumber()
    {
        var order = Build(Plan).Facts!.Links.Where(l => l.Relation == "precedes").Select(l => (l.From.Name, l.To.Name)).ToList();
        Assert.Equal(new[]
        {
            ("TLR_EOD_LOCK", "TLR_EOD_COUNT"), ("TLR_EOD_COUNT", "TLR_EOD_POST"), ("TLR_EOD_POST", "TLR_DW_EXTRACT"), ("TLR_EOD_POST", "TLR_EOD_REPORT"),
        }, order);
    }

    [Fact]
    public void TheTaskListIsAnAttributeOfThePlan_AndTheSummarySaysWhatWasRead()
    {
        var r = Build(Plan);
        Assert.Equal(5, PlanAttr(r, "taskCount").GetInt32());
        Assert.Equal("TLR_EOD_POST", PlanAttr(r, "tasks")[2].GetProperty("job").GetString());
        Assert.Equal("Read 1 job plan with 5 tasks.", r.Summary);
    }

    [Fact]
    public void EveryFactPointsAtAChunkThatExists()
    {
        var r = Build(Plan);
        var chunks = r.Chunks.Select(c => c.Locator).ToHashSet();
        Assert.All(r.Facts!.Entities, e => Assert.Contains(e.Locator!, chunks));
        Assert.All(r.Facts.Links, l => Assert.Contains(l.Locator!, chunks));
    }

    [Fact]
    public void ARestartPointStatedInTheHeader_IsFoundToo()
    {
        var r = Build("<JOBP name=\"P\">\n  <Restart task=\"2\"/>\n  <Tasks>\n    <Task Num=\"1\" Job=\"A\"/>\n    <Task Num=\"2\" Job=\"B\" After=\"1\"/>\n  </Tasks>\n</JOBP>\n");
        var fact = r.Facts!.Entities.First(e => e.Attrs!.ContainsKey("restartTask"));
        Assert.Equal("B", Attr(fact, "restartJob").GetString());
        Assert.Equal("xpath:/JOBP/Restart", fact.Locator);
        Assert.Contains(r.Facts.Links, l => l.Relation == "contains" && l.To.Name == "B" && l.Attrs!.ContainsKey("restartPoint"));
    }

    [Fact]
    public void PredecessorsByName_TasksWithoutNumbers_AndHeaderValuesAsAttributes_AreAllRead()
    {
        var r = Build("<JOBP name=\"P\" host=\"H1\" schedule=\"daily 06:00\">\n  <Tasks>\n    <Task Job=\"A\"/>\n    <Task Job=\"B\" After=\"A\"/>\n    <Task Job=\"C\" After=\"A, B\"/>\n  </Tasks>\n</JOBP>\n");
        Assert.Equal(new[] { ("A", "B"), ("A", "C"), ("B", "C") }, r.Facts!.Links.Where(l => l.Relation == "precedes").Select(l => (l.From.Name, l.To.Name)));
        Assert.Equal("H1", PlanAttrOf(r, "P", "host"));
        Assert.Equal("daily 06:00", PlanAttrOf(r, "P", "schedule"));
        Assert.Equal(new[] { 1, 2, 3 }, r.Facts.Links.Where(l => l.Relation == "contains").Select(l => JsonSerializer.SerializeToElement(l.Attrs!["task"]).GetInt32()));
    }

    private static string? PlanAttrOf(AdapterResult r, string name, string key) =>
        r.Facts!.Entities.Where(e => e.Name == name && e.Attrs is not null && e.Attrs.ContainsKey(key)).Select(e => Attr(e, key).GetString()).FirstOrDefault();

    [Fact]
    public void ATaskWithNoJob_AndAPredecessorThatIsNotInThePlan_AreWarnedAbout_NotInvented()
    {
        var r = Build("<JOBP name=\"P\">\n  <Tasks>\n    <Task Num=\"1\"/>\n    <Task Num=\"2\" Job=\"B\" After=\"9\"/>\n  </Tasks>\n</JOBP>\n");
        Assert.Equal(new[] { "Task 1 of P names no job, so it was left out.", "Task 2 of P runs after task 9, which is not in the plan." }, r.Warnings);
        Assert.DoesNotContain(r.Facts!.Links, l => l.Relation == "precedes");
        Assert.Single(r.Facts.Links.Where(l => l.Relation == "contains"));
    }

    [Fact]
    public void APlanWithNoName_IsNotRead()
    {
        var r = Build("<JOBP>\n  <Tasks><Task Job=\"A\"/></Tasks>\n</JOBP>\n");
        Assert.Contains("has no name", r.Warnings[0]);
        Assert.Empty(r.Facts!.Entities);
    }

    // ── jobs ────────────────────────────────────────────────────────────

    private static string JobXml(string name, string script, string extra = "") =>
        $"<JOBS name=\"{name}\" type=\"windows\">\n  <Title>{name} title</Title>\n  <Host>UC4AGENT-TLR01</Host>\n{extra}  <Script><![CDATA[\n{script}\n]]></Script>\n</JOBS>\n";

    private static IEnumerable<LinkFact> LinksOf(AdapterResult r, string relation) => r.Facts!.Links.Where(l => l.Relation == relation);

    [Fact]
    public void AJobThatRunsAProcedure_LinksToIt_FromTheChunkThatShowsTheCommand()
    {
        var r = Build(JobXml("TLR_EOD_POST", "sqlcmd -S %TLR_SQL% -d TELLER -b -Q \"EXEC dbo.usp_PostStage\""), "batch/uc4/TLR_EOD_POST.xml");
        var run = Assert.Single(LinksOf(r, "runs"));
        Assert.Equal(("TLR_EOD_POST", "procedure", "usp_PostStage", "high"), (run.From.Name, run.To.Type, run.To.Name, run.Confidence));
        Assert.Equal("xpath:/JOBS", run.Locator);
        var job = r.Facts!.Entities.Where(e => e.Name == "TLR_EOD_POST").ToList();
        Assert.Equal("job", Attr(job.First(e => e.Attrs!.ContainsKey("kind")), "kind").GetString());
        Assert.Equal("JOBS", Attr(job.First(e => e.Attrs!.ContainsKey("jobType")), "jobType").GetString());
    }

    [Fact]
    public void TheJobEntity_CarriesItsHostItsTitleAndItsCommand()
    {
        var r = Build(JobXml("TLR_EOD_POST", "sqlcmd -Q \"EXEC usp_PostStage\"\nrem second line"));
        string? Get(string key) => r.Facts!.Entities.Where(e => e.Attrs!.ContainsKey(key)).Select(e => Attr(e, key).GetString()).FirstOrDefault();
        Assert.Equal("UC4AGENT-TLR01", Get("host"));
        Assert.Equal("TLR_EOD_POST title", Get("title"));
        Assert.Equal("sqlcmd -Q \"EXEC usp_PostStage\"", Get("command"));
        Assert.Contains("rem second line", Get("script"));
    }

    [Fact]
    public void AJobThatRunsAPackage_AScript_AndAnExport_IsLinkedToEachOfThem()
    {
        var pkg = Build(JobXml("TLR_AM_RECON", "dtexec /F D:\\SSIS\\DrawerRecon.dtsx"));
        var run = Assert.Single(LinksOf(pkg, "runs"));
        Assert.Equal(("package", "DrawerRecon"), (run.To.Type, run.To.Name));
        Assert.Equal("D:\\SSIS\\DrawerRecon.dtsx", JsonSerializer.SerializeToElement(run.Attrs!["path"]).GetString());

        var script = Build(JobXml("TLR_EOD_REPORT", "powershell D:\\Scripts\\EodReport.ps1"));
        Assert.Equal(("script", "D:\\Scripts\\EodReport.ps1"), (LinksOf(script, "runs").Single().To.Type, LinksOf(script, "runs").Single().To.Name));

        var export = Build(JobXml("TLR_DW_EXTRACT", "bcp \"SELECT TXN_REF FROM TLR_POSTED\" queryout \\\\dw-stage\\teller\\ -c -T"));
        var writes = LinksOf(export, "writes").Single();
        Assert.Equal(("file", "\\\\dw-stage\\teller\\", "high"), (writes.To.Type, writes.To.Name, writes.Confidence));
        var reads = LinksOf(export, "reads").Single();
        Assert.Equal(("table", "TLR_POSTED", "medium"), (reads.To.Type, reads.To.Name, reads.Confidence));
    }

    [Fact]
    public void AFileEventThatStartsAJob_IsALinkFromTheFileToTheJob()
    {
        var r = Build(JobXml("TLR_AM_RECON", "dtexec /F D:\\SSIS\\DrawerRecon.dtsx", "  <Schedule>daily 06:15</Schedule>\n  <Trigger type=\"file\" path=\"D:\\Import\\CashVault\\cashvault_current.csv\"/>\n"));
        var trigger = Assert.Single(LinksOf(r, "triggers"));
        Assert.Equal(("file", "D:\\Import\\CashVault\\cashvault_current.csv", "TLR_AM_RECON"), (trigger.From.Type, trigger.From.Name, trigger.To.Name));
        Assert.Equal("xpath:/JOBS", trigger.Locator);
        Assert.Equal("daily 06:15", r.Facts!.Entities.Where(e => e.Attrs!.ContainsKey("schedule")).Select(e => Attr(e, "schedule").GetString()).Single());
    }

    [Fact]
    public void ATriggerThatIsNotAFileEvent_IsNotOne()
    {
        var r = Build(JobXml("J", "echo", "  <Trigger type=\"time\" path=\"x\"/>\n"));
        Assert.Empty(LinksOf(r, "triggers"));
    }

    [Fact]
    public void AnExportWrapper_WithSeveralObjects_GivesEachItsOwnChunkAndFacts()
    {
        var xml = "<uc-export clientvers=\"12.3.0\">\n  <JOBS name=\"A\"><Script>EXEC usp_A</Script></JOBS>\n  <JOBS_WIN name=\"B\"><Script>EXEC usp_B</Script></JOBS_WIN>\n</uc-export>\n";
        var r = Build(xml);
        Assert.Equal(new[] { "xpath:/uc-export", "xpath:/uc-export/JOBS", "xpath:/uc-export/JOBS_WIN" }, r.Chunks.Select(c => c.Locator));
        var runs = LinksOf(r, "runs").ToList();
        Assert.Equal(new[] { ("A", "usp_A", "xpath:/uc-export/JOBS"), ("B", "usp_B", "xpath:/uc-export/JOBS_WIN") }, runs.Select(l => (l.From.Name, l.To.Name, l.Locator!)));
        Assert.Equal("Read 2 jobs.", r.Summary);
    }

    [Fact]
    public void ALargeJob_IsOpenedUp_AndTheCommandIsCitedByItsOwnChunk()
    {
        var filler = string.Concat(Enumerable.Range(1, 70).Select(i => $"  <Param Name=\"p{i}\" Value=\"v{i}\"/>\n"));
        var xml = $"<JOBS name=\"BIG\">\n{filler}  <Script><![CDATA[\nEXEC usp_Big\n]]></Script>\n</JOBS>\n";
        var r = Build(xml);
        Assert.Equal("xpath:/JOBS/Script", LinksOf(r, "runs").Single().Locator);
        Assert.Contains(r.Chunks, c => c.Locator == "xpath:/JOBS/Script");
    }

    [Fact]
    public void AnUnrecognisedXmlFile_IsStillChunked_AndSaysNoJobsWereRead()
    {
        var r = Build("<Settings><Item Name=\"a\"/></Settings>\n");
        Assert.Single(r.Chunks);
        Assert.Contains("does not look like a scheduler export", r.Warnings.Single());
        Assert.True(r.Facts!.IsEmpty);
    }

    [Fact]
    public void BrokenXml_FallsBackToLines_WithNoFacts()
    {
        var r = Build("<JOBP name=\"P\">\n<Tasks>\n</JOBP>\n");
        Assert.StartsWith("L", r.Chunks[0].Locator);
        Assert.Contains("not well-formed", r.Warnings[0]);
        Assert.True(r.Facts is null || r.Facts.IsEmpty);
    }

    [Fact]
    public void AJobWithNoName_IsWarnedAbout() =>
        Assert.Contains("has no name", Build("<JOBS><Script>x</Script></JOBS>\n").Warnings[0]);

    [Fact]
    public void TheRegistryUsesItForJobExports() => Assert.IsType<JobAdapter>(EvidenceAdapters.AdapterFor("job", ".xml"));
}

public class SsisAdapterTests
{
    private static AdapterResult Build(string xml, string path = "batch/ssis/DrawerRecon.dtsx") => AdapterTestKit.Build(path, "etl", xml);

    private const string Package = """
        <?xml version="1.0"?>
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
          <DTS:Variables>
            <DTS:Variable DTS:ObjectName="VaultFilePath" DTS:Namespace="User"><DTS:VariableValue DTS:DataType="8">D:\Import\CashVault\cashvault_current.csv</DTS:VariableValue></DTS:Variable>
          </DTS:Variables>
          <DTS:Executables>
            <DTS:Executable DTS:refId="Package\Read vault path" DTS:CreationName="Microsoft.ScriptTask" DTS:ObjectName="Read vault path">
              <DTS:ObjectData>
                <ScriptProject Name="ST_ReadPath">
                  <ProjectItem Name="ScriptMain.cs"><![CDATA[
                    var path = config["Reconciliation:VaultFilePath"];
                    var user = Dts.Variables["User::VaultFilePath"].Value;
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
        """;

    private static IEnumerable<LinkFact> LinksOf(AdapterResult r, string relation) => r.Facts!.Links.Where(l => l.Relation == relation);

    private static JsonElement Attr(EntityFact e, string key) => JsonSerializer.SerializeToElement(e.Attrs![key]);

    [Fact]
    public void EachConnectionVariableAndTask_IsAChunkOfItsOwn()
    {
        var r = Build(Package);
        var locators = r.Chunks.Select(c => c.Locator).ToList();
        Assert.Contains("xpath:/Executable/ConnectionManagers/ConnectionManager[1]", locators);
        Assert.Contains("xpath:/Executable/ConnectionManagers/ConnectionManager[2]", locators);
        Assert.Contains("xpath:/Executable/Variables/Variable", locators);
        Assert.Contains("xpath:/Executable/Executables/Executable[1]", locators);
        Assert.Contains("xpath:/Executable/Executables/Executable[2]", locators);
        Assert.Contains("xpath:/Executable/Executables/Executable[3]", locators);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void ThePackageEntity_IsNamedByItsObjectName_AndCarriesItsConnectionsVariablesAndTasks()
    {
        var r = Build(Package);
        var package = r.Facts!.Entities.Where(e => e.Type == "package" && e.Name == "DrawerRecon").ToList();
        var connections = package.SelectMany(e => e.Attrs!.TryGetValue("connections", out _) ? Attr(e, "connections").EnumerateArray() : Enumerable.Empty<JsonElement>()).ToList();
        Assert.Equal(new[] { "VaultFile", "Teller" }, connections.Select(c => c.GetProperty("name").GetString()));
        Assert.Equal("D:\\Import\\CashVault\\cashvault_current.csv", connections[0].GetProperty("target").GetString());
        Assert.Equal("SQLPRD01/TELLER", connections[1].GetProperty("target").GetString());

        var tasks = package.SelectMany(e => e.Attrs!.TryGetValue("tasks", out _) ? Attr(e, "tasks").EnumerateArray() : Enumerable.Empty<JsonElement>()).ToList();
        Assert.Equal(new[] { ("Read vault path", "Script"), ("Import vault file", "Data flow"), ("Reconcile", "Execute SQL") }, tasks.Select(t => (t.GetProperty("name").GetString()!, t.GetProperty("type").GetString()!)));

        var variables = package.SelectMany(e => e.Attrs!.TryGetValue("variables", out _) ? Attr(e, "variables").EnumerateArray() : Enumerable.Empty<JsonElement>()).Single();
        Assert.Equal("VaultFilePath", variables.GetProperty("name").GetString());
    }

    [Fact]
    public void TheDataFlow_ReadsTheFileItsSourceReadsAndWritesTheTableItsDestinationNames()
    {
        var r = Build(Package);
        var reads = Assert.Single(LinksOf(r, "reads"));
        Assert.Equal(("package", "DrawerRecon", "file", "D:\\Import\\CashVault\\cashvault_current.csv", "high"),
            (reads.From.Type, reads.From.Name, reads.To.Type, reads.To.Name, reads.Confidence));
        Assert.Equal("xpath:/Executable/Executables/Executable[2]", reads.Locator);

        var writes = Assert.Single(LinksOf(r, "writes"));
        Assert.Equal(("table", "TLR_VAULT_FILE"), (writes.To.Type, writes.To.Name));
    }

    [Fact]
    public void AnExecuteSqlTask_RunsTheProcedureItNames()
    {
        var run = Assert.Single(LinksOf(Build(Package), "runs"));
        Assert.Equal(("procedure", "usp_ReconcileDrawers", "high"), (run.To.Type, run.To.Name, run.Confidence));
        Assert.Equal("xpath:/Executable/Executables/Executable[3]", run.Locator);
    }

    [Fact]
    public void AConfigurationKeyAScriptTaskRefersTo_ConfiguresThePackage_ButOnlyAtMediumConfidence()
    {
        var config = Assert.Single(LinksOf(Build(Package), "configures"));
        Assert.Equal(("config-key", "Reconciliation:VaultFilePath", "package", "medium"), (config.From.Type, config.From.Name, config.To.Type, config.Confidence));
        Assert.Equal("xpath:/Executable/Executables/Executable[1]", config.Locator);
    }

    [Fact]
    public void AVariableReferenceIsNotAConfigurationKey() =>
        Assert.DoesNotContain(Build(Package).Facts!.Links, l => l.From.Name.Contains("::"));

    [Fact]
    public void AConnectionWhoseStringComesFromAVariable_TakesTheVariablesValue()
    {
        var xml = Package.Replace(
            "<DTS:ConnectionManager DTS:ConnectionString=\"D:\\Import\\CashVault\\cashvault_current.csv\" />",
            "<DTS:PropertyExpression DTS:Name=\"ConnectionString\">@[User::VaultFilePath]</DTS:PropertyExpression>");
        var reads = Assert.Single(LinksOf(Build(xml), "reads"));
        Assert.Equal("D:\\Import\\CashVault\\cashvault_current.csv", reads.To.Name);
    }

    [Fact]
    public void EveryFactPointsAtAChunkThatExists()
    {
        var r = Build(Package);
        var chunks = r.Chunks.Select(c => c.Locator).ToHashSet();
        Assert.All(r.Facts!.Entities, e => Assert.Contains(e.Locator!, chunks));
        Assert.All(r.Facts.Links, l => Assert.Contains(l.Locator!, chunks));
    }

    [Fact]
    public void TheSummary_SaysWhatWasRead() =>
        Assert.Equal("Read 1 package with 2 connections and 3 tasks.", Build(Package).Summary);

    [Fact]
    public void APackageWithNoObjectName_IsNamedByItsFile()
    {
        var r = Build("<DTS:Executable xmlns:DTS=\"www.microsoft.com/SqlServer/Dts\"/>\n", "batch/ssis/Nightly.dtsx");
        Assert.Equal("Nightly", Assert.Single(r.Facts!.Entities).Name);
    }

    [Fact]
    public void AnXmlFileThatIsNotAPackage_IsChunkedAndSaysSo()
    {
        var r = Build("<Settings><Item/></Settings>\n");
        Assert.Contains("does not look like an Integration Services package", r.Warnings.Single());
        Assert.True(r.Facts!.IsEmpty);
    }

    [Fact]
    public void BrokenXml_FallsBackToLines() => Assert.StartsWith("L", Build("<a><b></a>\n").Chunks[0].Locator);

    [Fact]
    public void TheRegistryUsesItForPackages() => Assert.IsType<SsisAdapter>(EvidenceAdapters.AdapterFor("etl", ".dtsx"));
}
