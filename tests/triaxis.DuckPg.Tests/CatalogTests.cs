namespace triaxis.DuckPg.Tests;

/// What a GUI client reads out of the catalog. TablePlus asks for things psql never does -- a view
/// definition by name, the statio views, quote_ident -- and each of them is a question DuckDB
/// answers with an error the client shows the user instead of the schema.
public class CatalogTests : IDisposable
{
    readonly TestLake lake = new TestLake("catalog")
        .Json("base", "orders", """[{"order_id": 1, "note": "one"}]""")
        .Json("base", "lines", """[{"line_id": 10, "order_id": 1, "qty": 2}]""")
        .Stack("base")
        .WriteTo("local");

    public CatalogTests()
    {
        Dacpac.Write(lake.At("schema", "test.dacpac"),
            [new Dacpac.TableModel("orders", [("order_id", "int"), ("note", "nvarchar")], ["order_id"]),
             new Dacpac.TableModel("lines", [("line_id", "int"), ("order_id", "int"), ("qty", "int")], ["line_id"])],
            [],
            [new Dacpac.ReferenceModel("FK_lines_orders", "lines", ["order_id"], "orders", ["order_id"], "Cascade")]);
        lake.Config.Dacpac = lake.At("schema", "test.dacpac");
        lake.Start();
    }

    public void Dispose() => lake.Dispose();

    /// A regclass literal is the relation's name quoted the way the client wrote it, and the second
    /// argument is the pretty flag DuckDB's own macro has no room for -- which is what it is refused
    /// over, before the name is looked at at all.
    [Fact]
    public void AViewDefinitionIsAnsweredByName()
    {
        var answered = lake.Query("""SELECT pg_get_viewdef('"lake"."orders"'::regclass, true)""");
        Assert.Single(answered);
        Assert.Contains("orders", answered[0]);

        Assert.Equal(answered, lake.Query("SELECT pg_get_viewdef('lake.orders')"));
        Assert.Equal(answered, lake.Query(
            "SELECT pg_get_viewdef(oid) FROM pg_catalog.pg_class WHERE relname = 'orders'"));
    }

    [Fact]
    public void AViewNobodyPublishesIsAnsweredWithNothing()
    {
        Assert.Equal([""], lake.Query("""SELECT pg_get_viewdef('"lake"."missing"'::regclass, true)"""));
    }

    /// Only what has to be quoted, since the answer goes in front of a user.
    [Fact]
    public void QuoteIdentQuotesWhatPostgresQuotes()
    {
        Assert.Equal(["lake|\"Orders\"|\"has space\"|\"a\"\"b\""],
            lake.Query("""
                SELECT quote_ident('lake'), quote_ident('Orders'), quote_ident('has space'),
                       quote_ident('a"b')
                """));
    }

    /// Everything TablePlus asks about one relation, as it asks it: the sizes off the statio view
    /// where the name is a table, and a comment out of whichever of the three things by that name
    /// exists. A lake's table is a view, so the third branch is the one that answers.
    [Fact]
    public void TheRelationQueryATablePlusTabSendsIsAnswered()
    {
        Assert.Equal(["|||"], lake.Query("""
            SELECT
              pg_total_relation_size(relid) AS total_size,
              pg_table_size(relid) AS data_size,
              pg_indexes_size(relid) AS index_size,
              obj_description(relid, 'pg_class') AS comment
            FROM pg_catalog.pg_statio_user_tables
            WHERE schemaname = 'lake' AND relname = 'orders'
            UNION
            SELECT NULL, NULL, NULL, obj_description(p.oid, 'pg_proc')
            FROM pg_catalog.pg_proc p
              LEFT JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'lake' AND p.proname = 'orders'
            UNION
            SELECT NULL, NULL, NULL, obj_description(p.oid, 'pg_class')
            FROM pg_catalog.pg_class p
              LEFT JOIN pg_catalog.pg_namespace n ON n.oid = p.relnamespace
            WHERE n.nspname = 'lake' AND p.relname = 'orders' AND p.relkind = 'v'
            """));
    }

