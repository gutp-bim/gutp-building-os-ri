using BuildingOS.Shared;
using BuildingOs.ApiServer.GatewayProvisioning;

namespace BuildingOS.ApiServer.Test.GatewayProvisioning;

/// <summary>Pure tests for the gateway point-list diff (#224/diff): added / removed / changed.</summary>
public class PointListDifferTest
{
    private static GatewayPointEntry P(string id, string? unit = null) =>
        new() { PointId = id, Unit = unit };

    [Fact]
    public void Diff_DetectsAdded()
    {
        var prev = new[] { P("PT001") };
        var curr = new[] { P("PT001"), P("PT002") };

        var diff = PointListDiffer.Diff(prev, curr);
        Assert.Equal(["PT002"], diff.Added.Select(e => e.PointId));
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void Diff_DetectsRemoved()
    {
        var prev = new[] { P("PT001"), P("PT002") };
        var curr = new[] { P("PT001") };

        var diff = PointListDiffer.Diff(prev, curr);
        Assert.Equal(["PT002"], diff.Removed);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void Diff_DetectsChanged_BySameIdDifferentContent()
    {
        var prev = new[] { P("PT001", unit: "C") };
        var curr = new[] { P("PT001", unit: "F") };

        var diff = PointListDiffer.Diff(prev, curr);
        Assert.Equal(["PT001"], diff.Changed.Select(e => e.PointId));
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
    }

    [Fact]
    public void Diff_EmptyWhenIdentical()
    {
        var prev = new[] { P("PT001", unit: "C"), P("PT002") };
        var curr = new[] { P("PT002"), P("PT001", unit: "C") }; // order-independent

        var diff = PointListDiffer.Diff(prev, curr);
        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        Assert.Empty(diff.Changed);
        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void Diff_HasChanges_TrueWhenAnyDelta()
    {
        var diff = PointListDiffer.Diff([], new[] { P("PT001") });
        Assert.True(diff.HasChanges);
    }
}
