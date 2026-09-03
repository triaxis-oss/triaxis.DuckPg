# The schema, from a dacpac

`dacpac: app.dacpac` (or `--dacpac`) makes the declared schema authoritative: column names, order and
types, plus the keys and the uniqueness past them, read out of the dacpac directly and without
DacFx. Columns no layer
carries are published as typed `NULL`s, layer columns are cast to the declared type rather than to
whatever inference guessed, and the declared primary key means `--key` is unnecessary. A declared
table no layer carries is published as well — empty, with its declared shape — and is writable like
any other, so the catalog is the schema rather than a reflection of which files turned up. A single
`.dacpac` sitting in a layer directory is used on its own; several is refused at startup — serving
on with no schema would silently change every table's shape — and naming one with `--dacpac` (or
`dacpac:`) settles it.

**Defaults.** A `SqlDefaultConstraint` fills in the column where a row has no value for it, whether it
left the column out or spelled out a null. The expression is T-SQL and goes through the same
[translator](tsql.md) as any statement, so `(getdate())`, `('new')` and `((0))` all mean what
they say. In the
read layers it is evaluated once, when the lake is built — `GETDATE()` is the moment duckpg started
and `NEWID()` one id for the run, since a table scanned twice has to answer the same both times and a
row already in a file never said when it was written — `--derive-ids` below gives every row its own. A written row is stamped as it is written, the
write layer declaring the expression rather than the frozen value; nothing fills that layer in
afterwards, so a row written with an explicit `NULL` stays null. `SUSER_SNAME()`, `USER_NAME()` and
`ORIGINAL_LOGIN()` answer with the session's login name, or with the account duckpg runs as for a
default. A default DuckDB cannot answer at all (`NEWSEQUENTIALID()` and friends) is dropped with a
warning and the column keeps its `NULL`.

**Ids per row.** A declared default is evaluated once when the lake is built, so a column no file
carries holds the same value in every row — honest for `(getdate())`, where a row in a file cannot say
when it was written, and useless for `(newid())`, whose whole point is that no two rows share one.
`--derive-ids` answers those per row instead: the row's key written into the low half of a uuid whose
high half is fixed for the column, so ids come out under a shared prefix with the key counting up
beneath it — the shape `NEWSEQUENTIALID()` has, and the one an index likes.

They are *derived* rather than generated, which is what makes them usable: a view is bound on every
execution, so a generated id would differ on every scan. Derived, the same row answers the same way
twice, after a restart, and to the baseline the shutdown delta is measured against. A row written
while the lake runs is stamped with a real `uuid()` instead, as it always was — it has an identity of
its own, and the row it might shadow is one no read sees.

Only `NEWID()`, and only for a table with a declared key; one without keeps the run's single value and
is named in a warning at startup, since one flag across a mixed lake should leave the odd table out
rather than refuse to start. It costs about 190 ms a million rows on a scan of that column against 1
ms for the frozen value — paid once at build for a [materialized](performance.md) lake, and by every
read for a layered one, which is why it is off unless asked for.

**Uniqueness past the key.** A `UNIQUE` constraint and a unique index both declare that no two rows
share those columns, and both are kept — on a materialized lake, where the table is a table and
DuckDB can hold the rule. A plain, non-unique index declares nothing and is read as nothing. The rule
is dropped for a table the lake publishes without every column it is over — and for one over a column
no layer carries at all, since a declared default is frozen when the lake is built and `(newid())` is
then one id for the whole run rather than one per row. If those values were wanted they would be in
the input, or `--derive-ids` would be on: with it, such a column *is* per row and the rule holds over
it. A partition column
joins it as it joins the key, since rows are only unique within a partition. Two NULLs count as
different here, as they do in PostgreSQL and unlike SQL Server, which allows one such row rather than
many. A layered lake keeps none of it: it publishes views, and only the key is held over the merge.
Where the layers already break a declared rule, the lake says so at startup rather than serving rows
it would go on to refuse.

**References.** A `DELETE` of a row something still points at fails the way SQL Server fails it —
error 547, naming the constraint — rather than quietly leaving an orphan. The check is over the
*merged view*, since a row pointing at this one may live in any layer, and it runs before anything is
hidden. `ON DELETE CASCADE` is performed, as the same delete against the table that pointed and again
against whatever pointed at that, however deep the chain goes; every level still answers for the
references that do *not* cascade, and the count answered for is the target's own, as on SQL Server.
`ON DELETE SET NULL` and `SET DEFAULT` are performed as what they mean — the rows that pointed stay
where they are with the pointing columns emptied, which is an update, so nothing is hidden and nothing
recurses. Where either cannot be performed, the reference is kept as one that refuses instead and the
reason is logged at startup, since orphaning the rows is wrong either way. The insert side is
unchecked, and a reference to columns that are not the table's key is skipped.

**Views** declared in the dacpac are published beside the tables they read, so a report a client
already knows by name is there without being rewritten as a layer. The query goes through the same
translator as a statement a client sends, so a view over `[dbo].[orders]` reads the stacked layers,
and `ISNULL`, `TOP`, joins and a view of a view come with it. Order does not matter — a view that
reads another is made once the other is in. One that fails is named in a warning and left out rather
than stopping the lake, and so is one that reads it or calls a function that was not published, by
the name it reads; so is one whose name a layer already carries as a table: the files win.
Views are read-only.

**A declared scalar function is published as a macro**, so `[dbo].[Doubled]([order_id])` resolves onto
the lake exactly as a table reference does. The body is translated on the tree like any other T-SQL
and cast to the declared return type, so a body DuckDB widens still answers in the type SQL Server
would have. A macro is an expression, so that is the only body that can become one: anything with a
variable, a branch or a second statement is a procedure, and is left undeclared with the reason logged
rather than half-translated. Table-valued functions are not read at all.

**A store-generated key needs a dacpac that declares one.** A column marked `IsIdentity` draws from a
sequence in the write layer, seeded past the highest value the files already hold when the table first
grows a write branch, so a restart asks the files again rather than trusting what a previous process
handed out. That sequence lives in one duckpg process: two of them serving the same write directory
would hand out the same keys. It is the one place a lake invents a value.
