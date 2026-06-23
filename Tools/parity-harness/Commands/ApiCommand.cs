using ParityHarness.Core;
using ParityHarness.Report;

namespace ParityHarness.Commands;

/// <summary>
/// Compares responses from two API endpoints for the same set of paths.
/// Usage: api --url-a https://api-azure/ --url-b https://api-oss/ [--paths /buildings,/floors] [--golden-dir ../../tests/golden/api] [--report-dir ./reports]
/// </summary>
public static class ApiCommand
{
    private static readonly string[] DefaultPaths =
    [
        "/health",
        "/buildings",
        "/buildings/floors",
        "/buildings/spaces",
        "/buildings/devices",
        "/buildings/points",
    ];

    public static async Task<int> RunAsync(
        string urlA,
        string urlB,
        string[] paths,
        string? goldenDir,
        string reportDir)
    {
        if (paths.Length == 0) paths = DefaultPaths;

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var results = new List<ParityResult>();

        foreach (var path in paths)
        {
            var scenarioName = path.TrimStart('/').Replace('/', '-');
            string responseA, responseB;

            try
            {
                responseA = await FetchAsync(httpClient, urlA.TrimEnd('/') + path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[api] FETCH ERROR url-a {path}: {ex.Message}");
                results.Add(new ParityResult(scenarioName, [new FieldDiff("(http)", "success", ex.Message, DiffType.ValueMismatch)]));
                continue;
            }

            try
            {
                responseB = await FetchAsync(httpClient, urlB.TrimEnd('/') + path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[api] FETCH ERROR url-b {path}: {ex.Message}");
                results.Add(new ParityResult(scenarioName, [new FieldDiff("(http)", "success", ex.Message, DiffType.ValueMismatch)]));
                continue;
            }

            // Optionally compare against golden file
            string expected;
            if (goldenDir != null)
            {
                var goldenPath = Path.Combine(goldenDir, $"{scenarioName}.json");
                expected = File.Exists(goldenPath) ? File.ReadAllText(goldenPath) : responseA;
            }
            else
            {
                expected = responseA;
            }

            var diffs = JsonDiff.Compare(expected, responseB);
            results.Add(new ParityResult(scenarioName, diffs));
        }

        return GoldenCommand.WriteReports(results, reportDir, "api");
    }

    private static async Task<string> FetchAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
