using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BuildingOS.Shared.Infrastructure.OxiGraph;

/// <summary>
/// HTTP client for OxiGraph's SPARQL 1.1 Protocol endpoint.
/// Sends SELECT queries and returns variable bindings as dictionaries.
/// </summary>
public class OxiGraphClient
{
    private readonly HttpClient _http;
    private readonly string _queryEndpoint;
    private readonly string _updateEndpoint;
    private readonly string _storeEndpoint;
    private readonly string _storeBase;

    public OxiGraphClient(HttpClient http, string oxiGraphBaseUrl)
    {
        _http = http;
        var baseUrl = oxiGraphBaseUrl.TrimEnd('/');
        _queryEndpoint = $"{baseUrl}/query";
        _updateEndpoint = $"{baseUrl}/update";
        _storeBase = $"{baseUrl}/store";
        _storeEndpoint = $"{_storeBase}?default";
    }

    /// <summary>
    /// Execute a SPARQL SELECT query and return variable bindings.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> QueryAsync(
        string sparql, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _queryEndpoint)
        {
            Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("query", sparql) })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/sparql-results+json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseSparqlResults(json);
    }

    /// <summary>
    /// Execute a SPARQL UPDATE (INSERT DATA / DELETE DATA).
    /// </summary>
    public async Task UpdateAsync(string sparqlUpdate, CancellationToken ct = default)
    {
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("update", sparqlUpdate)
        });
        using var response = await _http.PostAsync(_updateEndpoint, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Bulk-import Turtle RDF data into OxiGraph's default graph via POST /store.
    /// </summary>
    public async Task ImportTurtleAsync(string turtleContent, CancellationToken ct = default)
    {
        var content = new StringContent(turtleContent, Encoding.UTF8, "text/turtle");
        using var response = await _http.PostAsync(_storeEndpoint, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Replace the default graph with the given Turtle content (idempotent).
    /// Drops all existing triples first, then imports the new content.
    /// </summary>
    public async Task ReplaceDefaultGraphAsync(string turtleContent, CancellationToken ct = default)
    {
        await UpdateAsync("DROP ALL", ct).ConfigureAwait(false);
        await ImportTurtleAsync(turtleContent, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Load Turtle into a named graph (Graph Store Protocol <c>PUT /store?graph={uri}</c>), replacing
    /// that graph's contents. Used to stage an import for preview/validation without touching the
    /// default graph (#322).
    /// </summary>
    public async Task LoadNamedGraphAsync(string graphUri, string turtleContent, CancellationToken ct = default)
    {
        var endpoint = $"{_storeBase}?graph={Uri.EscapeDataString(graphUri)}";
        var content = new StringContent(turtleContent, Encoding.UTF8, "text/turtle");
        using var response = await _http.PutAsync(endpoint, content, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Drop a named graph (SPARQL <c>DROP GRAPH</c>). Used to discard a staged import (#322).</summary>
    public async Task DropNamedGraphAsync(string graphUri, CancellationToken ct = default)
    {
        await UpdateAsync($"DROP GRAPH <{graphUri}>", ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> ParseSparqlResults(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var bindings = doc.RootElement
            .GetProperty("results")
            .GetProperty("bindings");

        var rows = new List<Dictionary<string, string>>();
        foreach (var binding in bindings.EnumerateArray())
        {
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in binding.EnumerateObject())
            {
                if (prop.Value.TryGetProperty("value", out var val))
                    row[prop.Name] = val.GetString() ?? string.Empty;
            }
            rows.Add(row);
        }
        return rows;
    }
}
