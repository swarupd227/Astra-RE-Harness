using System.Text.Json;
using Astra.Api.Evidence;
using Xunit;

namespace Astra.Api.Tests;

public class SqlAdapterTests
{
    private static AdapterResult Build(string sql, string path = "db/ddl.sql") => AdapterTestKit.Build(path, "sql", sql);

    private static EntityFact Entity(AdapterResult r, string type, string name) =>
        Assert.Single(r.Facts!.Entities, e => e.Type == type && e.Name == name);

    private static IEnumerable<string> LinkTargets(AdapterResult r, string relation, string fromName) =>
        r.Facts!.Links.Where(l => l.Relation == relation && l.From.Name == fromName).Select(l => l.To.Name).OrderBy(n => n, StringComparer.Ordinal);

    private static JsonElement Attr(EntityFact e, string key) => JsonSerializer.SerializeToElement(e.Attrs![key]);

    private static IEnumerable<string> Strings(JsonElement array) => array.EnumerateArray().Select(x => x.GetString()!);

    // Lines are numbered in the comments; the tests below depend on them.
    private const string Ddl = """
        -- Staged transactions waiting to be posted.
        CREATE TABLE dbo.TLR_STAGE (
            STAGE_ID INT IDENTITY(1,1) NOT NULL,
            TXN_REF VARCHAR(40) NOT NULL,
            AMOUNT DECIMAL(12,2) NOT NULL,
            CONSTRAINT PK_TLR_STAGE PRIMARY KEY (STAGE_ID),
            CONSTRAINT UQ_TLR_STAGE_TXN_REF UNIQUE (TXN_REF)
        );
        GO
        CREATE VIEW dbo.vw_DailyPosted AS
        SELECT p.TXN_REF FROM dbo.TLR_POSTED p;
        GO
        """;

