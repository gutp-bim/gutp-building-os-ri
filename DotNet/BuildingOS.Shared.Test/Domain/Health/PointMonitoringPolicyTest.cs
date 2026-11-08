using BuildingOS.Shared.Domain.Health;

namespace BuildingOS.Shared.Test.Domain.Health;

/// <summary>#635: which points data health judges at all.</summary>
public class PointMonitoringPolicyTest
{
    [Theory]
    [InlineData(false, null, true, null)]
    [InlineData(false, true, true, null)]
    [InlineData(false, false, false, NotMonitoredReason.DeclaredNoTelemetry)]
    [InlineData(true, null, false, NotMonitoredReason.ReleasePoint)]
    // A release point is a release point whatever else it declares.
    [InlineData(true, true, false, NotMonitoredReason.ReleasePoint)]
    [InlineData(true, false, false, NotMonitoredReason.ReleasePoint)]
    public void Resolve(bool isRelease, bool? reports, bool monitored, NotMonitoredReason? reason)
    {
        var m = PointMonitoringPolicy.Resolve(isRelease, reports);

        Assert.Equal(monitored, m.Monitored);
        Assert.Equal(reason, m.Reason);
    }

    [Fact]
    public void Writable_AloneDoesNotMatter()
    {
        var point = new Point { DtId = "d", Id = "P", Name = "AO", Writable = true };

        Assert.Equal(PointMonitoring.Yes, PointMonitoringPolicy.Resolve(point));
    }
}
