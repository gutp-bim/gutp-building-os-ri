using BuildingOS.Shared.Infrastructure.ControlRouting;

namespace BuildingOS.Shared.Test.Infrastructure.ControlRouting;

public class GatewaySettingsMaskerTest
{
    [Theory]
    [InlineData("password", true)]
    [InlineData("clientSecret", true)]
    [InlineData("iotHubConnectionString", false)]
    [InlineData("credentialsRef", true)]
    [InlineData("apiKey", true)]
    [InlineData("accessToken", true)]
    [InlineData("host", false)]
    [InlineData("port", false)]
    [InlineData("tenant", false)]
    public void IsSecretKey_FlagsSecretishKeys(string key, bool expected)
    {
        Assert.Equal(expected, GatewaySettingsMasker.IsSecretKey(key));
    }

    [Fact]
    public void Mask_ReplacesSecretValues_KeepsNonSecret()
    {
        var settings = new Dictionary<string, string>
        {
            ["host"] = "broker.example.com",
            ["port"] = "5671",
            ["password"] = "s3cr3t",
            ["tenant"] = "building-os",
        };

        var masked = GatewaySettingsMasker.Mask(settings);

        Assert.Equal("broker.example.com", masked["host"]);
        Assert.Equal("5671", masked["port"]);
        Assert.Equal("building-os", masked["tenant"]);
        Assert.Equal("***", masked["password"]);
    }

    [Fact]
    public void Mask_EmptySecret_StaysEmpty()
    {
        var masked = GatewaySettingsMasker.Mask(new Dictionary<string, string> { ["password"] = "" });
        Assert.Equal("", masked["password"]);
    }
}
