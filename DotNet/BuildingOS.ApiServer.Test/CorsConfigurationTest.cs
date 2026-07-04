using BuildingOs.ApiServer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BuildingOS.ApiServer.Test;

/// <summary>
/// Tests for CORS policy configuration: verifies that CORS_ALLOWED_ORIGINS
/// drives origin restriction rather than always allowing any origin.
/// </summary>
public class CorsConfigurationTest
{
    private static CorsPolicy GetPolicy(string? allowedOrigins)
    {
        var dict = new Dictionary<string, string?>();
        if (allowedOrigins != null) dict["CORS_ALLOWED_ORIGINS"] = allowedOrigins;
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();

        var services = new ServiceCollection();
        services.AddCorsForAll(config);
        var sp = services.BuildServiceProvider();

        var corsOptions = sp.GetRequiredService<IOptions<CorsOptions>>().Value;
        return corsOptions.GetPolicy("_myAllowSpecificOrigins")!;
    }

    [Fact]
    public void NoOriginsConfigured_PolicyAllowsAnyOrigin()
    {
        var policy = GetPolicy(null);

        Assert.NotNull(policy);
        Assert.True(policy.AllowAnyOrigin);
    }

    [Fact]
    public void EmptyOriginsConfigured_PolicyAllowsAnyOrigin()
    {
        var policy = GetPolicy("");

        Assert.NotNull(policy);
        Assert.True(policy.AllowAnyOrigin);
    }

    [Fact]
    public void SingleOriginConfigured_PolicyRestrictsToThatOrigin()
    {
        var policy = GetPolicy("https://app.example.com");

        Assert.NotNull(policy);
        Assert.False(policy.AllowAnyOrigin);
        Assert.Contains("https://app.example.com", policy.Origins);
    }

    [Fact]
    public void MultipleOriginsConfigured_PolicyRestrictsToAllOfThem()
    {
        var policy = GetPolicy("https://a.example.com,https://b.example.com");

        Assert.NotNull(policy);
        Assert.False(policy.AllowAnyOrigin);
        Assert.Contains("https://a.example.com", policy.Origins);
        Assert.Contains("https://b.example.com", policy.Origins);
    }

    [Fact]
    public void OriginsWithWhitespace_AreTrimedCorrectly()
    {
        var policy = GetPolicy("  https://a.example.com , https://b.example.com  ");

        Assert.NotNull(policy);
        Assert.False(policy.AllowAnyOrigin);
        Assert.Contains("https://a.example.com", policy.Origins);
        Assert.Contains("https://b.example.com", policy.Origins);
    }

    [Fact]
    public void PolicyAlwaysAllowsAnyMethod()
    {
        var policy = GetPolicy("https://a.example.com");
        Assert.True(policy.AllowAnyMethod);
    }

    [Fact]
    public void PolicyAlwaysAllowsAnyHeader()
    {
        var policy = GetPolicy("https://a.example.com");
        Assert.True(policy.AllowAnyHeader);
    }
}
