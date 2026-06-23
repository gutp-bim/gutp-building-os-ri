using System.Text.Json;
using BuildingOs.ApiServer.Extensions;
using BuildingOs.ApiServer.Filters;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.OidcClients;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>
/// Keycloak OIDC クライアントアプリ管理（#324）。confidential / service-account クライアントの
/// 一覧・詳細・作成・シークレット回転・有効/無効・削除を Keycloak admin API 経由で行う。
/// 全 mutating 操作を共有 admin 監査に記録する。secret は作成/回転の応答に**一度だけ**含まれ、
/// 一覧/詳細・監査には残さない。管理者のみ。未設定時は 503。
/// </summary>
[ApiController]
[Route("api/admin/oidc-clients")]
[AuthorizeFilter]
public class OidcClientsController : ControllerBase
{
    private readonly IOidcClientManagementService _service;
    private readonly IAdminAuditRecorder _audit;
    private readonly ILogger<OidcClientsController> _logger;

    public OidcClientsController(
        IOidcClientManagementService service,
        IAdminAuditRecorder audit,
        ILogger<OidcClientsController> logger)
    {
        _service = service;
        _audit = audit;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OidcClientSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();
        try
        {
            return Ok(await _service.ListClientsAsync(ct).ConfigureAwait(false));
        }
        catch (OidcServiceUnavailableException ex)
        {
            return Unavailable(ex);
        }
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(OidcClientDetail), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();
        try
        {
            var client = await _service.GetClientAsync(id, ct).ConfigureAwait(false);
            return client is null ? NotFound() : Ok(client);
        }
        catch (OidcServiceUnavailableException ex)
        {
            return Unavailable(ex);
        }
    }

    [HttpPost]
    [ProducesResponseType(typeof(CreatedOidcClientResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateOidcClientRequest request, CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();
        if (string.IsNullOrWhiteSpace(request.ClientId))
        {
            return BadRequest(new { error = "clientId は必須です" });
        }

        var auth = HttpContext.GetAuthorizationContext();
        try
        {
            var (client, secret) = await _service.CreateClientAsync(
                new CreateOidcClientSpec(
                    request.ClientId, request.Description, request.ServiceAccountsEnabled, request.RedirectUris),
                ct).ConfigureAwait(false);

            // Audit records the operation and serviceAccount flag — never the secret.
            await AuditAsync(auth, "create", request.ClientId, AdminAuditResult.Success,
                new { serviceAccountsEnabled = request.ServiceAccountsEnabled }, ct).ConfigureAwait(false);

            // The plaintext secret is returned ONCE here and is never readable again.
            return StatusCode(StatusCodes.Status201Created, new CreatedOidcClientResponse(client, secret));
        }
        catch (OidcServiceUnavailableException ex)
        {
            return Unavailable(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create OIDC client {ClientId}", request.ClientId);
            await AuditAsync(auth, "create", request.ClientId, AdminAuditResult.Failure,
                new { error = ex.Message }, ct).ConfigureAwait(false);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("{id}/rotate-secret")]
    [ProducesResponseType(typeof(RotatedSecretResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> RotateSecret(string id, CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();
        var auth = HttpContext.GetAuthorizationContext();
        try
        {
            var secret = await _service.RotateSecretAsync(id, ct).ConfigureAwait(false);
            await AuditAsync(auth, "rotate-secret", id, AdminAuditResult.Success, null, ct).ConfigureAwait(false);
            return Ok(new RotatedSecretResponse(secret));
        }
        catch (OidcServiceUnavailableException ex)
        {
            return Unavailable(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rotate secret for OIDC client {Id}", id);
            await AuditAsync(auth, "rotate-secret", id, AdminAuditResult.Failure,
                new { error = ex.Message }, ct).ConfigureAwait(false);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id}/enabled")]
    [ProducesResponseType(typeof(OidcClientDetail), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetEnabled(string id, [FromBody] SetEnabledRequest request, CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();
        var auth = HttpContext.GetAuthorizationContext();
        try
        {
            var client = await _service.SetEnabledAsync(id, request.Enabled, ct).ConfigureAwait(false);
            await AuditAsync(auth, "set-enabled", id, AdminAuditResult.Success,
                new { enabled = request.Enabled }, ct).ConfigureAwait(false);
            return Ok(client);
        }
        catch (OidcServiceUnavailableException ex)
        {
            return Unavailable(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set enabled for OIDC client {Id}", id);
            await AuditAsync(auth, "set-enabled", id, AdminAuditResult.Failure,
                new { error = ex.Message }, ct).ConfigureAwait(false);
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (!IsAdmin()) return Forbid();
        var auth = HttpContext.GetAuthorizationContext();
        try
        {
            await _service.DeleteClientAsync(id, ct).ConfigureAwait(false);
            await AuditAsync(auth, "delete", id, AdminAuditResult.Success, null, ct).ConfigureAwait(false);
            return NoContent();
        }
        catch (OidcServiceUnavailableException ex)
        {
            return Unavailable(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete OIDC client {Id}", id);
            await AuditAsync(auth, "delete", id, AdminAuditResult.Failure,
                new { error = ex.Message }, ct).ConfigureAwait(false);
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool IsAdmin() => HttpContext.GetAuthorizationContext().IsAdmin;

    private ObjectResult Unavailable(OidcServiceUnavailableException ex) =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });

    private Task AuditAsync(
        BuildingOS.Shared.Domain.Authorization.AuthorizationContext auth,
        string action, string targetId, AdminAuditResult result, object? detail, CancellationToken ct)
    {
        var detailJson = detail is null ? null : JsonSerializer.Serialize(detail);
        var record = AdminAuditRecord.Create(
            AdminAuditSubjects.OidcClient, action, targetId, auth.UserId, actorName: null, result, detailJson);
        return _audit.RecordAsync(record, ct);
    }

    // ── DTOs ───────────────────────────────────────────────────────────────────

    public record CreateOidcClientRequest
    {
        public string ClientId { get; init; } = default!;
        public string? Description { get; init; }
        public bool ServiceAccountsEnabled { get; init; }
        public List<string>? RedirectUris { get; init; }
    }

    /// <summary>Create response — carries the one-time plaintext secret (never returned again).</summary>
    public record CreatedOidcClientResponse(OidcClientDetail Client, string Secret);

    public record RotatedSecretResponse(string Secret);

    public record SetEnabledRequest
    {
        public bool Enabled { get; init; }
    }
}
