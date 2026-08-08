using BuildingOS.ConnectorWorker.Connectors;

namespace BuildingOS.Shared.Test.Infrastructure.ConnectorWorker;

public class IngressHierarchyPolicyTest
{
    [Theory]
    [InlineData(true, "DEV001")]
    [InlineData(false, "DEV001")]
    [InlineData(true, "")]
    [InlineData(false, null)]
    public void NotEnforced_AlwaysAllows(bool hasBuildingPath, string? deviceId)
        => Assert.Equal(IngressHierarchyDecision.Allow,
            IngressHierarchyPolicy.Check(enforce: false, hasBuildingPath, deviceId));

    [Fact]
    public void Enforced_BuildingPathAndDevice_Allows()
        => Assert.Equal(IngressHierarchyDecision.Allow,
            IngressHierarchyPolicy.Check(enforce: true, hasBuildingPath: true, deviceId: "DEV001"));

    [Fact]
    public void Enforced_NoBuildingPath_RejectsNoBuildingPath()
        => Assert.Equal(IngressHierarchyDecision.RejectNoBuildingPath,
            IngressHierarchyPolicy.Check(enforce: true, hasBuildingPath: false, deviceId: "DEV001"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Enforced_NoDevice_RejectsNoDeviceLink(string? deviceId)
        => Assert.Equal(IngressHierarchyDecision.RejectNoDeviceLink,
            IngressHierarchyPolicy.Check(enforce: true, hasBuildingPath: true, deviceId));

    [Fact]
    public void Enforced_NeitherLink_ReportsTheOutermostBreak()
        => Assert.Equal(IngressHierarchyDecision.RejectNoBuildingPath,
            IngressHierarchyPolicy.Check(enforce: true, hasBuildingPath: false, deviceId: ""));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("nope", false)]
    public void OptionsParse_Enforce(string? raw, bool expected)
        => Assert.Equal(expected, IngressHierarchyOptions.Parse(raw).Enforce);
}
