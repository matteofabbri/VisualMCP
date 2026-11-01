using Snitch.Languages.Sql;
using Xunit;

namespace VisualMCP.Tests;

/// <summary>
/// analyze_sql: one rule, one case that fires and one that must not. The scanner masks comments
/// and literals first, which is what keeps a keyword inside them from firing a rule.
/// </summary>
public sealed class SqlAnalyzerTests
{
    private static SqlAnalysis Analyze(string sql) => SqlAnalyzer.AnalyzeText(sql, "probe.sql");

    private static string[] Rules(SqlAnalysis analysis) =>
        analysis.Findings.Select(finding => finding.RuleId).Distinct().OrderBy(id => id).ToArray();

    [Fact]
    public void ACleanScriptHasNoFindingsAndItsStatementsAreRead()
    {
        var analysis = Analyze("""
            SELECT id, name FROM dbo.Customers WHERE id = @id;
            INSERT INTO dbo.Orders (id, total) VALUES (1, 10.5);
            UPDATE dbo.Orders SET total = 20 WHERE id = 1;
            """);

        Assert.Empty(analysis.Findings);
        Assert.Equal(3, analysis.Statements);
        Assert.Contains("SELECT", analysis.Verbs);
        Assert.Contains("dbo.Customers", analysis.Tables);
        Assert.Contains("dbo.Orders", analysis.Tables);
    }

    [Fact]
    public void UpdateAndDeleteWithoutWhereAreFlagged()
    {
        Assert.Contains("SQL0004", Rules(Analyze("UPDATE dbo.Orders SET total = 0")));
        Assert.Contains("SQL0004", Rules(Analyze("DELETE FROM dbo.Orders")));
        Assert.DoesNotContain("SQL0004", Rules(Analyze("DELETE FROM dbo.Orders WHERE id = 1")));
    }

    [Fact]
    public void SelectStarIsFlaggedButAColumnListIsNot()
    {
        Assert.Contains("SQL0003", Rules(Analyze("SELECT * FROM dbo.Users")));
        Assert.DoesNotContain("SQL0003", Rules(Analyze("SELECT id, name FROM dbo.Users")));
    }

    [Fact]
    public void DropWithoutIfExistsIsFlagged()
    {
        Assert.Contains("SQL0005", Rules(Analyze("DROP TABLE dbo.Temp")));
        Assert.DoesNotContain("SQL0005", Rules(Analyze("DROP TABLE IF EXISTS dbo.Temp")));
    }

    [Fact]
    public void AKeywordInsideACommentOrAStringDoesNotFire()
    {
        var commented = Analyze("-- DELETE FROM dbo.Orders\nSELECT id FROM dbo.Orders");

        Assert.DoesNotContain("SQL0004", Rules(commented));
        Assert.DoesNotContain("SQL0003", Rules(Analyze("SELECT name FROM dbo.Users WHERE note = 'SELECT * FROM x'")));
    }

    [Fact]
    public void UnterminatedStringsAndCommentsAreLexicalErrors()
    {
        Assert.Contains("SQL0001", Rules(Analyze("SELECT 'unterminated FROM dbo.Users")));
        Assert.Contains("SQL0001", Rules(Analyze("SELECT 1 /* never closed")));
        Assert.Contains("SQL0001", Rules(Analyze("SELECT [unclosed FROM dbo.Users")));
    }

    [Fact]
    public void UnbalancedParenthesesAreFlagged()
    {
        Assert.Contains("SQL0002", Rules(Analyze("SELECT id FROM dbo.Users WHERE id IN (1, 2")));
        Assert.Contains("SQL0002", Rules(Analyze("SELECT id FROM dbo.Users WHERE id = 1)")));
        Assert.DoesNotContain("SQL0002", Rules(Analyze("SELECT id FROM dbo.Users WHERE id IN (1, 2)")));
    }

    [Fact]
    public void InsertColumnValueMismatchIsFlagged()
    {
        Assert.Contains("SQL0010", Rules(Analyze("INSERT INTO dbo.T (a, b, c) VALUES (1, 2)")));
        Assert.DoesNotContain("SQL0010", Rules(Analyze("INSERT INTO dbo.T (a, b) VALUES (1, 2)")));
    }

