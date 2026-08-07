using BuildingOS.ConnectorWorker.Connectors;

namespace BuildingOS.Shared.Test.Infrastructure.ConnectorWorker;

public class IngressHierarchyPolicyTest
{
    [Theory]
    [InlineData("bldg-1", "DEV001")]
    [InlineData("", "DEV001")]
    [InlineData("bldg-1", "")]
    [InlineData(null, null)]
    public void NotEnforced_AlwaysAllows(string? building, string? deviceId)
        => Assert.Equal(IngressHierarchyDecision.Allow,
            IngressHierarchyPolicy.Check(enforce: false, building, deviceId));

    [Fact]
    public void Enforced_BuildingAndDevice_Allows()
        => Assert.Equal(IngressHierarchyDecision.Allow,
            IngressHierarchyPolicy.Check(enforce: true, building: "bldg-1", deviceId: "DEV001"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Enforced_NoBuilding_RejectsNoBuildingPath(string? building)
        => Assert.Equal(IngressHierarchyDecision.RejectNoBuildingPath,
            IngressHierarchyPolicy.Check(enforce: true, building, deviceId: "DEV001"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Enforced_NoDevice_RejectsNoDeviceLink(string? deviceId)
        => Assert.Equal(IngressHierarchyDecision.RejectNoDeviceLink,
            IngressHierarchyPolicy.Check(enforce: true, building: "bldg-1", deviceId));

    [Fact]
    public void Enforced_NeitherLink_ReportsTheOutermostBreak()
        => Assert.Equal(IngressHierarchyDecision.RejectNoBuildingPath,
            IngressHierarchyPolicy.Check(enforce: true, building: "", deviceId: ""));

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
