using AVADataMapper.Core.Mapping;
using AVADataMapper.Core.Schema;

namespace AVADataMapper.Tests;

public sealed class MapFileTests
{
    [Fact]
    public void A_mapping_reads_and_writes_back_the_same()
    {
        var text = File.ReadAllText(Samples.Path("clinic.dmap"));
        var map = MapFile.Read(text);
        Assert.Equal(7, map.Tables.Count);
        Assert.Equal(Dialect.Sqlite, map.Source.Dialect);
        var patient = map.Table("patient")!;
        Assert.Equal("Deleted = 0", patient.Where);
        var dob = patient.Column("birth_date")!;
        Assert.Equal((ColumnKind.Convert, "DOB", "date", 4, "reject"), (dob.Kind, dob.From, dob.As, dob.Formats!.Count, dob.Unknown));
        Assert.Equal(["PatName"], patient.Column("family_name")!.Reads);
        Assert.Equal(3, map.Table("sex_codes")!.Rows!.Count);
        Assert.Equal(3, map.Decisions.Count);
        var again = MapFile.Read(map.ToJson());
        Assert.Equal(map.ToJson(), again.ToJson());
        Assert.Contains("\"kind\": \"split\"", map.ToJson());
        Assert.Contains("\"dialect\": \"sqlite\"", map.ToJson());
        Assert.DoesNotContain("Password", map.ToJson());
    }

    [Fact]
    public void A_file_that_isnt_a_mapping_says_what_is_wrong()
    {
        Assert.Contains("isn't a mapping file", Assert.Throws<FormatException>(() => MapFile.Read("{\"format\":\"something\"}")).Message);
        Assert.Contains("isn't a mapping file", Assert.Throws<FormatException>(() => MapFile.Read("not json")).Message);
        Assert.Contains("no target", Assert.Throws<FormatException>(() => MapFile.Read("{\"format\":\"avadatamapper.map\",\"tables\":[{\"from\":\"x\"}]}")).Message);
        Assert.Equal(["Deleted", "Active"], Transforms.ExpressionColumns("Deleted = 0 and upper(Active) = 'Y' and Active is not null"));
    }
}
