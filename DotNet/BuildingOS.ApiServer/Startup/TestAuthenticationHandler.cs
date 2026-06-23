using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;

namespace BuildingOs.ApiServer
{
    public class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger, UrlEncoder encoder, ISystemClock clock)
            : base(options, logger, encoder, clock)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // 開発環境では常に認証成功
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, "TestUser"),
                new Claim(ClaimTypes.NameIdentifier, "test-user-id")
            };

            // ロール: X-Test-Roleヘッダーで上書き可能（デフォルトはadmin）
            var role = Request.Headers.TryGetValue("X-Test-Role", out var roleHeader)
                ? roleHeader.ToString()
                : "admin";
            // Keycloak-native claim names (building_os_role / permissions), matching the realm's
            // building-os-api client-scope mappers, so dev/test exercises the real token path (#10).
            claims.Add(new Claim("building_os_role", role));

            // パーミッション: X-Test-Permissionsヘッダーで追加可能（カンマ区切り）
            if (Request.Headers.TryGetValue("X-Test-Permissions", out var permissionsHeader))
            {
                var permissions = permissionsHeader.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var permission in permissions)
                {
                    claims.Add(new Claim("permissions", permission.Trim()));
                }
            }

            var identity = new ClaimsIdentity(claims, "Test");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "Test");

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
