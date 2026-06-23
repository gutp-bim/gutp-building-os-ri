using BuildingOS.ConnectorWorker.Connectors;

namespace BuildingOS.Shared.Test.Infrastructure.ConnectorWorker;

public class IngressIdentityPolicyTest
{
    [Theory]
    [InlineData("GW001")]
    [InlineData(null)]
    [InlineData("")]
    public void NotEnforced_AlwaysAllows(string? trusted)
        => Assert.Equal(IngressIdentityDecision.Allow,
            IngressIdentityPolicy.Check(enforce: false, trustedGatewayId: trusted, frameGatewayId: "GW001"));

    [Fact]
    public void Enforced_Match_Allows()
        => Assert.Equal(IngressIdentityDecision.Allow,
            IngressIdentityPolicy.Check(enforce: true, trustedGatewayId: "GW001", frameGatewayId: "GW001"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Enforced_NoTrustedId_RejectsMissing(string? trusted)
        => Assert.Equal(IngressIdentityDecision.RejectMissingIdentity,
            IngressIdentityPolicy.Check(enforce: true, trustedGatewayId: trusted, frameGatewayId: "GW001"));

    [Fact]
    public void Enforced_Mismatch_RejectsMismatch()
        => Assert.Equal(IngressIdentityDecision.RejectMismatch,
            IngressIdentityPolicy.Check(enforce: true, trustedGatewayId: "GW-ATTACKER", frameGatewayId: "GW001"));

    [Fact]
    public void Enforced_Match_IsCaseSensitive_Ordinal()
        => Assert.Equal(IngressIdentityDecision.RejectMismatch,
            IngressIdentityPolicy.Check(enforce: true, trustedGatewayId: "gw001", frameGatewayId: "GW001"));

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
        => Assert.Equal(expected, IngressIdentityOptions.Parse(raw, null).Enforce);

    [Theory]
    [InlineData(null, "X-Gateway-Id")]
    [InlineData("", "X-Gateway-Id")]
    [InlineData("  ", "X-Gateway-Id")]
    [InlineData("X-Custom-Gw", "X-Custom-Gw")]
    public void OptionsParse_HeaderName(string? raw, string expected)
        => Assert.Equal(expected, IngressIdentityOptions.Parse(null, raw).HeaderName);
}
