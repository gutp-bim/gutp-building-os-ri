using BuildingOS.DuckDbSpike;
using Xunit;

namespace BuildingOS.DuckDbSpike.Test;

public class DuckDbQueryBuilderTest
{
    // --- BuildS3Config ---

    [Fact]
    public void BuildS3Config_ContainsEndpointAndCredentials()
    {
        var sql = DuckDbQueryBuilder.BuildS3Config("minio:9000", "mykey", "mysecret");

        Assert.Contains("minio:9000", sql);
        Assert.Contains("mykey", sql);
        Assert.Contains("mysecret", sql);
        Assert.Contains("CREATE OR REPLACE SECRET", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildS3Config_SetsPathStyleAndNoTls()
    {
        var sql = DuckDbQueryBuilder.BuildS3Config("minio:9000", "k", "s");

        Assert.Contains("path", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("USE_SSL true", sql, StringComparison.OrdinalIgnoreCase);
    }

    // --- BuildPointQuery ---

    [Fact]
    public void BuildPointQuery_ContainsPointIdFilter()
    {
        var start = new DateTime(2025, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var end   = new DateTime(2025, 11, 1, 10, 59, 59, DateTimeKind.Utc);

        var sql = DuckDbQueryBuilder.BuildPointQuery("lake", "B01", start, end, "P001");

        Assert.Contains("P001", sql);
        Assert.Contains("point_id", sql);
    }

    [Fact]
    public void BuildPointQuery_ContainsExpectedHourPrefix()
    {
        var start = new DateTime(2025, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var end   = new DateTime(2025, 11, 1, 10, 59, 59, DateTimeKind.Utc);

        var sql = DuckDbQueryBuilder.BuildPointQuery("lake", "B01", start, end, "P001");

        Assert.Contains("building_id=B01", sql);
        Assert.Contains("year=2025", sql);
        Assert.Contains("month=11", sql);
        Assert.Contains("day=01", sql);
        Assert.Contains("hour=10", sql);
    }

    [Fact]
    public void BuildPointQuery_MultiHourRange_IncludesAllHours()
    {
        var start = new DateTime(2025, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var end   = new DateTime(2025, 11, 1, 12, 30, 0, DateTimeKind.Utc);

        var sql = DuckDbQueryBuilder.BuildPointQuery("lake", "B01", start, end, "P001");

        Assert.Contains("hour=10", sql);
        Assert.Contains("hour=11", sql);
        Assert.Contains("hour=12", sql);
    }

    [Fact]
    public void BuildPointQuery_IncludesDatetimeFilter()
    {
        var start = new DateTime(2025, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var end   = new DateTime(2025, 11, 1, 10, 59, 59, DateTimeKind.Utc);

        var sql = DuckDbQueryBuilder.BuildPointQuery("lake", "B01", start, end, "P001");

        Assert.Contains("datetime", sql);
    }

    [Fact]
    public void BuildPointQuery_UsesBucketName()
    {
        var start = new DateTime(2025, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var end   = start.AddHours(1);

        var sql = DuckDbQueryBuilder.BuildPointQuery("mybucket", "B01", start, end, "P001");

        Assert.Contains("s3://mybucket/", sql);
    }

    // --- BuildLatestQuery ---

    [Fact]
    public void BuildLatestQuery_ContainsOrderByDescAndLimit()
    {
        var since = new DateTime(2025, 11, 1, 0, 0, 0, DateTimeKind.Utc);

        var sql = DuckDbQueryBuilder.BuildLatestQuery("lake", "B01", since, "P001");

        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DESC", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT 1", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("P001", sql);
    }
}
