using System.Security.Cryptography;
using System.Text;

public class BasicAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _username;
    private readonly string? _password;
    private readonly bool _isDevelopment;

    public BasicAuthenticationMiddleware(
        RequestDelegate next,
        IConfiguration configuration)
    {
        _next = next;
        _password = configuration["SWAGGER_BASIC_AUTH_PASSWORD"];
        _username = configuration["SWAGGER_BASIC_AUTH_USER"] ?? "building-os";
        _isDevelopment = string.Equals(
            configuration["ASPNETCORE_ENVIRONMENT"], "Development",
            StringComparison.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api-docs") ||
            context.Request.Path.StartsWithSegments("/swagger"))
        {
            var password = _password;
            if (string.IsNullOrEmpty(password))
            {
                // No password configured: open only in Development; deny in all other environments.
                if (_isDevelopment)
                {
                    await _next(context);
                    return;
                }
            }
            else
            {
                string? authHeader = context.Request.Headers["Authorization"];
                // RFC 7235: authentication scheme names are case-insensitive.
                if (authHeader != null && authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                {
                    var encodedCredentials = authHeader.Substring("Basic ".Length).Trim();
                    try
                    {
                        var credentials = Encoding.UTF8.GetString(
                            Convert.FromBase64String(encodedCredentials));
                        var parts = credentials.Split(':', 2);

                        // Guard length first (attacker controls format, not the secret values).
                        // Then evaluate both IsEqual calls unconditionally with non-short-circuit &
                        // so neither username nor password correctness leaks via timing.
                        if (parts.Length == 2)
                        {
                            var usernameOk = IsEqual(parts[0], _username);
                            var passwordOk = IsEqual(parts[1], password);
                            if (usernameOk & passwordOk)
                            {
                                await _next(context);
                                return;
                            }
                        }
                    }
                    catch (FormatException)
                    {
                        // Malformed base64 — fall through to 401.
                    }
                }
            }

            context.Response.StatusCode = 401;
            context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"API Documentation\"";
            await context.Response.WriteAsync("Unauthorized", context.RequestAborted);
            return;
        }

        await _next(context);
    }

    // Timing-safe string comparison. Always compares bBytes.Length bytes so the
    // execution time is proportional to the stored credential length (b), not the
    // attacker-supplied input (a). Without this, Math.Max would make the timing
    // proportional to max(a,b) — leaking stored credential byte-length when a < b.
    private static bool IsEqual(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        // Pad a to b's length (the stored credential). Time is constant w.r.t. a.
        var aPadded = new byte[bBytes.Length];
        Buffer.BlockCopy(aBytes, 0, aPadded, 0, Math.Min(aBytes.Length, bBytes.Length));
        // Bitwise & (no short-circuit) so the length equality check runs unconditionally,
        // preventing a short-circuit path that would distinguish "wrong length" from "wrong bytes".
        return CryptographicOperations.FixedTimeEquals(aPadded, bBytes)
               & (aBytes.Length == bBytes.Length);
    }
}
