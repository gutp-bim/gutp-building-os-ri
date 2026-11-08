using BuildingOS.Shared.Domain.TwinAdmin;
using BuildingOS.Shared.Infrastructure.OxiGraph;

namespace BuildingOS.Shared.Test.Infrastructure.OxiGraph;

/// <summary>#637: which bos:feedbackPoint declarations the import refuses.</summary>
public class FeedbackPointIssueDetectionTest
{
    private static Dictionary<string, string> Row(
        string pt, string target, bool isPoint = true, string? id = "FB", string? targetGw = "GW1", string? gw = "GW1")
    {
        var r = new Dictionary<string, string> { ["pt"] = pt, ["target"] = target };
        if (isPoint) r["targetIsPoint"] = "true";
        if (id is not null) r["targetId"] = id;
        if (targetGw is not null) r["targetGw"] = targetGw;
        if (gw is not null) r["gw"] = gw;
        return r;
    }

    private static List<TwinFeedbackPointIssue> Classify(params Dictionary<string, string>[] rows) =>
        FeedbackPointIssueDetection.Classify(rows, cap: 100).Issues;

    [Fact]
    public void ASoundDeclaration_AndASelfReference_AreFine()
    {
        Assert.Empty(Classify(Row("urn:sp", "urn:pv"), Row("urn:self", "urn:self")));
    }

    [Theory]
    [InlineData(false, "FB", "GW1", FeedbackPointIssueReasons.TargetMissing)]
    [InlineData(true, null, "GW1", FeedbackPointIssueReasons.TargetNoId)]
    [InlineData(true, "FB", "GW2", FeedbackPointIssueReasons.TargetOtherGateway)]
    [InlineData(true, "FB", null, FeedbackPointIssueReasons.TargetOtherGateway)]
    public void BrokenTargets_AreReported(bool isPoint, string? id, string? targetGw, string reason)
    {
        Assert.Equal(reason, Classify(Row("urn:sp", "urn:pv", isPoint, id, targetGw)).Single().Reason);
    }

    [Fact]
    public void SeveralFeedbackPoints_AreOneIssue()
    {
        var issues = Classify(Row("urn:sp", "urn:a"), Row("urn:sp", "urn:b"));
        Assert.Equal(FeedbackPointIssueReasons.MultipleTargets, issues.Single().Reason);
    }

    [Fact]
    public void AControlledPointWithoutAGateway_IsNotJudgedOnGateway()
    {
        Assert.Empty(Classify(Row("urn:sp", "urn:pv", gw: null, targetGw: null)));
    }
}
