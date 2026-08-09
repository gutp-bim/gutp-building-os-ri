using System.Text.Json;
using BuildingOs.ApiServer.Extensions;
using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.UserManagement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BuildingOs.ApiServer.Filters;

/// <summary>
/// Applies <see cref="UserManagementUnavailableExceptionFilter"/> to a controller or action.
///
/// This is a <see cref="TypeFilterAttribute"/> rather than a plain attribute because the filter now
/// needs <see cref="IAdminAuditRecorder"/>, and an attribute instantiated by the runtime cannot take
/// constructor dependencies. The type name keeps <c>[UserManagementUnavailableFilter]</c> valid at
/// every existing usage — C# resolves an attribute with or without the <c>Attribute</c> suffix.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class UserManagementUnavailableFilterAttribute : TypeFilterAttribute
{
    public UserManagementUnavailableFilterAttribute()
        : base(typeof(UserManagementUnavailableExceptionFilter))
    {
    }
}

/// <summary>
/// Maps <see cref="UserManagementUnavailableException"/> to 503 (#293) and records the rejected
/// mutation in the admin audit log (#303). Keycloak admin being unconfigured is a deployment state,
/// not a client error, so every action of the annotated controller answers "service unavailable"
/// with the reason rather than 400/500.
///
/// <para>
/// Auditing lives here rather than in each action's <c>catch</c> because the exception is not always
/// raised inside one: the lockout guard calls <c>GetUsersAsync</c> before the try block, so those
/// requests returned 503 with no trace at all. A per-action catch also has to be remembered by
/// whoever adds the next action — the filter cannot be forgotten, since it is declared once on the
/// controller.
/// </para>
/// </summary>
public sealed class UserManagementUnavailableExceptionFilter(
    IAdminAuditRecorder audit,
    ILogger<UserManagementUnavailableExceptionFilter> logger) : IAsyncExceptionFilter
{
    public async Task OnExceptionAsync(ExceptionContext context)
    {
        if (context.Exception is not UserManagementUnavailableException ex) return;

        await TryAuditAsync(context).ConfigureAwait(false);

        context.Result = new ObjectResult(new { error = ex.Message })
        {
            StatusCode = StatusCodes.Status503ServiceUnavailable,
        };
        context.ExceptionHandled = true;
    }

    // How long the audit write may hold up the 503. The record matters, but the caller is waiting on
    // a response that says the service is unavailable — an audit store that is itself unavailable
    // must not turn that into a hang.
    private static readonly TimeSpan AuditTimeout = TimeSpan.FromSeconds(5);

    private async Task TryAuditAsync(ExceptionContext context)
    {
        // The audit log records mutations, and only the verbs that are one. Excluding GET alone was
        // not enough: ASP.NET routes HEAD to the same action as GET, so a HEAD probe would have been
        // filed as a refused mutation.
        if (!IsMutation(context.HttpContext.Request.Method)) return;

        try
        {
            var auth = context.HttpContext.GetAuthorizationContext();
            var record = AdminAuditRecord.Create(
                AdminAuditSubjects.User,
                ActionName(context),
                TargetId(context),
                auth.UserId,
                actorName: null,
                AdminAuditResult.Failure,
                JsonSerializer.Serialize(new { error = "user management unavailable" }));

            // Deliberately not the request-abort token: the point of this record is that the
            // operation was refused, and a client that has already disconnected must not erase the
            // evidence. Bounded by its own timeout so a wedged audit store delays the 503 by at
            // most AuditTimeout instead of indefinitely.
            using var cts = new CancellationTokenSource(AuditTimeout);
            await audit.RecordAsync(record, cts.Token).ConfigureAwait(false);
        }
        catch (Exception auditEx)
        {
            // Auditing is best-effort here. Whatever goes wrong writing the record, the caller still
            // needs the 503 — swallowing this exception is what keeps that true.
            logger.LogError(auditEx, "Failed to record the user-management-unavailable audit entry");
        }
    }

    /// <summary>
    /// Whether the verb is one that changes state. An allowlist rather than "not GET": HEAD reaches
    /// the same action as GET, and OPTIONS is answered by the framework — filing either as a refused
    /// mutation would put entries in the audit log for requests that never intended to mutate.
    /// </summary>
    internal static bool IsMutation(string method) =>
        HttpMethods.IsPost(method)
        || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method)
        || HttpMethods.IsDelete(method);

    /// <summary>
    /// The audit `action` for this request. The existing per-action audits use kebab-case verbs
    /// (`set-attributes`, `set-enabled`), so those names are preserved exactly; anything else falls
    /// back to a kebab-cased method name. The fallback matters: it means a newly added action is
    /// audited with a sensible name rather than not at all, which is the failure this filter exists
    /// to prevent.
    /// </summary>
    internal static string ActionName(ExceptionContext context)
    {
        var method = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName;
        return method switch
        {
            null or "" => "unknown",
            "UpdateAttributes" => "set-attributes",
            "SetEnabled" => "set-enabled",
            _ => ToKebabCase(method),
        };
    }

    private static string? TargetId(ExceptionContext context)
        => context.RouteData.Values.TryGetValue("id", out var id) ? id?.ToString() : null;

    internal static string ToKebabCase(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
            {
                if (i > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                sb.Append(name[i]);
            }
        }
        return sb.ToString();
    }
}
