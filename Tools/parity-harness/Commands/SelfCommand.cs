using ParityHarness.Core;
using ParityHarness.Report;

namespace ParityHarness.Commands;

/// <summary>
/// Self-comparison: sends the same request to one endpoint twice, verifying idempotency.
/// Exit code 0 = 100% match (baseline verified).
/// Usage: self --base-url https://api/ [--paths /buildings,/floors] [--report-dir ./reports]
/// </summary>
public static class SelfCommand
{
    public static async Task<int> RunAsync(string baseUrl, string[] paths, string reportDir)
    {
        if (paths.Length == 0)
            paths = ["/health", "/buildings", "/buildings/floors", "/buildings/spaces"];

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var results = new List<ParityResult>();

        foreach (var path in paths)
        {
            var scenarioName = $"self:{path.TrimStart('/').Replace('/', '-')}";
            string first, second;

            try
            {
                first = await FetchAsync(httpClient, baseUrl.TrimEnd('/') + path);
                second = await FetchAsync(httpClient, baseUrl.TrimEnd('/') + path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[self] FETCH ERROR {path}: {ex.Message}");
                results.Add(new ParityResult(scenarioName, [new FieldDiff("(http)", "success", ex.Message, DiffType.ValueMismatch)]));
                continue;
            }

            var diffs = JsonDiff.Compare(first, second);
            results.Add(new ParityResult(scenarioName, diffs));

            if (diffs.Count > 0)
                Console.Error.WriteLine($"[self] WARNING: non-idempotent response for {path} ({diffs.Count} diff(s))");
        }

        return GoldenCommand.WriteReports(results, reportDir, "self");
    }

    private static async Task<string> FetchAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