    /// The references tab, as TablePlus asks for it: `pg_constraint` unnested against
    /// `pg_attribute`, which DuckDB's own answers with an integer where the list should be. The oid
    /// is the only thing not sent verbatim -- the client has one by then and a test cannot.
    [Fact]
    public void TheForeignKeyQueryATablePlusTabSendsIsAnswered()
    {
        Assert.Equal(["FK_lines_orders|order_id|lake|orders|order_id|no action|cascade"], lake.Query("""
            SELECT c.conname AS constraint_name,
              (SELECT STRING_AGG(QUOTE_IDENT(a.attname), ',' ORDER BY t.seq)
               FROM (SELECT ROW_NUMBER() OVER (ROWS UNBOUNDED PRECEDING) AS seq, attnum
                     FROM UNNEST(c.conkey) AS t(attnum)) AS t
               INNER JOIN pg_attribute AS a ON a.attrelid = c.conrelid AND a.attnum = t.attnum) AS child_column,
              tt.schema AS parent_schema, tt.name AS parent_name,
              (SELECT STRING_AGG(QUOTE_IDENT(a.attname), ',' ORDER BY t.seq)
               FROM (SELECT ROW_NUMBER() OVER (ROWS UNBOUNDED PRECEDING) AS seq, attnum
                     FROM UNNEST(c.confkey) AS t(attnum)) AS t
               INNER JOIN pg_attribute AS a ON a.attrelid = c.confrelid AND a.attnum = t.attnum) AS parent_column,
              CASE confupdtype WHEN 'r' THEN 'restrict' WHEN 'c' THEN 'cascade' WHEN 'n' THEN 'set null'
                               WHEN 'd' THEN 'set default' WHEN 'a' THEN 'no action' ELSE NULL END AS on_update,
              CASE confdeltype WHEN 'r' THEN 'restrict' WHEN 'c' THEN 'cascade' WHEN 'n' THEN 'set null'
                               WHEN 'd' THEN 'set default' WHEN 'a' THEN 'no action' ELSE NULL END AS on_delete
            FROM pg_constraint AS c
            INNER JOIN (SELECT pg_class.oid, QUOTE_IDENT(pg_namespace.nspname) AS schema,
                               QUOTE_IDENT(pg_class.relname) AS name
                        FROM pg_class INNER JOIN pg_namespace ON pg_class.relnamespace = pg_namespace.oid) AS tf
              ON tf.oid = c.conrelid
            INNER JOIN (SELECT pg_class.oid, QUOTE_IDENT(pg_namespace.nspname) AS schema,
                               QUOTE_IDENT(pg_class.relname) AS name
                        FROM pg_class INNER JOIN pg_namespace ON pg_class.relnamespace = pg_namespace.oid) AS tt
              ON tt.oid = c.confrelid
            WHERE tf.oid = (SELECT oid FROM pg_class WHERE relname = 'lines') AND c.contype = 'f'
            """));
    }

    /// A key is a rule over the merged view, and a merged view is a DuckDB view -- which holds no
    /// constraint of its own for anything to read back.
    [Fact]
    public void ADeclaredKeyIsPublishedAsAPrimaryKey()
    {
        Assert.Equal(["PK_lines|line_id"], lake.Query("""
            SELECT c.conname, string_agg(a.attname, ',')
            FROM pg_constraint AS c
            INNER JOIN pg_attribute AS a
              ON a.attrelid = c.conrelid AND list_contains(c.conkey, a.attnum::BIGINT)
            WHERE c.contype = 'p' AND c.conrelid = (SELECT oid FROM pg_class WHERE relname = 'lines')
            GROUP BY 1
            """));
    }

    /// Rebuilt with the tables, since a constraint answers for relations by the number DuckDB gave
    /// their columns -- and a reload is exactly when those stop being the numbers it gave before.
    [Fact]
    public void AReloadRepublishesThemRatherThanDoublingThem()
    {
        Assert.Equal(["3"], lake.Query("SELECT count(*) FROM pg_constraint"));
        lake.Query("CALL duckpg_reload()");
        Assert.Equal(["3"], lake.Query("SELECT count(*) FROM pg_constraint"));
    }

    /// The function list, which TablePlus asks for as soon as it connects -- as it writes it, so the
    /// `pg_catalog.` names go through the shims on the way. DuckDB's `pg_proc` has no `proiswindow`
    /// and it reports a window function as an aggregate, so nothing here is one.
    [Fact]
    public void TheFunctionListATablePlusConnectAsksForIsAnswered()
    {
        var answered = lake.Query("""
            SELECT pg_catalog.pg_get_userbyid(p.proowner) AS owner, p.oid AS oid,
              pg_catalog.pg_get_function_identity_arguments(p.oid) AS args,
              n.nspname AS function_schema, p.proname AS function_name,
              CASE WHEN p.proisagg THEN 'aggregate' WHEN p.proiswindow THEN 'window'
                   WHEN p.prorettype = 'trigger'::pg_catalog.regtype THEN 'trigger'
                   ELSE 'function' END AS function_type
            FROM pg_catalog.pg_proc p
              LEFT JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
            """);

        Assert.NotEmpty(answered);
        Assert.All(answered, row => Assert.StartsWith("duckdb|", row));

        // The arguments are answered for by oid, and a macro body sees the caller's aliases: one
        // that named its own `p` -- as this query does -- would compare a row to itself and answer
        // every function with the same list.
        Assert.Contains(answered, row => row.EndsWith("|VARCHAR||upper|function"));
        Assert.Contains(answered, row => row.EndsWith("|sum|aggregate"));
    }

    /// A materialized lake holds real tables, so it is the one the statio view has rows for -- and
    /// what they are worth on disk is not a question a lake can answer.
    [Fact]
    public void AMaterializedTableIsListedWithoutASizeItCannotKnow()
    {
        lake.Stop();
        lake.Config.Materialize = true;
        lake.Start();

        Assert.Equal(["lake|orders||"], lake.Query("""
            SELECT schemaname, relname, pg_total_relation_size(relid), pg_indexes_size(relid)
            FROM pg_catalog.pg_statio_user_tables WHERE relname = 'orders'
            """));
    }
}
