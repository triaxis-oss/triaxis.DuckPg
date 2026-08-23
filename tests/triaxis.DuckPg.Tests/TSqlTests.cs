using triaxis.DuckPg.TSql;

namespace triaxis.DuckPg.Tests;

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

        return string.Join("; ", TSqlTranslator.Translate(sql, context).Select(t => t.Sql));
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
        """SELECT * FROM "lake"."orders" ORDER BY "id" NULLS FIRST LIMIT 5""")]
    [InlineData("SELECT * FROM orders ORDER BY id OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY",
        """SELECT * FROM "lake"."orders" ORDER BY "id" NULLS FIRST LIMIT 5 OFFSET 10""")]
    // A null is the smallest value SQL Server knows, which is neither of DuckDB's defaults.
    [InlineData("SELECT * FROM orders ORDER BY id DESC, amount",
        """SELECT * FROM "lake"."orders" ORDER BY "id" DESC NULLS LAST, "amount" NULLS FIRST""")]
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
    [InlineData("SELECT DATEFROMPARTS(2026, 8, 20)", "SELECT make_date(2026, 8, 20)")]
    // The fraction of a second is an argument of its own in T-SQL and part of the seconds in DuckDB:
    // milliseconds for the `datetime` one, and units the trailing precision names for the rest.
    [InlineData("SELECT DATETIMEFROMPARTS(2026, 8, 20, 13, 45, 30, 500)",
        "SELECT make_timestamp(2026, 8, 20, 13, 45, (30) + (500) / 1000.0)")]
    [InlineData("SELECT DATETIME2FROMPARTS(2026, 8, 20, 13, 45, 30, 1234567, 7)",
        "SELECT make_timestamp(2026, 8, 20, 13, 45, (30) + (1234567) / pow(10.0, 7))")]
    [InlineData("SELECT SMALLDATETIMEFROMPARTS(2026, 8, 20, 13, 45)",
        "SELECT make_timestamp(2026, 8, 20, 13, 45, 0)")]
    [InlineData("SELECT TIMEFROMPARTS(13, 45, 30, 5000000, 7)",
        "SELECT make_time(13, 45, (30) + (5000000) / pow(10.0, 7))")]
    // The offset is what turns the parts into an instant, since that is all a TIMESTAMPTZ keeps.
    [InlineData("SELECT DATETIMEOFFSETFROMPARTS(2026, 8, 20, 13, 45, 30, 0, 2, 30, 0)",
        "SELECT timezone('UTC', make_timestamp(2026, 8, 20, 13, 45, (30) + (0) / pow(10.0, 0)) "
            + "- (2) * INTERVAL '1 hour' - (30) * INTERVAL '1 minute')")]
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
    // A style names a date format, which is a thing .NET spells out and duckpg answers in .NET.
    [InlineData("SELECT CONVERT(VARCHAR, d, 120) FROM t",
        """SELECT duckpg_convert_to_text("d", CAST(120 AS INTEGER)) FROM "lake"."t" """)]
    [InlineData("SELECT CONVERT(datetime, s, 103) FROM t",
        """SELECT CAST(duckpg_convert_from_text("s", CAST(103 AS INTEGER)) AS TIMESTAMP) FROM "lake"."t" """)]
    // A type SQL Server does not have is one the caller meant literally.
    [InlineData("SELECT CAST(a AS HUGEINT) FROM t", """SELECT CAST("a" AS HUGEINT) FROM "lake"."t" """)]
    public void RewritesTypes(string tsql, string expected) => Assert.Equal(expected.Trim(), Translate(tsql));

    /// The conversion is written only where the column resolves to a `bit`. Nothing here knows what
    /// a derived table's columns are, so a reference into one is rendered as it was written -- and
    /// DuckDB says what it thinks of that, which is better than a cast nobody could justify.
    [Fact]
    public void BitArithmeticIsConvertedWhereTheColumnResolves()
    {
        static string Translated(string sql) => string.Join("; ", TSqlTranslator.Translate(sql, new TSqlContext(
            "lake", new Dictionary<string, string>(), new HashSet<string>(), "sa",
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["t"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { ["flag"] = "BOOLEAN", ["n"] = "INTEGER", ["amount"] = "DECIMAL(18,2)" },
                ["u"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { ["flag"] = "INTEGER", ["id"] = "INTEGER" },
            })).Select(t => t.Sql));

        Assert.Equal("""SELECT "n" * CAST("flag" AS INTEGER) FROM "lake"."t" """.Trim(),
            Translated("SELECT n * flag FROM t"));

        // Qualified, so the one that is a bit is converted and the one that is not is left alone.
        Assert.Equal(
            """SELECT CAST("a"."flag" AS INTEGER) + "b"."flag" FROM "lake"."t" AS "a" """ +
            """INNER JOIN "lake"."u" AS "b" ON "b"."id" = "a"."n" """.Trim(),
            Translated("SELECT a.flag + b.flag FROM t AS a INNER JOIN u AS b ON b.id = a.n"));

        // A decimal is not a bit, and converting one would truncate it.
        Assert.Equal("""SELECT "n" * "amount" FROM "lake"."t" """.Trim(), Translated("SELECT n * amount FROM t"));

        // Nothing says what a derived table's `flag` is, so nothing is done to it.
        Assert.Equal("""SELECT "n" * "flag" FROM (SELECT 1 AS "flag") AS "d" """.Trim(),
            Translated("SELECT n * flag FROM (SELECT 1 AS flag) AS d"));

        // Only arithmetic: a comparison and an aggregate already mean the same thing to both.
        Assert.Equal("""SELECT * FROM "lake"."t" WHERE "flag" = 1""", Translated("SELECT * FROM t WHERE flag = 1"));
    }

    /// `SELECT @a = x` is an assignment and `SELECT a = x` is an alias, which read alike and are
    /// nothing like each other -- the difference is that one of them names a variable.
    [Fact]
    public void AnAssignmentSelectIsTheQueryUnderIt()
    {
        var context = new TSqlContext("lake", new Dictionary<string, string>(),
            new HashSet<string>(["p1", "p2"], StringComparer.OrdinalIgnoreCase), "sa");

        var statements = TSqlParser.Parse("SELECT  @p1 = 1, @p2 = a FROM t");
        var assign = Assert.IsType<AssignStatement>(Assert.Single(statements));

        Assert.Equal(["p1", "p2"], assign.Variables);
        Assert.Equal("""SELECT 1, "a" FROM "lake"."t" """.Trim(), TSqlWriter.Write(assign, context));

        // What it assigns is carried beside the statement, since the rows go to the caller's
        // parameters rather than back down the wire.
        Assert.Equal(["p1", "p2"], TSqlTranslator.Translate(statements[0], context).Assigns);
    }

    [Fact]
    public void ASelectCannotBothAssignAndReturn()
    {
        var refused = Assert.Throws<TSqlException>(() => TSqlParser.Parse("SELECT @p1 = 1, 2"));
        Assert.Contains("assigns to variables or returns rows", refused.Message);
    }

    /// The generated key is written into the statement as the value it has now, in the type SQL
    /// Server answers all three with.
    [Fact]
    public void TheGeneratedKeyIsRenderedAsAValue()
    {
        static string Translated(string sql, decimal? identity, decimal? orders) =>
            string.Join("; ", TSqlTranslator.Translate(sql, new TSqlContext(
                "lake", new Dictionary<string, string>(), new HashSet<string>(), "sa",
                Identity: identity,
                Identities: table => table.Equals("orders", StringComparison.OrdinalIgnoreCase) ? orders : null))
                .Select(t => t.Sql));

        Assert.Equal("SELECT CAST(42 AS DECIMAL(38,0))", Translated("SELECT SCOPE_IDENTITY()", 42m, null));
        Assert.Equal("SELECT CAST(42 AS DECIMAL(38,0))", Translated("SELECT @@IDENTITY", 42m, null));

        // Nothing generated is a null rather than a zero, which would be a key.
        Assert.Equal("SELECT CAST(NULL AS DECIMAL(38,0))", Translated("SELECT SCOPE_IDENTITY()", null, null));

        // IDENT_CURRENT is the table's rather than the session's, and the schema it was written
        // with is not part of the question: a lake has one.
        Assert.Equal("SELECT CAST(7 AS DECIMAL(38,0))", Translated("SELECT IDENT_CURRENT('orders')", null, 7m));
        Assert.Equal("SELECT CAST(7 AS DECIMAL(38,0))", Translated("SELECT IDENT_CURRENT('[dbo].[orders]')", null, 7m));
        Assert.Equal("SELECT CAST(NULL AS DECIMAL(38,0))", Translated("SELECT IDENT_CURRENT('other')", null, 7m));
    }

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
        """SELECT row_number() OVER (PARTITION BY "a" ORDER BY "b" DESC NULLS LAST) FROM "lake"."t" """)]
    [InlineData("WITH c AS (SELECT 1 AS x) SELECT * FROM c",
        """WITH "c" AS (SELECT 1 AS "x") SELECT * FROM "c" """)]
    [InlineData("SELECT a FROM t UNION ALL SELECT b FROM u",
        """SELECT "a" FROM "lake"."t" UNION ALL SELECT "b" FROM "lake"."u" """)]
    // Written parentheses survive where the words alone would regroup: EXCEPT is not associative,
    // and INTERSECT binds tighter than the operators around it in both dialects.
    [InlineData("SELECT 1 UNION (SELECT 2 EXCEPT SELECT 3)", "SELECT 1 UNION (SELECT 2 EXCEPT SELECT 3)")]
    [InlineData("(SELECT 1 UNION SELECT 2) INTERSECT SELECT 3", "(SELECT 1 UNION SELECT 2) INTERSECT SELECT 3")]
    [InlineData("SELECT 1 UNION SELECT 2 INTERSECT SELECT 3", "SELECT 1 UNION SELECT 2 INTERSECT SELECT 3")]
    [InlineData("SELECT 1 EXCEPT SELECT 2 EXCEPT SELECT 3", "SELECT 1 EXCEPT SELECT 2 EXCEPT SELECT 3")]
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

    /// What EF Core sends a batch of rows as: the rows are the USING clause, `ON 1=0` says the
    /// matched branch is unreachable, and the insert branch takes every one of them.
    [Fact]
    public void MergeOverAConditionThatCannotMatchIsAnInsert()
    {
        Assert.Equal(
            """INSERT INTO "lake"."orders" ("order_id", "amount") SELECT "i"."order_id", "i"."amount" """ +
            """FROM (VALUES ($p0, $p1, 0), ($p2, $p3, 1)) AS "i" ("order_id", "amount", "_Position")""",
            Translate("""
                MERGE [orders] USING (
                VALUES (@p0, @p1, 0), (@p2, @p3, 1)) AS i ([order_id], [amount], _Position)
                ON 1=0
                WHEN NOT MATCHED THEN
                INSERT ([order_id], [amount]) VALUES (i.[order_id], i.[amount]);
                """, "p0", "p1", "p2", "p3"));
    }

    /// The rows are projected under the target's own column names and the answer is a RETURNING over
    /// them, so the gateway can write them down and answer from the same rows -- including the
    /// column the caller did not send, which is what it is asking for.
    [Fact]
    public void AnInsertAskedWhatItWroteReturnsIt()
    {
        Assert.Equal(
            """INSERT INTO "lake"."orders" ("amount") SELECT "i"."amount" AS "amount" """ +
            """FROM (VALUES ($p0, 0), ($p1, 1)) AS "i" ("amount", "_Position") RETURNING "order_id", "_Position" """.Trim(),
            Translate("""
                MERGE [orders] USING (VALUES (@p0, 0), (@p1, 1)) AS i ([amount], _Position)
                ON 1=0
                WHEN NOT MATCHED THEN INSERT ([amount]) VALUES (i.[amount])
                OUTPUT INSERTED.[order_id], i._Position;
                """, "p0", "p1"));

        // The single-row form: the rows still have to stand where a table does.
        Assert.Equal(
            """INSERT INTO "lake"."orders" ("amount") SELECT "amount" AS "amount" """ +
            """FROM (VALUES ($p0)) AS "_rows" ("amount") RETURNING "order_id" """.Trim(),
            Translate("INSERT INTO [orders] ([amount]) OUTPUT INSERTED.[order_id] VALUES (@p0)", "p0"));
    }

    /// `DELETE TOP (n)` deletes at most n of the rows the predicate matches, and says nothing about
    /// which -- there is no ORDER BY in this form. It goes out as a trailing `LIMIT` for the gateway
    /// to perform on the lake's own key, which neither dialect would run as it stands.
    [Theory]
    [InlineData("DELETE TOP (10000) FROM [t]", """DELETE FROM "lake"."t" LIMIT 10000""")]
    // Verbatim from a legacy ORM, mixed case and trailing semicolon and all.
    [InlineData("Delete Top (10000) From STAFF_SCHEDULE_RevisionEvents;",
        """DELETE FROM "lake"."STAFF_SCHEDULE_RevisionEvents" LIMIT 10000""")]
    [InlineData("DELETE TOP (5) FROM [t] WHERE [a] = 1", """DELETE FROM "lake"."t" WHERE "a" = 1 LIMIT 5""")]
    [InlineData("DELETE TOP (5) PERCENT FROM [t]", """DELETE FROM "lake"."t" LIMIT 5 PERCENT""")]
    [InlineData("UPDATE TOP (3) [t] SET [a] = 1 WHERE [b] = 2",
        """UPDATE "lake"."t" SET "a" = 1 WHERE "b" = 2 LIMIT 3""")]
    [InlineData("UPDATE TOP (3) PERCENT [t] SET [a] = 1", """UPDATE "lake"."t" SET "a" = 1 LIMIT 3 PERCENT""")]
    [InlineData("DELETE TOP (2) FROM [orders] OUTPUT 1 WHERE [id] = 2",
        """DELETE FROM "lake"."orders" WHERE "id" = 2 LIMIT 2 RETURNING 1""")]
    // The parentheses are what tell the keyword from a table of that name, which is why SQL Server
    // requires them on a write and duckpg does too.
    [InlineData("DELETE FROM top WHERE id = 1", """DELETE FROM "lake"."top" WHERE "id" = 1""")]
    [InlineData("UPDATE top SET a = 1", """UPDATE "lake"."top" SET "a" = 1""")]
    public void RendersARowLimitedWrite(string tsql, string expected) =>
        Assert.Equal(expected.Trim(), Translate(tsql));

    /// The count may be a parameter, which a lake binds like any other.
    [Fact]
    public void ARowLimitCanBeAParameter() =>
        Assert.Equal("""DELETE FROM "lake"."t" WHERE "a" = $p1 LIMIT $p0""",
                     Translate("DELETE TOP (@p0) FROM [t] WHERE [a] = @p1", "p0", "p1"));

    /// What EF Core's ExecuteDelete writes: the target names an alias the FROM clause binds, and the
    /// predicate names it too.
    [Theory]
    [InlineData("DELETE FROM [s] FROM [orders] AS [s] WHERE [s].[amount] = 23",
        """DELETE FROM "lake"."orders" AS "s" WHERE "s"."amount" = 23""")]
    // A name the FROM clause does not bind is an ordinary table, and the FROM stays where it was.
    [InlineData("DELETE FROM orders FROM staging WHERE staging.id = orders.id",
        """DELETE FROM "lake"."orders" USING "lake"."staging" WHERE "staging"."id" = "orders"."id""" + "\"")]
    public void RendersAnAliasedDeleteTarget(string tsql, string expected) =>
        Assert.Equal(expected.Trim(), Translate(tsql));

    /// What EF Core's ExecuteUpdate writes, and the shape a DELETE already resolved: the target
    /// names an alias the FROM clause binds.
    [Theory]
    [InlineData("UPDATE [o] SET [amount] = 3 FROM [orders] AS [o] WHERE [o].[id] = 1",
        """UPDATE "lake"."orders" AS "o" SET "amount" = 3 WHERE "o"."id" = 1""")]
    // A name the FROM clause does not bind is the table it says it is, and the FROM stays put.
    [InlineData("UPDATE orders SET amount = s.amount FROM staging s WHERE orders.id = s.id",
        """UPDATE "lake"."orders" SET "amount" = "s"."amount" FROM "lake"."staging" AS "s" WHERE "orders"."id" = "s"."id" """)]
    public void RendersAnAliasedUpdateTarget(string tsql, string expected) =>
        Assert.Equal(expected.Trim(), Translate(tsql));

    /// A join hint tells SQL Server's optimiser which algorithm to use and says nothing about which
    /// rows come back, so it is read and dropped.
    [Theory]
    [InlineData("SELECT a.id FROM a INNER LOOP JOIN b ON b.id = a.id")]
    [InlineData("SELECT a.id FROM a INNER HASH JOIN b ON b.id = a.id")]
    [InlineData("SELECT a.id FROM a INNER MERGE JOIN b ON b.id = a.id")]
    [InlineData("SELECT a.id FROM a LEFT OUTER LOOP JOIN b ON b.id = a.id")]
    public void JoinHintsAreDropped(string tsql) =>
        Assert.Equal(tsql.Contains("LEFT")
            ? """SELECT "a"."id" FROM "lake"."a" LEFT JOIN "lake"."b" ON "b"."id" = "a"."id" """.Trim()
            : """SELECT "a"."id" FROM "lake"."a" INNER JOIN "lake"."b" ON "b"."id" = "a"."id" """.Trim(),
            Translate(tsql));

    /// What EF Core writes for an update filtered through a navigation: the target is an alias, and
    /// the join is what picks its rows. The other table becomes the write's own FROM and the join
    /// condition joins the WHERE, which is the shape a lake already writes.
    [Theory]
    [InlineData("UPDATE [s] SET [note] = 'x' FROM [orders] AS [s] INNER JOIN [staging] AS [c] " +
        "ON [c].[id] = [s].[id] WHERE [c].[name] = 'Admin'",
        """UPDATE "lake"."orders" AS "s" SET "note" = 'x' FROM "lake"."staging" AS "c" """ +
        """WHERE ("c"."name" = 'Admin') AND ("c"."id" = "s"."id")""")]
    [InlineData("DELETE FROM [s] FROM [orders] AS [s] INNER JOIN [staging] AS [c] ON [c].[id] = [s].[id]",
        """DELETE FROM "lake"."orders" AS "s" USING "lake"."staging" AS "c" WHERE "c"."id" = "s"."id" """)]
    public void RendersAWriteJoinedToAnotherTable(string tsql, string expected) =>
        Assert.Equal(expected.Trim(), Translate(tsql));

    /// The target named again inside its own FROM clause, which is the smallest thing an ORM
    /// spelling every table out writes -- no join, nothing to prune, only the one source. It is one
    /// table either way, and a limit on it does not make it two.
    [Theory]
    [InlineData("DELETE FROM [db].[dbo].[t] FROM [db].[dbo].[t] WHERE [db].[dbo].[t].[c] = 1",
        """DELETE FROM "lake"."t" WHERE "lake"."t"."c" = 1""")]
    [InlineData("DELETE TOP (5) FROM [db].[dbo].[t] FROM [db].[dbo].[t] WHERE [db].[dbo].[t].[c] = 1",
        """DELETE FROM "lake"."t" WHERE "lake"."t"."c" = 1 LIMIT 5""")]
    [InlineData("UPDATE TOP (5) [db].[dbo].[t] SET [c] = 2 FROM [db].[dbo].[t] WHERE [db].[dbo].[t].[c] = 1",
        """UPDATE "lake"."t" SET "c" = 2 WHERE "lake"."t"."c" = 1 LIMIT 5""")]
    public void RendersATargetItsOwnFromClauseNamesAgain(string tsql, string expected) =>
        Assert.Equal(expected.Trim(), Translate(tsql));

    /// A row limit on that same join tree: the limit is the write's and the tree is resolved before
    /// it, so what comes out is the folded join with the limit still on the end. The joined table is
    /// qualified two-part where the predicate reads it and three-part where the FROM clause binds
    /// it -- real client output, and both have to reach the one source.
    [Fact]
    public void RendersARowLimitOnTheJoinTreeAnOrmWritesOut() =>
        Assert.Equal(
            """DELETE FROM "lake"."entries" USING "lake"."categories" """ +
            """WHERE (("lake"."entries"."revision_pKey" = $p1 AND "lake"."categories"."level" = 3)) AND """ +
            """("lake"."categories"."pKey" = "lake"."entries"."category_pKey") LIMIT 10000""",
            Translate("""
                DELETE TOP (10000) FROM [lake].[dbo].[entries]
                FROM (([lake].[dbo].[categories]
                       INNER JOIN [lake].[dbo].[entries]
                         ON [lake].[dbo].[categories].[pKey]=[lake].[dbo].[entries].[category_pKey])
                      LEFT JOIN [lake].[dbo].[batches]
                         ON [lake].[dbo].[batches].[pKey]=[lake].[dbo].[entries].[batch_pKey])
                WHERE ([lake].[dbo].[entries].[revision_pKey] = @p1 AND [dbo].[categories].[level] = 3)
                """, "p1"));

    /// What LLBLGen renders: every table spelled out in full, the target named rather than aliased
    /// and sitting inside its own join tree, the tree parenthesised, and the entity's whole relation
    /// graph written out whether the statement reads any of it or not. The LEFT JOIN nothing else
    /// names cannot pick which rows are deleted -- every entries row comes through it, matched or
    /// not -- so it goes, and what is left is the ordinary joined delete.
    [Fact]
    public void RendersTheJoinTreeAnOrmWritesOut()
    {
        Assert.Equal(
            """DELETE FROM "lake"."entries" USING "lake"."categories" """ +
            """WHERE (("lake"."entries"."revision_pKey" = $p1)) AND """ +
            """("lake"."categories"."pKey" = "lake"."entries"."category_pKey")""",
            Translate("""
                DELETE FROM [lake].[dbo].[entries]
                FROM (([lake].[dbo].[categories]
                       INNER JOIN [lake].[dbo].[entries]
                         ON [lake].[dbo].[categories].[pKey]=[lake].[dbo].[entries].[category_pKey])
                      LEFT JOIN [lake].[dbo].[batches]
                         ON [lake].[dbo].[batches].[pKey]=[lake].[dbo].[entries].[batch_pKey])
                WHERE ([lake].[dbo].[entries].[revision_pKey] = @p1)
                """, "p1"));
    }

    /// A join that decides nothing is dropped; one that decides something cannot fold into ordinary
    /// clauses -- it keeps rows matching nothing -- so the write carries the whole tree instead,
    /// target inside it, and the gateway keys the rows off what the tree selects.
    [Theory]
    // Nothing names the outer side, so every row of the target survives the join either way.
    [InlineData("DELETE FROM [s] FROM [orders] AS [s] LEFT JOIN [staging] AS [t] ON t.id = s.id",
        """DELETE FROM "lake"."orders" AS "s" """)]
    [InlineData("UPDATE [s] SET [x] = 1 FROM [orders] AS [s] LEFT JOIN [staging] AS [t] ON t.id = s.id",
        """UPDATE "lake"."orders" AS "s" SET "x" = 1""")]
    // Read by the predicate, so the join is what picks the rows and travels whole -- rows whose
    // outer side matched nothing included, which is exactly what such a predicate means to catch.
    [InlineData("DELETE FROM [s] FROM [orders] AS [s] LEFT JOIN [staging] AS [t] ON t.id = s.id WHERE t.x IS NULL",
        """DELETE FROM "lake"."orders" AS "s" USING "lake"."orders" AS "s" LEFT JOIN "lake"."staging" AS "t" """ +
        """ON "t"."id" = "s"."id" WHERE "t"."x" IS NULL""")]
    // Read by what the update writes: the assignment sees the outer side, null fill and all.
    [InlineData("UPDATE [s] SET [x] = [t].[x] FROM [orders] AS [s] LEFT JOIN [staging] AS [t] ON t.id = s.id",
        """UPDATE "lake"."orders" AS "s" SET "x" = "t"."x" FROM "lake"."orders" AS "s" """ +
        """LEFT JOIN "lake"."staging" AS "t" ON "t"."id" = "s"."id" """)]
    // An unqualified column could be anyone's, and a subquery may be correlated: neither is read
    // here, so neither is guessed at -- the join stays, and stays whole.
    [InlineData("DELETE FROM [s] FROM [orders] AS [s] LEFT JOIN [staging] AS [t] ON t.id = s.id WHERE x = 1",
        """DELETE FROM "lake"."orders" AS "s" USING "lake"."orders" AS "s" LEFT JOIN "lake"."staging" AS "t" """ +
        """ON "t"."id" = "s"."id" WHERE "x" = 1""")]
    [InlineData("DELETE FROM [s] FROM [orders] AS [s] LEFT JOIN [staging] AS [t] ON t.id = s.id " +
        "WHERE EXISTS (SELECT 1 FROM other WHERE other.id = t.id)",
        """DELETE FROM "lake"."orders" AS "s" USING "lake"."orders" AS "s" LEFT JOIN "lake"."staging" AS "t" """ +
        """ON "t"."id" = "s"."id" WHERE EXISTS (SELECT 1 FROM "lake"."other" WHERE "other"."id" = "t"."id")""")]
    // A target on the nullable side reads as an inner join: a row kept for matching nothing is not
    // a target row at all, only the null fill where its columns would be.
    [InlineData("DELETE FROM [s] FROM [staging] AS [t] LEFT JOIN [orders] AS [s] ON t.id = s.id",
        """DELETE FROM "lake"."orders" AS "s" USING "lake"."staging" AS "t" WHERE "t"."id" = "s"."id" """)]
    public void DropsOnlyTheJoinsThatDecideNothing(string tsql, string expected) =>
        Assert.Equal(expected.Trim(), Translate(tsql));

    /// The tree a write carries leads with its target, whatever order the client wrote it in --
    /// mirroring a join renames its sides and nothing else -- and a join preserving the target's
    /// side only where the target's own rows come through it keeps FULL and RIGHT out of the
    /// spelling the gateway reads.
    [Theory]
    // The ORM tree with the LEFT JOIN read: the inner join turns to put the target first, and the
    // outer one follows it whole.
    [InlineData("""
        DELETE FROM [lake].[dbo].[entries]
        FROM (([lake].[dbo].[categories]
               INNER JOIN [lake].[dbo].[entries]
                 ON [lake].[dbo].[categories].[pKey]=[lake].[dbo].[entries].[category_pKey])
              LEFT JOIN [lake].[dbo].[batches]
                 ON [lake].[dbo].[batches].[pKey]=[lake].[dbo].[entries].[batch_pKey])
        WHERE ([lake].[dbo].[batches].[pKey] IS NULL)
        """,
        """DELETE FROM "lake"."entries" USING "lake"."entries" INNER JOIN "lake"."categories" """ +
        """ON "lake"."categories"."pKey" = "lake"."entries"."category_pKey" LEFT JOIN "lake"."batches" """ +
        """ON "lake"."batches"."pKey" = "lake"."entries"."batch_pKey" WHERE ("lake"."batches"."pKey" IS NULL)""")]
    // A RIGHT JOIN preserving the target is the same join with its sides renamed.
    [InlineData("DELETE FROM [t] FROM [staging] AS [s] RIGHT JOIN [t] ON [s].[id] = [t].[id] WHERE [s].[id] IS NULL",
        """DELETE FROM "lake"."t" USING "lake"."t" LEFT JOIN "lake"."staging" AS "s" """ +
        """ON "s"."id" = "t"."id" WHERE "s"."id" IS NULL""")]
    // The target named by its table's name while the FROM clause aliases it: SQL Server binds the
    // two as one reference, and so does the write.
    [InlineData("DELETE FROM [t] FROM [t] AS [a] LEFT JOIN [staging] AS [s] ON s.id = a.id WHERE s.x IS NULL",
        """DELETE FROM "lake"."t" AS "a" USING "lake"."t" AS "a" LEFT JOIN "lake"."staging" AS "s" """ +
        """ON "s"."id" = "a"."id" WHERE "s"."x" IS NULL""")]
    public void TurnsTheJoinTreeToLeadWithItsTarget(string tsql, string expected) =>
        Assert.Equal(expected.Trim(), Translate(tsql));

    /// A target the FROM clause binds twice is a write against neither: nothing says which
    /// occurrence its rows come from. Refused, as SQL Server refuses it -- and refusing is the safe
    /// answer rather than the cautious one, since a target that resolves to nothing is scanned
    /// beside the tree instead of through it, and that cross join is every row of the table.
    [Theory]
    [InlineData("DELETE FROM [t] FROM [t] AS [a] INNER JOIN [t] AS [b] ON [b].[id] = [a].[id] " +
        "LEFT JOIN [staging] AS [s] ON [s].[id] = [a].[id] WHERE [s].[id] IS NULL")]
    [InlineData("UPDATE [t] SET [x] = 1 FROM [t] AS [a] INNER JOIN [t] AS [b] ON [b].[id] = [a].[id]")]
    // Two unaliased occurrences say no more than two aliased ones do.
    [InlineData("DELETE FROM [t] FROM [t] INNER JOIN [t] ON 1 = 1")]
    public void RefusesATargetTheFromClauseBindsTwice(string tsql) =>
        Assert.Contains("is ambiguous", Assert.Throws<TSqlException>(() => Translate(tsql)).Message);

    /// The OUTPUT clause sits between SET and WHERE, so a statement carrying one does not end at its
    /// assignments. `OUTPUT 1` is how EF Core counts the rows a statement touched.
    [Theory]
    [InlineData("UPDATE [orders] SET [amount] = 1 OUTPUT 1 WHERE [id] = 2",
        """UPDATE "lake"."orders" SET "amount" = 1 WHERE "id" = 2 RETURNING 1""")]
    [InlineData("DELETE FROM [orders] OUTPUT 1 WHERE [id] = 2",
        """DELETE FROM "lake"."orders" WHERE "id" = 2 RETURNING 1""")]
    [InlineData("UPDATE [orders] SET [amount] = 1 OUTPUT INSERTED.[amount] AS [was] WHERE [id] = 2",
        """UPDATE "lake"."orders" SET "amount" = 1 WHERE "id" = 2 RETURNING "amount" AS "was""" + "\"")]
    public void RendersTheOutputClauseAWriteCarries(string tsql, string expected) =>
        Assert.Equal(expected.Trim(), Translate(tsql));

    [Theory]
    // Anything that changes which rows exist is the layer machinery's business, not one statement's.
    [InlineData("MERGE INTO t a USING s f ON a.id = f.id WHEN MATCHED THEN DELETE", "WHEN MATCHED THEN UPDATE")]
    [InlineData("MERGE INTO t a USING s f ON a.id = f.id WHEN NOT MATCHED THEN INSERT (id) VALUES (f.id)",
        "a condition that cannot match")]
    [InlineData("MERGE INTO t a USING s f ON a.id = f.id WHEN MATCHED AND f.x > 1 THEN UPDATE SET a.x = f.x",
        "WHEN MATCHED AND")]
    [InlineData("MERGE INTO t a USING s f ON a.id = f.id WHEN MATCHED THEN UPDATE SET a.x = f.x " +
        "WHEN NOT MATCHED THEN INSERT (id) VALUES (f.id)", "only one MERGE branch")]
    // An insert branch over a condition that can match is an insert of what is not already there,
    // and what "already there" means when the row is in a layer below is not one statement's call.
    [InlineData("MERGE t USING (VALUES (1)) AS i (id) ON t.id = i.id WHEN NOT MATCHED THEN INSERT (id) VALUES (i.id)",
        "a condition that cannot match")]
    [InlineData("MERGE t USING (VALUES (1)) AS i (id) ON 1=0 WHEN NOT MATCHED BY SOURCE THEN DELETE",
        "NOT MATCHED BY SOURCE")]
    // What OUTPUT cannot mean over an insert: there is no row it replaced, and nowhere else to put
    // the answer than the answer.
    [InlineData("MERGE t USING (VALUES (1)) AS i (id) ON 1=0 WHEN NOT MATCHED THEN " +
        "INSERT (id) VALUES (i.id) OUTPUT DELETED.id", "an insert replaces none")]
    [InlineData("INSERT INTO t (a) OUTPUT INSERTED.a INTO @kept VALUES (1)", "puts the rows somewhere else")]
    [InlineData("MERGE t a USING s f ON a.id = f.id WHEN MATCHED THEN UPDATE SET a.x = f.x OUTPUT inserted.x",
        "not the update")]
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

    /// A scratch table the client makes for itself: DuckDB's temporary tables belong to a
    /// connection, which is what SQL Server means by a session, so `#t` needs no schema and must
    /// not be given the lake's.
    [Theory]
    [InlineData("DROP TABLE IF EXISTS #staged", """DROP TABLE IF EXISTS "#staged" """)]
    [InlineData("DROP TABLE #staged", """DROP TABLE "#staged" """)]
    [InlineData("SELECT a.id, a.amount INTO #staged FROM orders a WHERE a.id > 0",
        """CREATE TEMP TABLE "#staged" AS SELECT "a"."id", "a"."amount" FROM "lake"."orders" AS "a" WHERE "a"."id" > 0""")]
    [InlineData("SELECT s.id FROM #staged s JOIN orders o ON o.id = s.id",
        """SELECT "s"."id" FROM "#staged" AS "s" INNER JOIN "lake"."orders" AS "o" ON "o"."id" = "s"."id" """)]
    [InlineData("INSERT INTO #staged (id) VALUES (1)", """INSERT INTO "#staged" ("id") VALUES (1)""")]
    public void RendersTemporaryTables(string tsql, string expected) => Assert.Equal(expected.Trim(), Translate(tsql));

    /// A pooled connection's reset finds what to drop by enumerating DuckDB's whole catalog, so it
    /// only asks when something made a temporary table -- which means every way of making one has to
    /// be recognised in what was rendered: the client's `#t` and the gateway's own scratch alike.
    [Fact]
    public void RecognisesWhatMakesATemporaryTable()
    {
        Assert.True(SqlText.MakesTemporary(Translate("SELECT a.id INTO #staged FROM orders a")));
        Assert.True(SqlText.MakesTemporary("CREATE OR REPLACE TEMP TABLE duckpg_written AS SELECT 1"));

        Assert.False(SqlText.MakesTemporary(Translate("DROP TABLE #staged")));
        Assert.False(SqlText.MakesTemporary(Translate("SELECT s.id FROM #staged s")));
        Assert.False(SqlText.MakesTemporary(Translate("SELECT 'CREATE TEMP TABLE' AS x")));
    }

    /// Nested joins defer their conditions, closing in reverse: the ON belonging to the inner join
    /// arrives first. Read left-deep instead, the last ON has nothing left to attach to.
    [Theory]
    [InlineData("SELECT * FROM a LEFT JOIN b INNER JOIN c ON c.id = b.cid ON b.id = a.bid",
        """SELECT * FROM "lake"."a" LEFT JOIN ("lake"."b" INNER JOIN "lake"."c" ON "c"."id" = "b"."cid") """ +
        """ON "b"."id" = "a"."bid" """)]
    // The ordinary chain is still a chain: an ON right after the operand ends it.
    [InlineData("SELECT * FROM a JOIN b ON b.id = a.bid JOIN c ON c.id = b.cid",
        """SELECT * FROM "lake"."a" INNER JOIN "lake"."b" ON "b"."id" = "a"."bid" """ +
        """INNER JOIN "lake"."c" ON "c"."id" = "b"."cid" """)]
    // What a dacpac view is written with, and what SSDT emits: parentheses saying the same thing.
    [InlineData("SELECT * FROM a LEFT JOIN (b INNER JOIN c ON c.id = b.cid) ON b.id = a.bid",
        """SELECT * FROM "lake"."a" LEFT JOIN ("lake"."b" INNER JOIN "lake"."c" ON "c"."id" = "b"."cid") """ +
        """ON "b"."id" = "a"."bid" """)]
    public void RendersNestedJoins(string tsql, string expected) => Assert.Equal(expected.Trim(), Translate(tsql));

    /// `TOP n PERCENT` is a share of the rows rounded up, and at least one of them. DuckDB's own
    /// `LIMIT n%` rounds down, so the count is worked out rather than handed over.
    [Fact]
    public void TopPercentCountsTheRows()
    {
        Assert.Equal(
            """SELECT * FROM "lake"."orders" ORDER BY "id" NULLS FIRST LIMIT (SELECT CAST(CEIL(count(*) * 50 / 100.0) """ +
            """AS BIGINT) FROM (SELECT * FROM "lake"."orders") AS "_percent")""",
            Translate("SELECT TOP 50 PERCENT * FROM orders ORDER BY id"));
    }

    /// A savepoint is a promise to undo part of a transaction, and DuckDB has nothing to undo it
    /// with. Marking one costs nothing; returning to one is refused rather than quietly not done,
    /// which would keep the writes the caller asked to discard.
    [Fact]
    public void SavepointsAreMarkedAndNotRolledBackTo()
    {
        Assert.Equal("", Translate("SAVE TRANSACTION __ef_savepoint"));
        Assert.Equal("ROLLBACK", Translate("ROLLBACK TRANSACTION"));

        var refused = Assert.Throws<TSqlException>(() => Translate("ROLLBACK TRANSACTION [__ef_savepoint]"));
        Assert.Contains("cannot be honoured", refused.Message);
    }

    [Fact]
    public void ApplicationLocksRenderToNothing()
    {
        Assert.Equal("", Translate("EXEC sp_getapplock @Resource = 'orders:reload', " +
                                   "@LockMode = 'Exclusive', @LockOwner = 'Transaction'"));
        Assert.Equal("", Translate("EXECUTE [dbo].[sp_releaseapplock] 'orders:reload', 'Transaction'"));
    }

    [Theory]
    [InlineData("CREATE TABLE t (a INT)", "unsupported statement")]
    [InlineData("EXEC sp_who", "sp_who is not supported")]
    [InlineData("EXEC ('SELECT 1')", "EXEC of a string")]
    [InlineData("EXEC @rc = sp_getapplock @Resource = 'r'", "EXEC into a variable")]
    [InlineData("EXEC sp_getapplock @Resource = 'r', @Result = @out OUTPUT", "OUTPUT argument")]
    // A lake's tables are the files under it: making or dropping one is not a statement's to do.
    [InlineData("SELECT * INTO staging FROM orders", "not a temporary table")]
    [InlineData("DROP TABLE orders", "not a temporary table")]
    [InlineData("DROP VIEW v", "only DROP TABLE")]
    // A global temporary table is one another connection can see, and there is none to share with.
    [InlineData("SELECT * INTO ##shared FROM orders", "global temporary table")]
    [InlineData("SELECT (SELECT x INTO #t FROM u) FROM v", "statement of its own")]
    [InlineData("SELECT CONVERT(INT, s, 120) FROM t", "takes no style")]
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
        """SELECT "LPA_L1"."OrderID" FROM (SELECT ROW_NUMBER() OVER (ORDER BY "lake"."Orders"."OrderID" NULLS FIRST) AS "__rn", "lake"."Orders"."OrderID" FROM "lake"."Orders") AS "LPA_L1" WHERE "LPA_L1"."__rn" > $p1 LIMIT $p2""")]
    public void RendersWhatAnOrmSends(string tsql, string expected) =>
        Assert.Equal(expected.Trim(), Translate(tsql, "p1", "p2"));

    /// SqlBulkCopy's probe comes as one batch with no semicolons, so a SET option has to stop at
    /// the statement that follows it instead of swallowing it as more of the option.
    [Fact]
    public void ParsesWhatSqlBulkCopySendsFirst()
    {
        var statements = TSqlParser.Parse(
            "select @@trancount; SET FMTONLY ON select * from [dbo].[orders] SET FMTONLY OFF " +
            "exec ..sp_tablecollations_100 N'[dbo].[orders]'");

        Assert.Equal(5, statements.Count);
        Assert.Equal("FMTONLY ON", ((SetOptionStatement)statements[1]).Option);
        Assert.IsType<SelectStatement>(statements[2]);
        Assert.Equal("FMTONLY OFF", ((SetOptionStatement)statements[3]).Option);
        var probe = Assert.IsType<ExecuteStatement>(statements[4]);
        Assert.Equal("sp_tablecollations_100", probe.Procedure.Table.Text);
    }

    /// The INSERT BULK that declares a load: types, collations and hints are read past, the names
    /// and their order are what the bulk stream is landed by.
    [Fact]
    public void ParsesInsertBulk()
    {
        var statements = TSqlParser.Parse(
            "insert bulk [dbo].[orders] ([order_id] Int, [amount] Decimal(10,2), " +
            "[note] NVarChar(max) COLLATE SQL_Latin1_General_CP1_CI_AS) with (TABLOCK, ROWS_PER_BATCH = 3)");

        var bulk = Assert.IsType<InsertBulkStatement>(Assert.Single(statements));
        Assert.Equal(["order_id", "amount", "note"], bulk.Columns.Select(c => c.Text));
    }

    /// The destination comes out of a string literal, so the quoting decides where the parts end
    /// -- a dot inside the brackets is part of the name, not a separator.
    [Fact]
    public void CollationsAnswerSurvivesADottedTableName()
    {
        Assert.Contains("lower('order.lines')", Translate("EXEC ..sp_tablecollations_100 N'[dbo].[order.lines]'"));
    }
}