    [Fact]
    public void AnOpenTransactionIsFlagged()
    {
        Assert.Contains("SQL0007", Rules(Analyze("""
            BEGIN TRANSACTION;
            UPDATE dbo.Orders SET total = 1 WHERE id = 1;
            """)));

        Assert.DoesNotContain("SQL0007", Rules(Analyze("""
            BEGIN TRAN;
            UPDATE dbo.Orders SET total = 1 WHERE id = 1;
            COMMIT;
            """)));
    }

    [Fact]
    public void LeadingWildcardLikeIsFlaggedButTrailingIsNot()
    {
        Assert.Contains("SQL0008", Rules(Analyze("SELECT id FROM dbo.Users WHERE name LIKE '%son'")));
        Assert.DoesNotContain("SQL0008", Rules(Analyze("SELECT id FROM dbo.Users WHERE name LIKE 'John%'")));
    }

    [Fact]
    public void AlterAddNotNullWithoutDefaultIsFlagged()
    {
        Assert.Contains("SQL0009", Rules(Analyze("ALTER TABLE dbo.Users ADD email NVARCHAR(200) NOT NULL")));
        Assert.DoesNotContain("SQL0009", Rules(
            Analyze("ALTER TABLE dbo.Users ADD email NVARCHAR(200) NOT NULL DEFAULT ''")));
    }

    [Fact]
    public void DynamicSqlByConcatenationIsFlagged()
    {
        Assert.Contains("SQL0006", Rules(
            Analyze("EXEC ('SELECT id FROM dbo.Users WHERE name = ''' + @name + '''')")));
    }

    [Fact]
    public void BracketedAndQuotedTableNamesAreExtracted()
    {
        var analysis = Analyze("SELECT id FROM [dbo].[Users] JOIN \"public\".\"Orders\" o ON o.user_id = id");

        Assert.Contains("[dbo].[Users]", analysis.Tables);
        Assert.Contains("\"public\".\"Orders\"", analysis.Tables);
    }

    [Fact]
    public void FindingsCarryTheirPositionAndTheSourceLine()
    {
        var analysis = Analyze("""
            -- header
            SELECT *
            FROM dbo.Users;
            """);

        var finding = Assert.Single(analysis.Findings);
        Assert.Equal("SQL0003", finding.RuleId);
        Assert.Equal(2, finding.Line);
        Assert.Contains("SELECT *", finding.Snippet);
        Assert.Equal("probe.sql", finding.File);
    }

    [Fact]
    public async Task TheServiceScansTheTreeSkipsBuildOutputAndFiltersByRule()
    {
        using var workspace = FakeWorkspace.Create(("Probe.cs", "class C { }"));
        var service = new SqlService(workspace);

        var root = workspace.RootDirectory;
        Directory.CreateDirectory(Path.Combine(root, "db"));
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        await File.WriteAllTextAsync(Path.Combine(root, "db", "schema.sql"), "CREATE TABLE dbo.T (id INT);");
        await File.WriteAllTextAsync(Path.Combine(root, "db", "seed.sql"), "DELETE FROM dbo.T");
        await File.WriteAllTextAsync(Path.Combine(root, "bin", "ignored.sql"), "DELETE FROM dbo.Ignored");

        var inventory = ResultAssert.Json(await service.InventoryAsync(root, 100));
        Assert.Equal(2, inventory.GetProperty("fileCount").GetInt32());

        var analysis = ResultAssert.Json(await service.AnalyzeAsync(root, "info", "SQL0004", 100, 100));
        Assert.Equal(1, analysis.GetProperty("findingCount").GetInt32());
        Assert.Equal(2, analysis.GetProperty("filesAnalyzed").GetInt32());

        var unknown = ResultAssert.Error(await service.AnalyzeAsync(root, "info", "SQL9999", 100, 100));
        Assert.Contains("Unknown rule", unknown);
    }

