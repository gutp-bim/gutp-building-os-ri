using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Text;

namespace BuildingOS.ApiServer.Test;

/// <summary>
/// Tests for BasicAuthenticationMiddleware ensuring credentials are loaded
/// from IConfiguration rather than being hardcoded.
/// </summary>
public class BasicAuthenticationMiddlewareTest
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static IConfiguration Config(string? user = null, string? password = null)
    {
        var dict = new Dictionary<string, string?>();
        if (user != null) dict["SWAGGER_BASIC_AUTH_USER"] = user;
        if (password != null) dict["SWAGGER_BASIC_AUTH_PASSWORD"] = password;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static string BasicHeader(string user, string password)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        return $"Basic {encoded}";
    }

    private static (DefaultHttpContext ctx, bool nextCalled) BuildContext(
        string path = "/swagger",
        string? authHeader = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = new PathString(path);
        ctx.Response.Body = new MemoryStream();
        if (authHeader != null)
            ctx.Request.Headers["Authorization"] = authHeader;

        return (ctx, false);
    }

    private static BasicAuthenticationMiddleware BuildSut(
        IConfiguration config,
        out bool nextInvoked)
    {
        var invoked = false;
        nextInvoked = false;
        RequestDelegate next = _ => { invoked = true; return Task.CompletedTask; };
        // capture by ref trick: use a wrapper
        var sut = new BasicAuthenticationMiddleware(
            _ => { invoked = true; return Task.CompletedTask; },
            config);
        _ = invoked; // suppress unused warning
        return sut;
    }

    // ── Tests: credentials not configured ─────────────────────────────────

    [Fact]
    public async Task SwaggerPath_NoCredentialsConfigured_PassesThrough()
    {
        // When SWAGGER_BASIC_AUTH_PASSWORD is not set, Swagger is open (dev mode).
        var config = Config(); // neither user nor password set
        var nextCalled = false;
        var sut = new BasicAuthenticationMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            config);

        var (ctx, _) = BuildContext("/swagger");
        await sut.InvokeAsync(ctx);

        Assert.True(nextCalled);
        Assert.NotEqual(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ApiDocsPath_NoCredentialsConfigured_PassesThrough()
    {
        var config = Config();
        var nextCalled = false;
        var sut = new BasicAuthenticationMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            config);

        var (ctx, _) = BuildContext("/api-docs");
        await sut.InvokeAsync(ctx);

        Assert.True(nextCalled);
        Assert.NotEqual(401, ctx.Response.StatusCode);
    }

    // ── Tests: credentials configured, correct auth ────────────────────────

    [Fact]
    public async Task SwaggerPath_CorrectCredentials_PassesThrough()
    {
        var config = Config(user: "testuser", password: "s3cr3t!");
        var nextCalled = false;
        var sut = new BasicAuthenticationMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            config);

        var (ctx, _) = BuildContext("/swagger", BasicHeader("testuser", "s3cr3t!"));
        await sut.InvokeAsync(ctx);

        Assert.True(nextCalled);
        Assert.NotEqual(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task SwaggerSubPath_CorrectCredentials_PassesThrough()
    {
        var config = Config(user: "u", password: "p");
        var nextCalled = false;
        var sut = new BasicAuthenticationMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            config);

        var (ctx, _) = BuildContext("/swagger/index.html", BasicHeader("u", "p"));
        await sut.InvokeAsync(ctx);

        Assert.True(nextCalled);
    }

    // ── Tests: credentials configured, wrong/missing auth ─────────────────

    [Fact]
    public async Task SwaggerPath_CredentialsConfigured_NoAuthHeader_Returns401()
    {
        var config = Config(user: "testuser", password: "s3cr3t!");
        var nextCalled = false;
        var sut = new BasicAuthenticationMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            config);

        var (ctx, _) = BuildContext("/swagger"); // no Authorization header
        await sut.InvokeAsync(ctx);

        Assert.False(nextCalled);
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task SwaggerPath_CredentialsConfigured_WrongPassword_Returns401()
    {
        var config = Config(user: "testuser", password: "s3cr3t!");
        var nextCalled = false;
        var sut = new BasicAuthenticationMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            config);

        var (ctx, _) = BuildContext("/swagger", BasicHeader("testuser", "wrong"));
        await sut.InvokeAsync(ctx);

        Assert.False(nextCalled);
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task SwaggerPath_CredentialsConfigured_WrongUser_Returns401()
    {
        var config = Config(user: "testuser", password: "s3cr3t!");
        var nextCalled = false;
        var sut = new BasicAuthenticationMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            config);

        var (ctx, _) = BuildContext("/swagger", BasicHeader("otheruser", "s3cr3t!"));
        await sut.InvokeAsync(ctx);

        Assert.False(nextCalled);
        Assert.Equal(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task SwaggerPath_Returns401_WithWwwAuthenticateHeader()
    {
        var config = Config(user: "u", password: "p");
        var sut = new BasicAuthenticationMiddleware(
            _ => Task.CompletedTask,
            config);

        var (ctx, _) = BuildContext("/swagger"); // no auth
        await sut.InvokeAsync(ctx);

        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.Contains("Basic", ctx.Response.Headers["WWW-Authenticate"].ToString());
    }

    // ── Tests: non-swagger paths always pass through ───────────────────────

    [Fact]
    public async Task NonSwaggerPath_CredentialsConfigured_AlwaysPassesThrough()
    {
        var config = Config(user: "u", password: "p");
        var nextCalled = false;
        var sut = new BasicAuthenticationMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            config);

        var (ctx, _) = BuildContext("/api/buildings"); // not a swagger path
        await sut.InvokeAsync(ctx);

        Assert.True(nextCalled);
        Assert.NotEqual(401, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task HealthPath_CredentialsConfigured_AlwaysPassesThrough()
    {
        var config = Config(user: "u", password: "p");
        var nextCalled = false;
        var sut = new BasicAuthenticationMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            config);

        var (ctx, _) = BuildContext("/health");
        await sut.InvokeAsync(ctx);

        Assert.True(nextCalled);
    }

    // ── Tests: password-only configuration (user defaults to "building-os") ──

    [Fact]
    public async Task PasswordOnlyConfigured_DefaultUserApplied_CorrectAuth_PassesThrough()
    {
        // User defaults to "building-os" when only password is configured.
        var config = Config(password: "mysecret");
        var nextCalled = false;
        var sut = new BasicAuthenticationMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            config);

        var (ctx, _) = BuildContext("/swagger", BasicHeader("building-os", "mysecret"));
        await sut.InvokeAsync(ctx);

        Assert.True(nextCalled);
    }
}