    [Fact]
    public void ATableIsOneChunk_WithItsLeadingComment_AndItsKeysInTheStructuredPart()
    {
        var r = Build(Ddl);
        var table = r.Chunks[0];
        Assert.Equal("L1-8", table.Locator);
        Assert.Equal("TABLE TLR_STAGE", table.Label);
        Assert.Equal((1, 8), (table.LineStart, table.LineEnd));
        Assert.StartsWith("-- Staged transactions", table.Text);
        Assert.Contains("UQ_TLR_STAGE_TXN_REF", table.Text);

        var s = AdapterTestKit.Structured(table);
        Assert.Equal("table", s.GetProperty("object").GetString());
        Assert.Equal("scriptdom", s.GetProperty("parsedBy").GetString());
        Assert.Equal(new[] { "STAGE_ID", "TXN_REF", "AMOUNT" }, s.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Equal("VARCHAR(40)", s.GetProperty("columns")[1].GetProperty("type").GetString());
        Assert.True(s.GetProperty("columns")[0].GetProperty("primaryKey").GetBoolean());
        Assert.False(s.GetProperty("columns")[1].GetProperty("nullable").GetBoolean());
        var keys = s.GetProperty("keys").EnumerateArray().Select(k => (k.GetProperty("name").GetString(), k.GetProperty("kind").GetString())).ToList();
        Assert.Equal(new[] { ("PK_TLR_STAGE", "primary"), ("UQ_TLR_STAGE_TXN_REF", "unique") }, keys);
    }

    [Fact]
    public void AnObjectAfterGo_KeepsItsRealLineNumbers()
    {
        var r = Build(Ddl);
        var view = r.Chunks[1];
        Assert.Equal("L10-11", view.Locator);
        Assert.Equal("VIEW vw_DailyPosted", view.Label);
        Assert.Equal((10, 11), (view.LineStart, view.LineEnd));
    }

    [Fact]
    public void GoWithACountOrInLowerCase_IsASeparator()
    {
        var r = Build("CREATE TABLE A (ID INT)\ngo\nCREATE TABLE B (ID INT)\nGO 2\nCREATE TABLE C (ID INT)\n");
        Assert.Equal(new[] { "L1-1", "L3-3", "L5-5" }, r.Chunks.Select(c => c.Locator));
    }

    [Fact]
    public void ACommentSeparatedFromAStatementByABlankLine_IsNotPartOfIt()
    {
        var r = Build("-- about something else\n\nCREATE TABLE A (ID INT);\n");
        Assert.Equal("L3-3", Assert.Single(r.Chunks).Locator);
    }

    [Fact]
    public void FactsPointAtTheChunksThatShowThem()
    {
        var r = Build(Ddl);
        var chunks = r.Chunks.Select(c => c.Locator).ToHashSet();
        Assert.All(r.Facts!.Entities, e => Assert.Contains(e.Locator!, chunks));
        Assert.All(r.Facts.Links, l => Assert.Contains(l.Locator!, chunks));
        Assert.Equal("L1-8", Entity(r, "table", "TLR_STAGE").Locator);
        Assert.Equal("L10-11", Entity(r, "view", "vw_DailyPosted").Locator);
    }

    [Fact]
    public void TheTableEntity_CarriesItsColumnsAndKeys()
    {
        var table = Entity(Build(Ddl), "table", "TLR_STAGE");
        Assert.Equal(3, Attr(table, "columns").GetArrayLength());
        Assert.Equal(new[] { "STAGE_ID" }, Strings(Attr(table, "primaryKey")));
        var unique = Attr(table, "uniqueKeys")[0];
        Assert.Equal("UQ_TLR_STAGE_TXN_REF", unique.GetProperty("name").GetString());
        Assert.Equal(new[] { "TXN_REF" }, Strings(unique.GetProperty("columns")));
    }

    [Fact]
    public void AViewReadsTheTablesItSelectsFrom()
    {
        var r = Build(Ddl);
        Assert.Equal(new[] { "TLR_POSTED" }, LinkTargets(r, "reads", "vw_DailyPosted"));
        Assert.All(r.Facts!.Links, l => Assert.Equal("high", l.Confidence));
    }

    private const string Procs = """
        CREATE PROCEDURE dbo.usp_PostStage
            @Mode VARCHAR(20) = 'ALL'
        AS
        BEGIN
            SET NOCOUNT ON;
            WITH due AS (SELECT TXN_REF, AMOUNT FROM TLR_STAGE WHERE AMOUNT > 0)
            INSERT INTO TLR_POSTED (TXN_REF) SELECT d.TXN_REF FROM due d JOIN TLR_TXN t ON t.TXN_REF = d.TXN_REF;
            UPDATE s SET s.AMOUNT = 0 FROM TLR_STAGE s WHERE s.TXN_REF IN (SELECT TXN_REF FROM TLR_POSTED);
            DELETE FROM #scratch;
            SELECT * INTO #copy FROM TLR_LIMIT;
            EXEC dbo.usp_LogOfac @Id = 1;
            EXEC sp_executesql N'select 1';
        END
        GO
        """;

    [Fact]
    public void AProcedure_ReadsAndWritesWhatItsBodySays()
    {
        var r = Build(Procs, "db/procs/usp_PostStage.sql");
        Assert.Equal(new[] { "TLR_LIMIT", "TLR_POSTED", "TLR_STAGE", "TLR_TXN" }, LinkTargets(r, "reads", "usp_PostStage"));
        Assert.Equal(new[] { "TLR_POSTED", "TLR_STAGE" }, LinkTargets(r, "writes", "usp_PostStage"));
        Assert.Equal(new[] { "usp_LogOfac" }, LinkTargets(r, "calls", "usp_PostStage"));
    }

    [Fact]
    public void ACteNameATempTableAndASystemProcedure_AreNotPartOfTheApplication()
    {
        var r = Build(Procs);
        var all = r.Facts!.Links.Select(l => l.To.Name).ToList();
        Assert.DoesNotContain("due", all);
        Assert.DoesNotContain("#scratch", all);
        Assert.DoesNotContain("#copy", all);
        Assert.DoesNotContain("sp_executesql", all);
    }

    [Fact]
    public void TheWriteTargetOfUpdateWithAnAlias_IsTheTableBehindIt() =>
        Assert.Contains("TLR_STAGE", LinkTargets(Build(Procs), "writes", "usp_PostStage"));

    [Fact]
    public void AProcedureEntity_SaysItHasNoTransactionNoCursorNoLoop()
    {
        var p = Entity(Build(Procs), "procedure", "usp_PostStage");
        Assert.False(Attr(p, "usesTransaction").GetBoolean());
        Assert.False(Attr(p, "hasCursor").GetBoolean());
        Assert.False(Attr(p, "hasLoop").GetBoolean());
        Assert.Equal("@Mode", Attr(p, "parameters")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void ATransactionACursorALoopAndAThrow_AreRecorded()
    {
        var r = Build("""
            CREATE PROCEDURE usp_RebuildDrawerTotals AS
            BEGIN
                BEGIN TRY
                    BEGIN TRANSACTION;
                    DECLARE @id INT;
                    DECLARE c CURSOR FOR SELECT DRAWER_ID FROM TLR_DRAWER;
                    OPEN c;
                    FETCH NEXT FROM c INTO @id;
                    WHILE @@FETCH_STATUS = 0
                    BEGIN
                        UPDATE TLR_DRAWER SET TOTAL = 0 WHERE DRAWER_ID = @id;
                        FETCH NEXT FROM c INTO @id;
                    END
                    CLOSE c; DEALLOCATE c;
                    COMMIT;
                END TRY
                BEGIN CATCH
                    ROLLBACK;
                    THROW 50001, 'rebuild failed', 1;
                END CATCH
            END
            """);
        var p = Entity(r, "procedure", "usp_RebuildDrawerTotals");
        Assert.True(Attr(p, "usesTransaction").GetBoolean());
        Assert.True(Attr(p, "hasCursor").GetBoolean());
        Assert.True(Attr(p, "hasLoop").GetBoolean());
        Assert.True(Attr(p, "hasTryCatch").GetBoolean());
        Assert.Equal(new[] { "50001" }, Strings(Attr(p, "raises")));
        Assert.Equal(new[] { "TLR_DRAWER" }, LinkTargets(r, "writes", "usp_RebuildDrawerTotals"));
    }

    [Fact]
    public void AProcedureChunk_StatesTheSameFactsInItsStructuredPart()
    {
        var s = AdapterTestKit.Structured(Build(Procs).Chunks[0]);
        Assert.Equal("procedure", s.GetProperty("object").GetString());
        Assert.Equal(new[] { "usp_LogOfac" }, Strings(s.GetProperty("calls")));
        Assert.Equal(new[] { "TLR_POSTED", "TLR_STAGE" }, Strings(s.GetProperty("writes")).OrderBy(x => x, StringComparer.Ordinal));
        Assert.False(s.GetProperty("usesTransaction").GetBoolean());
    }

    [Fact]
    public void AnAlterTableThatAddsAKey_AttachesItToTheTable_AndIsItsOwnChunk()
    {
        var r = Build("""
            CREATE TABLE TLR_STAGE (STAGE_ID INT NOT NULL, TXN_REF VARCHAR(40) NOT NULL);
            GO
            ALTER TABLE TLR_STAGE ADD CONSTRAINT UQ_TLR_STAGE_TXN_REF UNIQUE (TXN_REF);
            GO
            """);
        Assert.Equal(new[] { "TABLE TLR_STAGE", "ALTER TABLE TLR_STAGE · UQ_TLR_STAGE_TXN_REF" }, r.Chunks.Select(c => c.Label));
        var forTable = r.Facts!.Entities.Where(e => e.Type == "table" && e.Name == "TLR_STAGE").ToList();
        Assert.Equal(new[] { "L1-1", "L3-3" }, forTable.Select(e => e.Locator));
        Assert.Equal("UQ_TLR_STAGE_TXN_REF", Attr(forTable[1], "uniqueKeys")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void AUniqueIndex_IsAKeyOfItsTable()
    {
        var r = Build("CREATE TABLE T (A INT, B INT);\nGO\nCREATE UNIQUE INDEX UX_T_A ON T (A DESC);\n");
        Assert.Equal("INDEX UX_T_A on T", r.Chunks[1].Label);
        var onTable = r.Facts!.Entities.Last(e => e.Name == "T");
        Assert.Equal(new[] { "A" }, Strings(Attr(onTable, "uniqueKeys")[0].GetProperty("columns")));
    }

    [Fact]
    public void AForeignKey_IsALinkBetweenTables()
    {
        var r = Build("CREATE TABLE TLR_POSTED (ID INT NOT NULL, TXN_REF VARCHAR(40), CONSTRAINT FK_P FOREIGN KEY (TXN_REF) REFERENCES TLR_STAGE (TXN_REF));");
        var link = Assert.Single(r.Facts!.Links);
        Assert.Equal(("references", "TLR_POSTED", "TLR_STAGE"), (link.Relation, link.From.Name, link.To.Name));
        Assert.Equal("FK_P", JsonSerializer.SerializeToElement(link.Attrs!["constraint"]).GetString());
    }

    [Fact]
    public void AnInlineForeignKeyAndAnInlinePrimaryKey_AreRead()
    {
        var r = Build("CREATE TABLE C (ID INT PRIMARY KEY, P INT REFERENCES PARENT (ID));");
        var table = Entity(r, "table", "C");
        Assert.Equal(new[] { "ID" }, Strings(Attr(table, "primaryKey")));
        Assert.Equal("PARENT", Assert.Single(r.Facts!.Links).To.Name);
    }

    [Fact]
    public void Brackets_AndTheDboSchema_DoNotReachTheName_ButAnotherSchemaDoes()
    {
        var r = Build("CREATE TABLE [dbo].[TLR_A] (ID INT);\nGO\nCREATE TABLE sales.Orders (ID INT);\nGO\nCREATE PROCEDURE [dbo].[usp_X] AS SELECT * FROM [sales].[Orders], [dbo].[TLR_A];");
        Assert.Contains(r.Facts!.Entities, e => e.Type == "table" && e.Name == "TLR_A");
        Assert.Contains(r.Facts.Entities, e => e.Type == "table" && e.Name == "sales.Orders");
        Assert.Equal(new[] { "TLR_A", "sales.Orders" }, LinkTargets(r, "reads", "usp_X"));
    }

    [Fact]
    public void StatementsThatDefineNothing_AreGroupedIntoWindowsOfLines()
    {
        var inserts = string.Join('\n', Enumerable.Range(1, 90).Select(i => $"INSERT INTO TLR_BRANCH (ID) VALUES ({i});"));
        var r = Build("CREATE TABLE TLR_BRANCH (ID INT);\nGO\n" + inserts + "\n");
        Assert.Equal(new[] { "L1-1", "L3-42", "L43-82", "L83-92" }, r.Chunks.Select(c => c.Locator));
        Assert.Equal("ddl.sql · lines 3–42", r.Chunks[1].Label);
        Assert.Single(r.Facts!.Entities);
    }

    [Fact]
    public void AFunctionIsAnEntity_AndATriggerIsAChunkWithNoEntity()
    {
        var r = Build("CREATE FUNCTION dbo.fn_Limit (@id INT) RETURNS INT AS BEGIN RETURN 1 END;\nGO\nCREATE TRIGGER trg_t ON TLR_AUDIT AFTER INSERT AS SELECT 1;\n");
        Assert.Equal(new[] { "FUNCTION fn_Limit", "TRIGGER trg_t" }, r.Chunks.Select(c => c.Label));
        Assert.Equal("fn_Limit", Entity(r, "function", "fn_Limit").Name);
        Assert.DoesNotContain(r.Facts!.Entities, e => e.Name == "trg_t");
    }

    [Fact]
    public void AlterAndCreateOrAlterProcedure_AreProcedures()
    {
        var r = Build("ALTER PROCEDURE usp_A AS SELECT 1;\nGO\nCREATE OR ALTER PROCEDURE usp_B AS SELECT 1;\n");
        Assert.Equal(new[] { "PROCEDURE usp_A", "PROCEDURE usp_B" }, r.Chunks.Select(c => c.Label));
    }

    [Fact]
    public void TheSummary_SaysWhatWasFound()
    {
        var r = Build(Ddl + "\n" + Procs);
        Assert.Equal("Read 1 table, 1 view and 1 stored procedure.", r.Summary);
    }

    // ── what ScriptDom cannot read ──────────────────────────────────────

    private const string OneBadStatement = """
        CREATE TABLE T1 (ID INT);

        CREATE PROCEDURE usp_Broken AS
        BEGIN
            SELEKT * FROM T1 WHERE;
        END

        CREATE PROCEDURE usp_Fine AS SELECT ID FROM T1;
        """;

    [Fact]
    public void OneStatementThatDoesNotParse_CostsOnlyItself()
    {
        var r = Build(OneBadStatement);
        Assert.Equal(new[] { "TABLE T1", "PROCEDURE usp_Broken", "PROCEDURE usp_Fine" }, r.Chunks.Select(c => c.Label));
        Assert.Equal("scriptdom", AdapterTestKit.Structured(r.Chunks[0]).GetProperty("parsedBy").GetString());
        Assert.Equal("pattern", AdapterTestKit.Structured(r.Chunks[1]).GetProperty("parsedBy").GetString());
        Assert.Equal("scriptdom", AdapterTestKit.Structured(r.Chunks[2]).GetProperty("parsedBy").GetString());
    }

    [Fact]
    public void WhatWasReadByPattern_IsMediumConfidence_AndTheWarningNamesTheLines()
    {
        var r = Build(OneBadStatement);
        var broken = r.Facts!.Links.Single(l => l.From.Name == "usp_Broken");
        Assert.Equal(("reads", "T1", "medium"), (broken.Relation, broken.To.Name, broken.Confidence));
        Assert.Equal("high", r.Facts.Links.Single(l => l.From.Name == "usp_Fine").Confidence);
        var warning = Assert.Single(r.Warnings);
        Assert.Contains("Lines 3–6", warning);
        Assert.Contains("medium confidence", warning);
    }

    [Fact]
    public void AnObjectReadByPattern_IsMarkedAsSoInItsEntity()
    {
        var broken = Entity(Build(OneBadStatement), "procedure", "usp_Broken");
        Assert.Equal("pattern", Attr(broken, "parsedBy").GetString());
    }

    [Fact]
    public void ThePatternReader_FindsKeysCallsAndWrites()
    {
        var r = Build("""
            CREATE TABLE T (ID INT, REF VARCHAR(10), CONSTRAINT UQ_T_REF UNIQUE (REF)))
            GO
            CREATE PROCEDURE usp_P AS
            BEGIN
                -- FROM ignored_in_comment
                INSERT INTO TLR_OUT SELECT * FROM TLR_IN i JOIN TLR_OTHER o ON o.ID = i.ID WHERE x = 'FROM not_a_table';
                DELETE FROM TLR_OLD;
                EXEC usp_Next;
                EXEC sp_who;
            END )
            """);
        var table = Entity(r, "table", "T");
        Assert.Equal("UQ_T_REF", Attr(table, "uniqueKeys")[0].GetProperty("name").GetString());
        Assert.Equal(new[] { "TLR_IN", "TLR_OTHER" }, LinkTargets(r, "reads", "usp_P"));
        Assert.Equal(new[] { "TLR_OLD", "TLR_OUT" }, LinkTargets(r, "writes", "usp_P"));
        Assert.Equal(new[] { "usp_Next" }, LinkTargets(r, "calls", "usp_P"));
    }

    [Fact]
    public void AnotherDialect_IsStillCutIntoItsObjects()
    {
        var r = Build("""
            CREATE TABLE accounts (id serial PRIMARY KEY, name text);

            CREATE VIEW v_accounts AS SELECT id FROM accounts WHERE name LIKE 'a%';
            """);
        Assert.Equal(new[] { "TABLE accounts", "VIEW v_accounts" }, r.Chunks.Select(c => c.Label));
        Assert.Equal(new[] { "accounts" }, LinkTargets(r, "reads", "v_accounts"));
    }

    [Fact]
    public void WhenTheTimeAllowedRunsOut_TheRestIsReadByPattern_AndTheFileSaysSo()
    {
        var r = EvidenceAdapters.Build(new AdapterInput("db/ddl.sql", "sql", Ddl, null));
        Assert.Empty(r.Warnings);

        var slow = new SqlAdapter(TimeSpan.Zero, SqlAdapter.MaxBatchChars).Build(new AdapterInput("db/ddl.sql", "sql", Ddl, null));
        Assert.Equal(new[] { "TABLE TLR_STAGE", "VIEW vw_DailyPosted" }, slow.Chunks.Select(c => c.Label));
        Assert.All(slow.Facts!.Links, l => Assert.Equal("medium", l.Confidence));
        Assert.Contains("time allowed", slow.Warnings[0]);
    }

    [Fact]
    public void ATooLargeBatch_IsReadByPattern_AndTheFileSaysSo()
    {
        var big = new SqlAdapter(SqlAdapter.DefaultBudget, 40).Build(new AdapterInput("db/ddl.sql", "sql", Ddl, null));
        Assert.Contains("longer than 40", big.Warnings[0]);
        Assert.Contains(big.Chunks, c => c.Label == "TABLE TLR_STAGE");
        Assert.Contains("UQ_TLR_STAGE_TXN_REF", Attr(Entity(big, "table", "TLR_STAGE"), "uniqueKeys").GetRawText());
    }

    [Fact]
    public void TextThatIsNotSqlAtAll_DoesNotFail_ItIsCutIntoLines()
    {
        var r = Build("this is not sql\nat all\n");
        Assert.Equal("L1-2", Assert.Single(r.Chunks).Locator);
        Assert.Empty(r.Facts!.Entities);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n")]
    [InlineData("-- only a comment\n")]
    public void NothingToRead_IsNoChunksAndNoFacts(string sql)
    {
        var r = Build(sql);
        Assert.Empty(r.Chunks);
        Assert.True(r.Facts is null || r.Facts.IsEmpty);
    }

    [Fact]
    public void ABom_IsIgnored() => Assert.Equal("TABLE A", Build("﻿CREATE TABLE A (ID INT);").Chunks.Single().Label);

    [Fact]
    public void TheRegistry_UsesItForSqlFiles_AndMarksTheKindAsProducingFacts()
    {
        Assert.IsType<SqlAdapter>(EvidenceAdapters.AdapterFor("sql", ".sql"));
        Assert.IsType<SqlAdapter>(EvidenceAdapters.AdapterFor("sql", ".ddl"));
        Assert.True(EvidenceAdapters.ProducesFacts("sql"));
        Assert.False(EvidenceAdapters.ProducesFacts("ticket"));
    }

    [Fact]
    public void FactsFollowAChunkWhoseLocatorWasBounded()
    {
        var long1 = "xpath:/" + string.Concat(Enumerable.Repeat("Level/", 120)) + "A";
        var facts = new FactSet(new[] { new EntityFact("job", "X", long1) }, Array.Empty<LinkFact>());
        var r = EvidenceAdapters.Finish(
            new AdapterResult(new[] { new EvidenceChunkDraft(long1, "x", "text") }, Array.Empty<string>(), facts), 10);
        Assert.Equal(r.Chunks[0].Locator, r.Facts!.Entities[0].Locator);
        Assert.NotEqual(long1, r.Facts.Entities[0].Locator);
    }
}
