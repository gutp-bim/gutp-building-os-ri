using BuildingOs.ApiServer.Middlewares;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOs.ApiServer.GatewayProvisioning;
using BuildingOs.ApiServer.Routing;
using BuildingOS.Shared.Domain.GatewayProvisioning;
using Microsoft.AspNetCore.Mvc;

namespace BuildingOs.ApiServer.Controllers;

/// <summary>Gateway identity and admin authorization match the legacy point-list provisioning API.</summary>
[ApiController]
[Route(ApiRoutes.GatewayProvisioning)]
public sealed class GatewayConfigurationController(
    IGatewayProvisioningSource source,
    IGatewayIdentityResolver gatewayIdentity,
    ILogger<GatewayConfigurationController> logger) : ControllerBase
{
    [HttpGet("{gatewayId}/provisioning/v1")]
    [ProducesResponseType(typeof(GatewayConfigurationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Get(string gatewayId, [FromQuery] string? since, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(gatewayId) || gatewayId.Any(char.IsControl)) return BadRequest();
        var caller = gatewayIdentity.ResolveGatewayId(Request.Headers);
        var isAdmin = HttpContext.Items.TryGetValue(AuthorizationContextMiddleware.HttpContextKey, out var context)
            && context is AuthorizationContext { IsAdmin: true };
        if (!isAdmin && !string.Equals(caller, gatewayId, StringComparison.Ordinal))
            return StatusCode(StatusCodes.Status403Forbidden);
        if (since is not null) return BadRequest(new { error = "This endpoint supports full snapshots only." });
        try
        {
            // Always read a consistent live graph before checking the ETag. This endpoint never
            // trusts the point-only revision/cache, so clock-only changes and deletions invalidate
            // immediately across replicas without adding another cache invalidation protocol.
            var snapshot = await source.ReadAsync(gatewayId, ct).ConfigureAwait(false);
            var response = GatewayConfigurationResponse.From(gatewayId, snapshot);
            Response.Headers.ETag = response.Revision;
            Response.Headers.CacheControl = "private, no-cache";
            if (Request.Headers.IfNoneMatch.ToString().Split(',').Select(h => h.Trim())
                .Any(h => h == response.Revision || h == "W/" + response.Revision || h == "*"))
                return StatusCode(StatusCodes.Status304NotModified);
            return new JsonResult(response, GatewayConfigurationResponse.JsonOptions);
        }
        catch (ClockConfigurationException)
        {
            // Do not expose the offending resource/other gateway through a gateway-scoped API.
            logger.LogWarning("Gateway configuration is invalid; withholding provisioning snapshot");
            return Conflict(new { error = "Static clock configuration is invalid; contact the twin administrator." });
        }
    }
}
