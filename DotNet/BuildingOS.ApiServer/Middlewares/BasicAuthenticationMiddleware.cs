using System.Text;

public class BasicAuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _username;
    private readonly string _password;

    public BasicAuthenticationMiddleware(
        RequestDelegate next,
        IConfiguration configuration)
    {
        _next = next;
        _username = "building-os";
        _password = "oP*4yzbN8jE7";
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Redocページのパスをチェック
        if (context.Request.Path.StartsWithSegments("/api-docs") || context.Request.Path.StartsWithSegments("/swagger"))
        {
            string authHeader = context.Request.Headers["Authorization"];
            
            if (authHeader != null && authHeader.StartsWith("Basic "))
            {
                var encodedCredentials = authHeader.Substring("Basic ".Length).Trim();
                var credentials = Encoding.UTF8.GetString(
                    Convert.FromBase64String(encodedCredentials));
                var parts = credentials.Split(':', 2);
                
                if (parts.Length == 2 && 
                    parts[0] == _username && 
                    parts[1] == _password)
                {
                    await _next(context);
                    return;
                }
            }

            // 認証失敗
            context.Response.StatusCode = 401;
            context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"API Documentation\"";
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        await _next(context);
    }
}