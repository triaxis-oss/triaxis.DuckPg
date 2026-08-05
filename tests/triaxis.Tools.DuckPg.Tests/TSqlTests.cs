using triaxis.Tools.DuckPg.TSql;

namespace triaxis.Tools.DuckPg.Tests;

/// The dialect is translated on the parse tree, so these check the tree's rendering rather than
/// any particular text transformation — the point of having a parser is that both sides are SQL.
public class TSqlTests
{
    static string Translate(string sql, params string[] parameters)
    {
        var context = new TSqlContext(
            "lake",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["version"] = "'Microsoft SQL Server 2022 (duckpg)'",
                ["rowcount"] = "0",
                ["trancount"] = "0",
            },
            new HashSet<string>(parameters, StringComparer.OrdinalIgnoreCase),
            "sa");

        return string.Join("; ", TSqlTranslator.Translate(sql, context));
    }

    [Theory]
    // Bracketed names and the schema a client believes in.
    [InlineData("SELECT [order id] FROM [dbo].[orders]", """SELECT "order id" FROM "lake"."orders" """)]
    [InlineData("SELECT * FROM orders", """SELECT * FROM "lake"."orders" """)]
    [InlineData("SELECT * FROM app.dbo.orders", """SELECT * FROM "lake"."orders" """)]
    [InlineData("SELECT * FROM seed.orders", """SELECT * FROM "seed"."orders" """)]
    // TOP is a LIMIT in the wrong place.
    [InlineData("SELECT TOP 5 * FROM orders", """SELECT * FROM "lake"."orders" LIMIT 5""")]
    [InlineData("SELECT TOP (5) * FROM orders ORDER BY id",
        """SELECT * FROM "lake"."orders" ORDER BY "id" LIMIT 5""")]
    [InlineData("SELECT * FROM orders ORDER BY id OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY",
        """SELECT * FROM "lake"."orders" ORDER BY "id" LIMIT 5 OFFSET 10""")]
    // A Unicode literal is just a literal.
    [InlineData("SELECT N'hello' AS greeting", """SELECT 'hello' AS "greeting" """)]
    [InlineData("SELECT 'it''s' AS x", """SELECT 'it''s' AS "x" """)]
    [InlineData("SELECT 0xDEAD AS b", """SELECT from_hex('DEAD') AS "b" """)]
    public void Renders(string tsql, string expected) => Assert.Equal(expected.Trim(), Translate(tsql));

    [Theory]
    [InlineData("SELECT ISNULL(a, 0) FROM t", """SELECT coalesce("a", 0) FROM "lake"."t" """)]
    [InlineData("SELECT LEN(name) FROM t", """SELECT length(rtrim("name")) FROM "lake"."t" """)]
    [InlineData("SELECT GETDATE()", "SELECT current_localtimestamp()")]
    [InlineData("SELECT GETUTCDATE()", "SELECT timezone('UTC', now())")]
    [InlineData("SELECT NEWID()", "SELECT uuid()")]
    // Who is asking is answered by the session, not by a principal DuckDB would have to keep.
    [InlineData("SELECT SUSER_SNAME()", "SELECT 'sa'")]
    [InlineData("SELECT USER_NAME()", "SELECT 'sa'")]
    [InlineData("SELECT IIF(a > 1, 'big', 'small') FROM t",
        """SELECT CASE WHEN "a" > 1 THEN 'big' ELSE 'small' END FROM "lake"."t" """)]
    // CHARINDEX and instr disagree about which argument comes first.
    [InlineData("SELECT CHARINDEX('a', name) FROM t", """SELECT instr("name", 'a') FROM "lake"."t" """)]
    [InlineData("SELECT DATEPART(year, d) FROM t", """SELECT date_part('year', "d") FROM "lake"."t" """)]
    [InlineData("SELECT DATEDIFF(dd, a, b) FROM t", """SELECT date_diff('day', "a", "b") FROM "lake"."t" """)]
    [InlineData("SELECT DATEADD(month, 1, d) FROM t", """SELECT ("d" + (1) * INTERVAL '1 month') FROM "lake"."t" """)]
    [InlineData("SELECT CEILING(x) FROM t", """SELECT ceil("x") FROM "lake"."t" """)]
    public void RewritesFunctions(string tsql, string expected) => Assert.Equal(expected.Trim(), Translate(tsql));

    [Theory]
    [InlineData("SELECT CAST(a AS NVARCHAR(50)) FROM t", """SELECT CAST("a" AS VARCHAR) FROM "lake"."t" """)]
    [InlineData("SELECT CAST(a AS NVARCHAR(MAX)) FROM t", """SELECT CAST("a" AS VARCHAR) FROM "lake"."t" """)]
    [InlineData("SELECT CAST(a AS INT) FROM t", """SELECT CAST("a" AS INTEGER) FROM "lake"."t" """)]
    [InlineData("SELECT CAST(a AS BIT) FROM t", """SELECT CAST("a" AS BOOLEAN) FROM "lake"."t" """)]
    [InlineData("SELECT CAST(a AS DATETIME2) FROM t", """SELECT CAST("a" AS TIMESTAMP) FROM "lake"."t" """)]
    [InlineData("SELECT CAST(a AS DECIMAL(10,2)) FROM t", """SELECT CAST("a" AS DECIMAL(10, 2)) FROM "lake"."t" """)]
    [InlineData("SELECT CAST(a AS UNIQUEIDENTIFIER) FROM t", """SELECT CAST("a" AS UUID) FROM "lake"."t" """)]
    [InlineData("SELECT CONVERT(INT, a) FROM t", """SELECT CAST("a" AS INTEGER) FROM "lake"."t" """)]
    // A type SQL Server does not have is one the caller meant literally.
    [InlineData("SELECT CAST(a AS HUGEINT) FROM t", """SELECT CAST("a" AS HUGEINT) FROM "lake"."t" """)]
    public void RewritesTypes(string tsql, string expected) => Assert.Equal(expected.Trim(), Translate(tsql));

    [Fact]
    public void PlusConcatenatesOnlyWhenItCanBeProved()
    {
        // One side is text, so `+` means concatenation.
        Assert.Equal("""SELECT 'total: ' || "name" FROM "lake"."t" """.Trim(),
            Translate("SELECT 'total: ' + name FROM t"));
        Assert.Equal("""SELECT UPPER("a") || "b" FROM "lake"."t" """.Trim(),
            Translate("SELECT UPPER(a) + b FROM t"));

        // Nothing says these are text, and turning 1 + 2 into '12' would be worse than failing.
        Assert.Equal("""SELECT "a" + "b" FROM "lake"."t" """.Trim(), Translate("SELECT a + b FROM t"));
        Assert.Equal("SELECT 1 + 2", Translate("SELECT 1 + 2"));
    }

    [Theory]
    [InlineData("SELECT a, b FROM t WHERE a IS NOT NULL AND b IN (1, 2)",
        """SELECT "a", "b" FROM "lake"."t" WHERE "a" IS NOT NULL AND "b" IN (1, 2)""")]
    [InlineData("SELECT * FROM t WHERE a BETWEEN 1 AND 5 AND b NOT LIKE 'x%'",
        """SELECT * FROM "lake"."t" WHERE "a" BETWEEN 1 AND 5 AND "b" NOT LIKE 'x%'""")]
    [InlineData("SELECT * FROM t WHERE NOT (a = 1 OR a = 2)",
        """SELECT * FROM "lake"."t" WHERE NOT ("a" = 1 OR "a" = 2)""")]
    [InlineData("SELECT * FROM t WHERE a <> 1 AND b != 2",
        """SELECT * FROM "lake"."t" WHERE "a" <> 1 AND "b" <> 2""")]
    [InlineData("SELECT * FROM a JOIN b ON a.id = b.id LEFT OUTER JOIN c ON c.id = a.id",
        """SELECT * FROM "lake"."a" INNER JOIN "lake"."b" ON "a"."id" = "b"."id" LEFT JOIN "lake"."c" ON "c"."id" = "a"."id" """)]
    [InlineData("SELECT o.id FROM orders AS o WITH (NOLOCK) WHERE o.id > 0",
        """SELECT "o"."id" FROM "lake"."orders" AS "o" WHERE "o"."id" > 0""")]
    [InlineData("SELECT name = c.name FROM customers c", """SELECT "c"."name" AS "name" FROM "lake"."customers" AS "c" """)]
    // A function name keeps the case it was written in; DuckDB matches it either way. COUNT is an
    // `int` in SQL Server and a BIGINT here, so it is cast back to what the caller expects.
    [InlineData("SELECT COUNT(*) FROM t", """SELECT CAST(COUNT(*) AS INTEGER) FROM "lake"."t" """)]
    [InlineData("SELECT COUNT(DISTINCT a) FROM t", """SELECT CAST(COUNT(DISTINCT "a") AS INTEGER) FROM "lake"."t" """)]
    [InlineData("SELECT COUNT_BIG(*) FROM t", """SELECT count(*) FROM "lake"."t" """)]
    // The cast goes around the window clause, not inside it.
    [InlineData("SELECT COUNT(*) OVER (PARTITION BY a) FROM t",
        """SELECT CAST(COUNT(*) OVER (PARTITION BY "a") AS INTEGER) FROM "lake"."t" """)]
    [InlineData("SELECT row_number() OVER (PARTITION BY a ORDER BY b DESC) FROM t",
        """SELECT row_number() OVER (PARTITION BY "a" ORDER BY "b" DESC) FROM "lake"."t" """)]
    [InlineData("WITH c AS (SELECT 1 AS x) SELECT * FROM c",
        """WITH "c" AS (SELECT 1 AS "x") SELECT * FROM "c" """)]
    [InlineData("SELECT a FROM t UNION ALL SELECT b FROM u",
        """SELECT "a" FROM "lake"."t" UNION ALL SELECT "b" FROM "lake"."u" """)]
    [InlineData("SELECT * FROM (SELECT 1 AS x) AS d", """SELECT * FROM (SELECT 1 AS "x") AS "d" """)]
    [InlineData("SELECT * FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.id = t.id)",
        """SELECT * FROM "lake"."t" WHERE EXISTS (SELECT 1 FROM "lake"."u" WHERE "u"."id" = "t"."id")""")]
    public void RendersQueries(string tsql, string expected) => Assert.Equal(expected.Trim(), Translate(tsql));

    /// OPENJSON is how a collection reaches the server as one parameter -- EF Core's translation of
    /// `WHERE x IN (list)`. It is a derived table here, projecting the columns the WITH clause
    /// declares, so a reference to them needs no rewriting where it is used.
    [Theory]
    [InlineData("SELECT [f].[value] FROM OPENJSON(@p) WITH ([value] int '$') AS [f]",
        """SELECT "f"."value" FROM (SELECT CAST("__element" ->> '$' AS INTEGER) AS "value" """ +
        """FROM (SELECT unnest(from_json(CAST($p AS JSON), '["JSON"]')) AS "__element")) AS "f" """)]
    // A column with no path of its own means `$.` and its own name, as it does on SQL Server.
    [InlineData("SELECT * FROM OPENJSON(@p) WITH (id int, name nvarchar(50)) f",
        """SELECT * FROM (SELECT CAST("__element" ->> '$.id' AS INTEGER) AS "id", """ +
        """CAST("__element" ->> '$.name' AS VARCHAR) AS "name" """ +
        """FROM (SELECT unnest(from_json(CAST($p AS JSON), '["JSON"]')) AS "__element")) AS "f" """)]
    // A second argument names the part of the document the rows come from.
    [InlineData("SELECT * FROM OPENJSON(@p, '$.items') WITH (id int '$.id') AS j",
        """SELECT * FROM (SELECT CAST("__element" ->> '$.id' AS INTEGER) AS "id" """ +
        """FROM (SELECT unnest(from_json(CAST($p AS JSON) -> '$.items', '["JSON"]')) AS "__element")) AS "j" """)]
    // Without a WITH clause it is SQL Server's own shape: the index, the element, and a type code.
    [InlineData("SELECT [key], [value] FROM OPENJSON(@p)",
        """SELECT "key", "value" FROM (SELECT "key", "value", CASE "type" WHEN 'NULL' THEN 0 """ +
        """WHEN 'VARCHAR' THEN 1 WHEN 'BOOLEAN' THEN 3 WHEN 'ARRAY' THEN 4 WHEN 'OBJECT' THEN 5 """ +
        """ELSE 2 END AS "type" FROM json_each(CAST($p AS JSON)))""")]
    public void RendersOpenJson(string tsql, string expected) =>
        Assert.Equal(expected.Trim(), Translate(tsql, "p"));

    /// `AS JSON` keeps the element as a document, which is a shape this does not publish -- and a
    /// refusal names it rather than rendering something that would quietly differ.
    [Fact]
    public void OpenJsonAsJsonIsRefused()
    {
        var refused = Assert.Throws<TSqlException>(() =>
            Translate("SELECT * FROM OPENJSON(@p) WITH (x nvarchar(max) '$.x' AS JSON) AS j", "p"));

        Assert.Contains("AS JSON is not supported", refused.Message);
    }

    [Theory]
    [InlineData("INSERT INTO orders (id, amount) VALUES (1, 2)",
        """INSERT INTO "lake"."orders" ("id", "amount") VALUES (1, 2)""")]
    [InlineData("INSERT INTO orders SELECT * FROM staging",
        """INSERT INTO "lake"."orders" SELECT * FROM "lake"."staging" """)]
    [InlineData("UPDATE orders SET amount = 1, note = 'x' WHERE id = 2",
        """UPDATE "lake"."orders" SET "amount" = 1, "note" = 'x' WHERE "id" = 2""")]
    [InlineData("UPDATE o SET o.amount = 1 WHERE o.id = 2",
        """UPDATE "lake"."o" SET "amount" = 1 WHERE "o"."id" = 2""")]
    [InlineData("UPDATE o SET o.amount = s.amount FROM staging s WHERE o.id = s.id",
        """UPDATE "lake"."o" SET "amount" = "s"."amount" FROM "lake"."staging" AS "s" WHERE "o"."id" = "s"."id" """)]
    [InlineData("DELETE FROM orders WHERE id = 1", """DELETE FROM "lake"."orders" WHERE "id" = 1""")]
    [InlineData("DELETE orders WHERE id = 1", """DELETE FROM "lake"."orders" WHERE "id" = 1""")]
    // A matched-only MERGE is an update joined to its source, and renders as one.
    [InlineData("MERGE INTO [orders] o USING [staging] s ON o.id = s.id WHEN MATCHED THEN UPDATE SET o.amount = s.amount",
        """UPDATE "lake"."orders" AS "o" SET "amount" = "s"."amount" FROM "lake"."staging" AS "s" WHERE "o"."id" = "s"."id" """)]
    public void RendersWrites(string tsql, string expected) => Assert.Equal(expected.Trim(), Translate(tsql));

    [Theory]
    // Anything that changes which rows exist is the layer machinery's business, not one statement's.
    [InlineData("MERGE INTO t a USING s f ON a.id = f.id WHEN MATCHED THEN DELETE", "WHEN MATCHED THEN UPDATE")]
    [InlineData("MERGE INTO t a USING s f ON a.id = f.id WHEN NOT MATCHED THEN INSERT (id) VALUES (f.id)", "WHEN MATCHED THEN UPDATE")]
    [InlineData("MERGE INTO t a USING s f ON a.id = f.id WHEN MATCHED AND f.x > 1 THEN UPDATE SET a.x = f.x",
        "WHEN MATCHED AND")]
    [InlineData("MERGE INTO t a USING s f ON a.id = f.id WHEN MATCHED THEN UPDATE SET a.x = f.x " +
        "WHEN NOT MATCHED THEN INSERT (id) VALUES (f.id)", "WHEN MATCHED THEN UPDATE")]
    [InlineData("MERGE INTO t a USING s f ON a.id = f.id WHEN MATCHED THEN UPDATE SET a.x = f.x OUTPUT inserted.x",
        "OUTPUT")]
    public void RefusesTheMergeBranchesItDoesNotDo(string tsql, string expected) =>
        Assert.Contains(expected, Assert.Throws<TSqlException>(() => Translate(tsql)).Message);

    [Fact]
    public void ParametersBecomeDuckDbPlaceholders()
    {
        Assert.Equal("""SELECT * FROM "lake"."t" WHERE "a" = $p0 AND "b" = $p1""".Trim(),
            Translate("SELECT * FROM t WHERE a = @p0 AND b = @p1", "p0", "p1"));

        // A variable nobody declared is a bug in the client, not something to render and hope.
        var error = Assert.Throws<TSqlException>(() => Translate("SELECT * FROM t WHERE a = @nope"));
        Assert.Contains("@nope", error.Message);
    }

    [Fact]
    public void SessionValuesComeFromTheSession()
    {
        Assert.Equal("SELECT 'Microsoft SQL Server 2022 (duckpg)'", Translate("SELECT @@VERSION"));
        Assert.Contains("unsupported session value @@LOCK_TIMEOUT",
            Assert.Throws<TSqlException>(() => Translate("SELECT @@LOCK_TIMEOUT")).Message);
    }

    [Fact]
    public void CommentsAndBatchesAreUnderstood()
    {
        Assert.Equal("""SELECT 1; SELECT 2""", Translate("SELECT 1; /* a /* nested */ comment */ SELECT 2 -- trailing"));
        Assert.Equal("SET NOCOUNT ON", Translate("SET NOCOUNT ON"));
        Assert.Equal("BEGIN TRANSACTION", Translate("BEGIN TRANSACTION"));
        Assert.Equal("COMMIT", Translate("COMMIT TRAN"));
    }

    [Theory]
    [InlineData("CREATE TABLE t (a INT)", "unsupported statement")]
    [InlineData("SELECT TOP 5 PERCENT * FROM t", "TOP PERCENT")]
    [InlineData("SELECT CONVERT(VARCHAR, d, 120) FROM t", "CONVERT with a style")]
    [InlineData("SELECT * FROM", "expected a name")]
    [InlineData("SELECT 'unterminated", "unterminated")]
    public void RefusesWhatItCannotTranslate(string tsql, string expected) =>
        Assert.Contains(expected, Assert.ThrowsAny<Exception>(() => Translate(tsql)).Message);

    [Theory]
    // What LLBLGen Pro emits: it qualifies columns by schema and table, so the qualifier has to
    // follow the table to the lake or it names something that is not in the FROM clause.
    [InlineData("SELECT [dbo].[Orders].[OrderID] FROM [dbo].[Orders]",
        """SELECT "lake"."Orders"."OrderID" FROM "lake"."Orders" """)]
    [InlineData("SELECT [app].[dbo].[Orders].[OrderID] FROM [app].[dbo].[Orders]",
        """SELECT "lake"."Orders"."OrderID" FROM "lake"."Orders" """)]
    [InlineData("SELECT [dbo].[Orders].* FROM [dbo].[Orders]",
        """SELECT "lake"."Orders".* FROM "lake"."Orders" """)]
    [InlineData("SELECT DISTINCT [LPA_L1].[CustomerID] FROM [dbo].[Customers] [LPA_L1] WHERE ([LPA_L1].[Country] = @p1)",
        """SELECT DISTINCT "LPA_L1"."CustomerID" FROM "lake"."Customers" AS "LPA_L1" WHERE ("LPA_L1"."Country" = $p1)""")]
    [InlineData("UPDATE [dbo].[Orders] SET [Amount] = @p1 WHERE ([dbo].[Orders].[OrderID] = @p2)",
        """UPDATE "lake"."Orders" SET "Amount" = $p1 WHERE ("lake"."Orders"."OrderID" = $p2)""")]
    [InlineData("DELETE FROM [dbo].[Orders] WHERE ([dbo].[Orders].[OrderID] = @p1)",
        """DELETE FROM "lake"."Orders" WHERE ("lake"."Orders"."OrderID" = $p1)""")]
    [InlineData("INSERT INTO [dbo].[Orders] ([OrderID], [Amount]) VALUES (@p1, @p2)",
        """INSERT INTO "lake"."Orders" ("OrderID", "Amount") VALUES ($p1, $p2)""")]
    // Its paging shape: TOP with a parameter over a row-numbered derived table.
    [InlineData("SELECT TOP(@p2) [LPA_L1].[OrderID] FROM (SELECT ROW_NUMBER() OVER(ORDER BY [dbo].[Orders].[OrderID] ASC) AS [__rn], [dbo].[Orders].[OrderID] FROM [dbo].[Orders]) [LPA_L1] WHERE [LPA_L1].[__rn] > @p1",
        """SELECT "LPA_L1"."OrderID" FROM (SELECT ROW_NUMBER() OVER (ORDER BY "lake"."Orders"."OrderID") AS "__rn", "lake"."Orders"."OrderID" FROM "lake"."Orders") AS "LPA_L1" WHERE "LPA_L1"."__rn" > $p1 LIMIT $p2""")]
    public void RendersWhatAnOrmSends(string tsql, string expected) =>
        Assert.Equal(expected.Trim(), Translate(tsql, "p1", "p2"));
}
