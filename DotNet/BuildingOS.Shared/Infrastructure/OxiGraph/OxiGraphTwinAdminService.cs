using System.Diagnostics;
using BuildingOS.Shared.Domain.TwinAdmin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

/// <summary>
/// <see cref="ITwinAdminService"/> over <see cref="OxiGraphClient"/> (#322). Import preview stages the
/// Turtle in a temporary named graph, runs scoped count/uniqueness queries, then drops the staging
/// graph — so a destructive replace is validated before it touches the default graph.
/// </summary>
public sealed class OxiGraphTwinAdminService : ITwinAdminService
{
    private const string Sbco = "https://www.sbco.or.jp/ont/";

    private readonly OxiGraphClient _client;
    private readonly ILogger<OxiGraphTwinAdminService> _logger;

    public OxiGraphTwinAdminService(
        OxiGraphClient client, ILogger<OxiGraphTwinAdminService>? logger = null)
    {
        _client = client;
        _logger = logger ?? NullLogger<OxiGraphTwinAdminService>.Instance;
    }

    public async Task<TwinImportPreview> PreviewImportAsync(string turtle, CancellationToken ct = default)
    {
        var graph = $"urn:bos:import-preview:{Guid.NewGuid():N}";
        await _client.LoadNamedGraphAsync(graph, turtle, ct).ConfigureAwait(false);
        try
        {
            var triples = await ScalarCountAsync(
                $"SELECT (COUNT(*) AS ?n) WHERE {{ GRAPH <{graph}> {{ ?s ?p ?o }} }}", ct).ConfigureAwait(false);

            var gateways = await ScalarCountAsync(
                $"SELECT (COUNT(DISTINCT ?gw) AS ?n) WHERE {{ GRAPH <{graph}> {{ ?pt <{Sbco}gatewayId> ?gw }} }}",
                ct).ConfigureAwait(false);

            var collisionRows = await _client.QueryAsync($@"
SELECT ?gw (COUNT(DISTINCT ?b) AS ?n) WHERE {{
  GRAPH <{graph}> {{ ?pt a <{Sbco}PointExt> ; <{Sbco}gatewayId> ?gw ; <{Sbco}building> ?b . }}
}}
GROUP BY ?gw
HAVING (COUNT(DISTINCT ?b) > 1)", ct).ConfigureAwait(false);

            var collisions = collisionRows
                .Select(r => new GatewayCollision(
                    r.GetValueOrDefault("gw", ""),
                    int.TryParse(r.GetValueOrDefault("n", "0"), out var n) ? n : 0))
                .ToList();

            return new TwinImportPreview(triples, (int)gateways, collisions);
        }
        finally
        {
            // Always discard the staging graph, even if validation queries throw. Swallow cleanup
            // errors so they never mask the original validation exception (best-effort cleanup).
            try
            {
                await _client.DropNamedGraphAsync(graph, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to drop import-preview staging graph {Graph}", graph);
            }
        }
    }

    public async Task ApplyImportAsync(string turtle, TwinImportMode mode, CancellationToken ct = default)
    {
        if (mode == TwinImportMode.Replace)
        {
            await _client.ReplaceDefaultGraphAsync(turtle, ct).ConfigureAwait(false);
        }
        else
        {
            await _client.ImportTurtleAsync(turtle, ct).ConfigureAwait(false);
        }
    }

    public async Task<SparqlQueryResult> RunReadOnlyQueryAsync(
        string query, int maxRows, TimeSpan timeout, CancellationToken ct = default)
    {
        // Defense in depth: never let a non-read-only query through even if a caller forgets to guard.
        if (!SparqlReadOnlyGuard.IsReadOnly(query))
        {
            throw new InvalidOperationException("Only read-only SELECT/ASK queries are permitted.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var sw = Stopwatch.StartNew();
        var rows = await _client.QueryAsync(query, cts.Token).ConfigureAwait(false);
        sw.Stop();

        var cap = maxRows <= 0 ? 1000 : maxRows;
        var truncated = rows.Count > cap;
        var capped = truncated ? rows.Take(cap).ToList() : rows;
        var columns = capped.Count > 0
            ? capped[0].Keys.ToList()
            : new List<string>();

        return new SparqlQueryResult(columns, capped, capped.Count, truncated, sw.ElapsedMilliseconds);
    }

    private async Task<long> ScalarCountAsync(string sparql, CancellationToken ct)
    {
        var rows = await _client.QueryAsync(sparql, ct).ConfigureAwait(false);
        if (rows.Count == 0) return 0;
        return long.TryParse(rows[0].GetValueOrDefault("n", "0"), out var n) ? n : 0;
    }
}
