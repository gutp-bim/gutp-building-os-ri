using System.Security.Cryptography;
using System.Text;

public class BasicAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string? _username;
    private readonly string? _password;

    public BasicAuthenticationMiddleware(
        RequestDelegate next,
        IConfiguration configuration)
    {
        _next = next;
        _password = configuration["SWAGGER_BASIC_AUTH_PASSWORD"];
        _username = configuration["SWAGGER_BASIC_AUTH_USER"] ?? "building-os";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api-docs") ||
            context.Request.Path.StartsWithSegments("/swagger"))
        {
            // When no password is configured, Swagger is open (development mode).
            if (string.IsNullOrEmpty(_password))
            {
                await _next(context);
                return;
            }

            string? authHeader = context.Request.Headers["Authorization"];
            if (authHeader != null && authHeader.StartsWith("Basic "))
            {
                var encodedCredentials = authHeader.Substring("Basic ".Length).Trim();
                var credentials = Encoding.UTF8.GetString(
                    Convert.FromBase64String(encodedCredentials));
                var parts = credentials.Split(':', 2);

                if (parts.Length == 2 &&
                    IsEqual(parts[0], _username) &&
                    IsEqual(parts[1], _password))
                {
                    await _next(context);
                    return;
                }
            }

            context.Response.StatusCode = 401;
            context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"API Documentation\"";
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        await _next(context);
    }

    // Timing-safe string comparison to resist timing attacks.
    private static bool IsEqual(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}