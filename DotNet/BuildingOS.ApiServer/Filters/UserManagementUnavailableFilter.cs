using BuildingOS.Shared.Domain.UserManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BuildingOs.ApiServer.Filters;

/// <summary>
/// Maps <see cref="UserManagementUnavailableException"/> to 503 (#293). Keycloak admin being
/// unconfigured is a deployment state, not a client error, so every action of the annotated
/// controller answers "service unavailable" with the reason rather than 400/500.
///
/// Applied at the controller so a newly added action cannot forget it — the failure is raised from
/// <see cref="UnconfiguredUserManagementService"/>, which backs every method of the interface.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class UserManagementUnavailableFilter : Attribute, IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not UserManagementUnavailableException ex) return;

        context.Result = new ObjectResult(new { error = ex.Message })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
        };
        context.ExceptionHandled = true;
    }
}
