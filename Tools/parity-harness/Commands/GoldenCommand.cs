using ParityHarness.Core;
using ParityHarness.Report;

namespace ParityHarness.Commands;

/// <summary>
/// Compares JSON files in --actual-dir against golden files in --golden-dir.
/// Usage: golden --golden-dir ../../tests/golden/connector --actual-dir ./output [--report-dir ./reports]
/// </summary>
public static class GoldenCommand
{
    public static int Run(string goldenDir, string? actualDir, string reportDir)
    {
        if (!Directory.Exists(goldenDir))
        {
            Console.Error.WriteLine($"[golden] golden-dir not found: {goldenDir}");
            return 2;
        }

        var goldenFiles = Directory.GetFiles(goldenDir, "*.json", SearchOption.AllDirectories);
        if (goldenFiles.Length == 0)
        {
            Console.WriteLine("[golden] No golden files found.");
            return 0;
        }

        var results = new List<ParityResult>();

        foreach (var goldenPath in goldenFiles.OrderBy(f => f))
        {
            var scenarioName = Path.GetFileNameWithoutExtension(goldenPath);
            string actual;

            if (actualDir != null)
            {
                var actualPath = Path.Combine(actualDir, Path.GetFileName(goldenPath));
                if (!File.Exists(actualPath))
                {
                    results.Add(new ParityResult(scenarioName,
                        [new FieldDiff("(file)", goldenPath, null, DiffType.Missing)]));
                    continue;
                }
                actual = File.ReadAllText(actualPath);
            }
            else
            {
                // Self-verification: golden file is both expected and actual (should always pass)
                actual = File.ReadAllText(goldenPath);
            }

            var expected = File.ReadAllText(goldenPath);
            var diffs = JsonDiff.Compare(expected, actual);
            results.Add(new ParityResult(scenarioName, diffs));
        }

        return WriteReports(results, reportDir, "golden");
    }

    internal static int WriteReports(IReadOnlyList<ParityResult> results, string reportDir, string prefix)
    {
        Directory.CreateDirectory(reportDir);
        var summary = ParitySummary.From(results);

        var jsonPath = Path.Combine(reportDir, $"{prefix}-parity-report.json");
        File.WriteAllText(jsonPath, JsonReporter.Generate(results));

        var htmlPath = Path.Combine(reportDir, $"{prefix}-parity-report.html");
        File.WriteAllText(htmlPath, HtmlReporter.Generate(results));

        Console.WriteLine($"[{prefix}] Results: {summary.Passed}/{summary.Total} passed");
        Console.WriteLine($"  JSON report → {jsonPath}");
        Console.WriteLine($"  HTML report → {htmlPath}");

        foreach (var r in results.Where(r => !r.Passed))
        {
            Console.WriteLine($"  FAIL: {r.ScenarioName}");
            foreach (var d in r.Diffs)
                Console.WriteLine($"    [{d.Type}] {d.Path}: expected={d.Expected ?? "—"}, actual={d.Actual ?? "—"}");
        }

        return summary.AllPassed ? 0 : 1;
    }
}
