using BuildingOS.Shared.Infrastructure.Monitoring;

namespace BuildingOS.Shared.Test.Infrastructure.Monitoring;

public class ServiceHealthTargetTest
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseList_ReturnsEmpty_ForBlankInput(string? raw)
        => Assert.Empty(ServiceHealthTarget.ParseList(raw));

    [Fact]
    public void ParseList_ParsesNameUrlPairs()
    {
        var targets = ServiceHealthTarget.ParseList(
            "nats=http://nats:8222/healthz,minio=http://minio:9000/minio/health/live");

        Assert.Equal(2, targets.Count);
        Assert.Equal("nats", targets[0].Name);
        Assert.Equal("http://nats:8222/healthz", targets[0].Url);
        Assert.Equal("minio", targets[1].Name);
        Assert.Equal("http://minio:9000/minio/health/live", targets[1].Url);
    }

    [Fact]
    public void ParseList_TrimsWhitespaceAroundEntries()
    {
        // The compose YAML folds the value across lines, yielding spaces after commas.
        var targets = ServiceHealthTarget.ParseList(" nats=http://nats:8222/healthz ,  minio=http://minio:9000/live ");

        Assert.Equal(2, targets.Count);
        Assert.Equal("nats", targets[0].Name);
        Assert.Equal("http://minio:9000/live", targets[1].Url);
    }

    [Theory]
    [InlineData("noequalsign")]
    [InlineData("=http://no-name")]
    [InlineData("name-only=")]
    public void ParseList_SkipsMalformedEntries(string raw)
        => Assert.Empty(ServiceHealthTarget.ParseList(raw));
}