    [Fact]
    public void TheSchemaModelReadsCreateTableAndAlterAdd()
    {
        var schema = new SqlSchema();

        SqlAnalyzer.ExtractSchemaInto(schema, """
            CREATE TABLE [dbo].[Users] (
                [Id] INT NOT NULL PRIMARY KEY,
                [Name] NVARCHAR(200) NOT NULL,
                CONSTRAINT [FK_Users_Orders] FOREIGN KEY ([Id]) REFERENCES [dbo].[Orders] ([UserId]),
                [Email] NVARCHAR(200) NULL
            );

            ALTER TABLE dbo.Users ADD [CreatedAt] DATETIME2 NOT NULL DEFAULT GETUTCDATE();
            """);

        var table = Assert.Single(schema.Tables);
        Assert.Equal("Users", table.Name);
        Assert.Equal("dbo", table.Schema);
        Assert.Equal(["Id", "Name", "Email", "CreatedAt"], table.Columns);
        Assert.Equal(1, schema.TableCount);
        Assert.Equal(4, schema.ColumnCount);
    }

    [Fact]
    public void ASchemaInTheScanFlagsAnUndeclaredTableButNotADeclaredOne()
    {
        var schema = new SqlSchema();
        SqlAnalyzer.ExtractSchemaInto(schema, "CREATE TABLE dbo.Users (Id INT, Name NVARCHAR(100));");

        var unknown = SqlAnalyzer.AnalyzeText("SELECT Id FROM dbo.Missing", "probe.sql", schema);
        Assert.Contains("SQL0011", Rules(unknown));

        var known = SqlAnalyzer.AnalyzeText("SELECT Id, Name FROM dbo.Users", "probe.sql", schema);
        Assert.DoesNotContain("SQL0011", Rules(known));
        Assert.DoesNotContain("SQL0012", Rules(known));
    }

    [Fact]
    public void AColumnTheDeclaredTableDoesNotHaveIsFlaggedInSelectInsertAndUpdate()
    {
        var schema = new SqlSchema();
        SqlAnalyzer.ExtractSchemaInto(schema, "CREATE TABLE dbo.Users (Id INT, Name NVARCHAR(100));");

        Assert.Contains("SQL0012", Rules(
            SqlAnalyzer.AnalyzeText("SELECT Id, Nickname FROM dbo.Users", "probe.sql", schema)));
        Assert.Contains("SQL0012", Rules(
            SqlAnalyzer.AnalyzeText("INSERT INTO dbo.Users (Id, Nickname) VALUES (1, 'a')", "probe.sql", schema)));
        Assert.Contains("SQL0012", Rules(
            SqlAnalyzer.AnalyzeText("UPDATE dbo.Users SET Nickname = 'a' WHERE Id = 1", "probe.sql", schema)));

        Assert.DoesNotContain("SQL0012", Rules(
            SqlAnalyzer.AnalyzeText("SELECT u.Id, u.Name FROM dbo.Users u", "probe.sql", schema)));
    }

    [Fact]
    public void AJoinOrASubqueryStopsTheColumnCheckInsteadOfGuessing()
    {
        var schema = new SqlSchema();
        SqlAnalyzer.ExtractSchemaInto(schema,
            "CREATE TABLE dbo.Users (Id INT, Name NVARCHAR(100)); CREATE TABLE dbo.Orders (Id INT, UserId INT);");

        Assert.DoesNotContain("SQL0012", Rules(SqlAnalyzer.AnalyzeText(
            "SELECT Users.Id, Orders.UserId FROM dbo.Users JOIN dbo.Orders ON Orders.UserId = Users.Id",
            "probe.sql", schema)));

        Assert.DoesNotContain("SQL0012", Rules(SqlAnalyzer.AnalyzeText(
            "SELECT Id FROM dbo.Users WHERE Id IN (SELECT UserId FROM dbo.Orders)", "probe.sql", schema)));
    }

    [Fact]
    public void ACteAndAnExternalTableAreNotUndeclared()
    {
        var schema = new SqlSchema();
        SqlAnalyzer.ExtractSchemaInto(schema, "CREATE TABLE dbo.Users (Id INT);");

        var cte = SqlAnalyzer.AnalyzeText(
            "WITH recent AS (SELECT Id FROM dbo.Users) SELECT Id FROM recent", "probe.sql", schema);
        Assert.DoesNotContain("SQL0011", Rules(cte));

        var external = SqlAnalyzer.AnalyzeText("SELECT name FROM sys.objects", "probe.sql", schema);
        Assert.DoesNotContain("SQL0011", Rules(external));
    }
}
