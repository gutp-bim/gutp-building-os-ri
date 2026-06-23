using BuildingOs.ApiServer.GatewayProvisioning;
using Microsoft.AspNetCore.Http;

namespace BuildingOS.ApiServer.Test.GatewayProvisioning;

public class HeaderGatewayIdentityResolverTest
{
    private static IHeaderDictionary Headers(string? name, string? value)
    {
        var h = new HeaderDictionary();
        if (name is not null && value is not null) h[name] = value;
        return h;
    }

    [Fact]
    public void Resolves_FromDefaultHeader()
    {
        var sut = new HeaderGatewayIdentityResolver();
        Assert.Equal("GW001", sut.ResolveGatewayId(Headers("X-Gateway-Id", "GW001")));
    }

    [Fact]
    public void Trims_Whitespace()
    {
        var sut = new HeaderGatewayIdentityResolver();
        Assert.Equal("GW001", sut.ResolveGatewayId(Headers("X-Gateway-Id", "  GW001  ")));
    }

    [Fact]
    public void ReturnsNull_WhenHeaderAbsent()
    {
        var sut = new HeaderGatewayIdentityResolver();
        Assert.Null(sut.ResolveGatewayId(Headers(null, null)));
    }

    [Fact]
    public void ReturnsNull_WhenHeaderBlank()
    {
        var sut = new HeaderGatewayIdentityResolver();
        Assert.Null(sut.ResolveGatewayId(Headers("X-Gateway-Id", "   ")));
    }

    [Fact]
    public void Honors_CustomHeaderName()
    {
        var sut = new HeaderGatewayIdentityResolver("X-Edge-Gw");
        Assert.Equal("GW9", sut.ResolveGatewayId(Headers("X-Edge-Gw", "GW9")));
        Assert.Null(sut.ResolveGatewayId(Headers("X-Gateway-Id", "GW9")));
    }
}
