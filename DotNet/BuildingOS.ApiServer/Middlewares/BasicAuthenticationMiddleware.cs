using System.Security.Cryptography;
using System.Text;

public class BasicAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _username;
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
            var password = _password;
            if (string.IsNullOrEmpty(password))
            {
                await _next(context);
                return;
            }

            string? authHeader = context.Request.Headers["Authorization"];
            if (authHeader != null && authHeader.StartsWith("Basic "))
            {
                var encodedCredentials = authHeader.Substring("Basic ".Length).Trim();
                try
                {
                    var credentials = Encoding.UTF8.GetString(
                        Convert.FromBase64String(encodedCredentials));
                    var parts = credentials.Split(':', 2);

                    // Evaluate both comparisons unconditionally to prevent username
                    // enumeration via timing (short-circuit && would leak correctness).
                    var usernameOk = IsEqual(parts[0], _username);
                    var passwordOk = IsEqual(parts[1], password);
                    if (parts.Length == 2 & usernameOk & passwordOk)
                    {
                        await _next(context);
                        return;
                    }
                }
                catch (FormatException)
                {
                    // Malformed base64 — fall through to 401.
                }
            }

            context.Response.StatusCode = 401;
            context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"API Documentation\"";
            await context.Response.WriteAsync("Unauthorized", context.RequestAborted);
            return;
        }

        await _next(context);
    }

    // Timing-safe string comparison. Pads to equal length so FixedTimeEquals
    // does not short-circuit on length mismatch, which would leak password byte-length.
    private static bool IsEqual(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        var maxLen = Math.Max(aBytes.Length, bBytes.Length);
        var aPadded = new byte[maxLen];
        var bPadded = new byte[maxLen];
        Buffer.BlockCopy(aBytes, 0, aPadded, 0, aBytes.Length);
        Buffer.BlockCopy(bBytes, 0, bPadded, 0, bBytes.Length);
        // Bitwise & (no short-circuit) so length check runs unconditionally,
        // preserving constant-time behaviour for inputs of differing length.
        return CryptographicOperations.FixedTimeEquals(aPadded, bPadded)
               & (aBytes.Length == bBytes.Length);
    }
}