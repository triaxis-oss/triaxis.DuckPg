using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;

namespace triaxis.DuckPg.Tests;

/// The reader is pinned to a `model.xml` shaped like a SqlPackage-built dacpac's rather than like
/// one this project writes: a fixture built by the same understanding as the parser agrees with it
/// whether or not either is right, and a property DacFx spells differently is then invisible.
public class DacpacFormatTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "duckpg-tests", $"format-{Guid.NewGuid():N}");
    readonly Warnings warnings = new();

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    /// The structure is verbatim, down to the attributes the reader ignores -- an `ExternalSource`
    /// on a built-in type reference, an element name on a constraint, the CDATA a script is wrapped
    /// in. Only the names are this suite's own, since a name is the one thing the reader does not
    /// have an opinion about.
    const string Model = """
        <?xml version="1.0" encoding="utf-8"?>
        <DataSchemaModel FileFormatVersion="1.2" SchemaVersion="3.5"
                         DspName="Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider"
                         xmlns="http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02">
          <Model>
            <Element Type="SqlTable" Name="[dbo].[Orders]">
              <Relationship Name="Columns">
                <Entry>
                  <Element Type="SqlSimpleColumn" Name="[dbo].[Orders].[Id]">
                    <Property Name="IsNullable" Value="False" />
                    <Relationship Name="TypeSpecifier">
                      <Entry>
                        <Element Type="SqlTypeSpecifier">
                          <Relationship Name="Type">
                            <Entry><References ExternalSource="BuiltIns" Name="[int]" /></Entry>
                          </Relationship>
                        </Element>
                      </Entry>
                    </Relationship>
                  </Element>
                </Entry>
                <Entry>
                  <Element Type="SqlSimpleColumn" Name="[dbo].[Orders].[Created]">
                    <Relationship Name="TypeSpecifier">
                      <Entry>
                        <Element Type="SqlTypeSpecifier">
                          <Relationship Name="Type">
                            <Entry><References ExternalSource="BuiltIns" Name="[datetime]" /></Entry>
                          </Relationship>
                        </Element>
                      </Entry>
                    </Relationship>
                  </Element>
                </Entry>
                <Entry>
                  <Element Type="SqlSimpleColumn" Name="[dbo].[Orders].[CreatedUser]">
                    <Relationship Name="TypeSpecifier">
                      <Entry>
                        <Element Type="SqlTypeSpecifier">
                          <Property Name="Length" Value="50" />
                          <Relationship Name="Type">
                            <Entry><References ExternalSource="BuiltIns" Name="[nvarchar]" /></Entry>
                          </Relationship>
                        </Element>
                      </Entry>
                    </Relationship>
                  </Element>
                </Entry>
              </Relationship>
              <Relationship Name="Schema">
                <Entry><References Name="[dbo]" /></Entry>
              </Relationship>
            </Element>
            <Element Type="SqlDefaultConstraint" Name="[dbo].[DF_Orders_Created]">
              <Property Name="DefaultExpressionScript">
                <Value><![CDATA[(getdate())]]></Value>
              </Property>
              <Relationship Name="DefiningTable">
                <Entry><References Name="[dbo].[Orders]" /></Entry>
              </Relationship>
              <Relationship Name="ForColumn">
                <Entry><References Name="[dbo].[Orders].[Created]" /></Entry>
              </Relationship>
            </Element>
            <Element Type="SqlDefaultConstraint" Name="[dbo].[DF_Orders_CreatedUser]">
              <Property Name="DefaultExpressionScript">
                <Value><![CDATA[(suser_sname())]]></Value>
              </Property>
              <Relationship Name="DefiningTable">
                <Entry><References Name="[dbo].[Orders]" /></Entry>
              </Relationship>
              <Relationship Name="ForColumn">
                <Entry><References Name="[dbo].[Orders].[CreatedUser]" /></Entry>
              </Relationship>
            </Element>
            <Element Type="SqlPrimaryKeyConstraint" Name="[dbo].[PK_Orders]">
              <Relationship Name="ColumnSpecifications">
                <Entry>
                  <Element Type="SqlIndexedColumnSpecification">
                    <Relationship Name="Column">
                      <Entry><References Name="[dbo].[Orders].[Id]" /></Entry>
                    </Relationship>
                  </Element>
                </Entry>
              </Relationship>
              <Relationship Name="DefiningTable">
                <Entry><References Name="[dbo].[Orders]" /></Entry>
              </Relationship>
            </Element>
          </Model>
        </DataSchemaModel>
        """;

    /// `ExpressionScript` is the spelling this reader once looked for, and no dacpac uses it for a
    /// default. A schema that read the element and made nothing of it counts it, so the difference
    /// between that and a schema declaring no defaults at all is visible to whoever asked.
    [Fact]
    public void ADefaultItCannotReadIsCountedRatherThanPassedOver()
    {
        var schema = Load(Model.Replace("DefaultExpressionScript", "ExpressionScript"));

        Assert.Null(schema.Default("Orders", "Created"));
        Assert.Contains(warnings.Messages, m => m.Contains("2 SqlDefaultConstraint elements"));
        Assert.DoesNotContain(warnings.Messages, m => m.Contains("SqlTable"));
    }

    DacpacSchema Load(string model = Model)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"{Guid.NewGuid():N}.dacpac");

        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var entry = archive.CreateEntry("model.xml").Open())
            entry.Write(Encoding.UTF8.GetBytes(model));

        return new DacpacSchema(new Config { Dacpac = path }, warnings);
    }

    /// Enough of an `ILogger` to read back what the schema complained about.
    sealed class Warnings : ILogger<DacpacSchema>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
                                Func<TState, Exception?, string> message)
        {
            if (level >= LogLevel.Warning) Messages.Add(message(state, error));
        }
    }

    /// The delete actions read off DacFx's own output, not this suite's writer. `Dacpac.cs` and the
    /// reader can agree on a wrong encoding forever -- which is exactly what happened to the name of
    /// the property carrying it -- so the numbers are pinned to the checked-in dacpac SqlPackage
    /// built. NO ACTION cannot be pinned here at all: DacFx omits the property for it, so an
    /// absent one and a property read by the wrong name look exactly alike.
    [Fact]
    public void ReadsTheDeleteActionsDacFxEncodes()
    {
        var schema = new DacpacSchema(
            new Config { Dacpac = Path.Combine(AppContext.BaseDirectory, "Schema", "sample.dacpac") },
            warnings);

        Assert.Equal(["FK_lines_orders=Cascade", "FK_notes_orders=SetNull", "FK_tags_orders=SetDefault"],
                     schema.References.Select(r => $"{r.Name}={r.OnDelete}").Order());
    }

    [Fact]
    public void ReadsTheColumnsAndTheKey()
    {
        var schema = Load();

        Assert.Equal(["Id:INTEGER", "Created:TIMESTAMP", "CreatedUser:VARCHAR"],
            schema.Columns("Orders")!.Select(c => $"{c.Name}:{c.Type}"));
        Assert.Equal(["Id"], schema.Key("Orders"));
    }

    /// The property DacFx writes for a default is `DefaultExpressionScript`; reading anything else
    /// leaves every default in a real dacpac unseen, and nothing downstream can tell.
    [Fact]
    public void ReadsTheDefaultsOffTheNameDacFxWrites()
    {
        var schema = Load();

        Assert.Equal("(getdate())", schema.Default("Orders", "Created"));
        Assert.Equal("(suser_sname())", schema.Default("Orders", "CreatedUser"));
        Assert.Null(schema.Default("Orders", "Id"));
    }
}
