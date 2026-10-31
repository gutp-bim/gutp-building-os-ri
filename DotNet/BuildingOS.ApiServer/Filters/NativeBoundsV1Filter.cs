using BuildingOS.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BuildingOs.ApiServer.Filters;

/// <summary>Rejects a v1 read response that cannot faithfully represent native bounds as int32.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NativeBoundsV1FilterAttribute : Attribute, IResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ObjectResult result || result.StatusCode is >= 400) return;
        IEnumerable<Point> points = result.Value switch
        {
            Point point => [point],
            PointDetail detail => [detail.Point],
            IEnumerable<Point> list => list,
            IEnumerable<PointDetail> details => details.Select(d => d.Point),
            _ => [],
        };
        if (points.Any(p => p.HasUnrepresentableNativeBounds))
            context.Result = new ConflictObjectResult(new NativeBoundsV1Error(
                "native_bounds_not_representable",
                "The v1 API requires minPresValue and maxPresValue to be int32 values or absent. The response cannot be represented without data loss."));
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}

/// <summary>A v1 read response was refused because its native bounds cannot be represented.</summary>
public sealed record NativeBoundsV1Error(string Error, string Message);
