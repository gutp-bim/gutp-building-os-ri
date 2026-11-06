using BuildingOS.Shared.Domain.GatewayProvisioning;
using BuildingOS.Shared.Domain.TwinAdmin;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

/// <summary>
/// Validates a complete candidate before publication. A graph comparison in the commit transaction
/// prevents an append from publishing validation results computed against an obsolete live twin.
/// No distributed lock or additional configuration registry is introduced.
/// </summary>
internal static class ClockValidatedImport
{
    internal static async Task ApplyAsync(
        OxiGraphClient client, OxiGraphIngestMaterializer materializer, string source,
        TwinImportMode mode, ILogger logger, CancellationToken ct)
    {
        var root = $"urn:bos:clock-import:{Guid.NewGuid():N}";
        var baseline = root + ":baseline";
        var candidate = root + ":candidate";
        var committed = root + ":committed";
        try
        {
            // Register the marker's graph/IRIs before the multi-operation transaction. OxiGraph
            // 0.5.0 cannot reliably resolve a fresh graph name first inserted and then read within
            // that transaction (string-store lookup failure). The false marker cannot authorize a commit.
            await client.UpdateAsync($"CREATE SILENT GRAPH <{baseline}> ; INSERT {{ GRAPH <{baseline}> {{ ?s ?p ?o }} }} WHERE {{ ?s ?p ?o }} ; INSERT DATA {{ GRAPH <{committed}> {{ <{root}> <urn:bos:committed> false }} }}", ct).ConfigureAwait(false);
            await materializer.MaterializeNamedGraphAsync(source, candidate, ct).ConfigureAwait(false);
            if (mode == TwinImportMode.Append)
                await client.UpdateAsync($"INSERT {{ GRAPH <{candidate}> {{ ?s ?p ?o }} }} WHERE {{ GRAPH <{baseline}> {{ ?s ?p ?o }} }}", ct).ConfigureAwait(false);
            var rows = await client.QueryAsync(ClockConfigurationGraph.BuildQuery(candidate), ct).ConfigureAwait(false);
            var projection = new ClockConfigurationGraph(rows);
            var previousRows = await client.QueryAsync(ClockConfigurationGraph.BuildQuery(baseline), ct).ConfigureAwait(false);
            var issues = projection.Validate().Concat(projection.ValidateOwnership(new(previousRows))).Distinct().ToArray();
            if (issues.Length > 0) throw new ClockConfigurationException(issues);

            // One multi-statement OxiGraph update is one transaction: compare the baseline, mark
            // success, and replace the complete default graph only when the comparison succeeded.
            var provenance = mode == TwinImportMode.Replace
                ? $"DELETE {{ GRAPH <urn:bos:twin-source> {{ ?s ?p ?o }} }} WHERE {{ GRAPH <{committed}> {{ ?m ?f true }} GRAPH <urn:bos:twin-source> {{ ?s ?p ?o }} }} ; " +
                  $"INSERT {{ GRAPH <urn:bos:twin-source> {{ ?s ?p ?o }} }} WHERE {{ GRAPH <{committed}> {{ ?m ?f true }} GRAPH <{source}> {{ ?s ?p ?o }} }} ; "
                : "";
            await client.UpdateAsync($$"""
                INSERT { GRAPH <{{committed}}> { <{{root}}> <urn:bos:committed> true } }
                WHERE {
                  FILTER NOT EXISTS { ?s ?p ?o FILTER NOT EXISTS { GRAPH <{{baseline}}> { ?s ?p ?o } } }
                  FILTER NOT EXISTS { GRAPH <{{baseline}}> { ?s ?p ?o } FILTER NOT EXISTS { ?s ?p ?o } }
                } ;
                {{provenance}}
                DELETE { ?s ?p ?o } WHERE { GRAPH <{{committed}}> { ?marker ?flag true } ?s ?p ?o } ;
                INSERT { ?s ?p ?o } WHERE { GRAPH <{{committed}}> { ?marker ?flag true } GRAPH <{{candidate}}> { ?s ?p ?o } }
                """, ct).ConfigureAwait(false);
            var result = await client.QueryAsync($"SELECT ?flag WHERE {{ GRAPH <{committed}> {{ ?s ?p true }} BIND(true AS ?flag) }}", ct).ConfigureAwait(false);
            if (result.Count == 0) throw new TwinImportConcurrencyException();
        }
        finally
        {
            foreach (var graph in new[] { baseline, candidate, committed })
            {
                try { await client.UpdateAsync($"DROP SILENT GRAPH <{graph}>", CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to clean clock import graph {Graph}", graph); }
            }
        }
    }
}
