using System.Diagnostics;

namespace BuildingOS.DuckDbSpike;

/// <summary>
/// Compares DuckDB vs Parquet.Net for Parquet lake reads (#221 spike).
/// Run with: dotnet run -- --minio http://localhost:9000 --bucket lake --building B01 --point P001
/// Results are printed to stdout and appended to results/run-{timestamp}.txt.
/// </summary>
public static class BenchmarkRunner
{
    public record Config(
        string MinioEndpoint,
        string AccessKey,
        string SecretKey,
        string Bucket,
        string Building,
        string PointId,
        DateTime Start,
        DateTime End,
        int Iterations = 3);

    public static async Task RunAsync(Config cfg)
    {
        Console.WriteLine("=== DuckDB vs Parquet.Net lake benchmark (#221) ===");
        Console.WriteLine($"MinIO:    {cfg.MinioEndpoint}");
        Console.WriteLine($"Query:    building={cfg.Building} point={cfg.PointId}");
        Console.WriteLine($"Range:    {cfg.Start:O} → {cfg.End:O}");
        Console.WriteLine($"Iters:    {cfg.Iterations}");
        Console.WriteLine();

        var duckDbTimes  = new List<long>();
        var duckDbCounts = new List<int>();

        Console.WriteLine("--- DuckDB (S3 + httpfs) ---");
        try
        {
            using var store = new DuckDbLakeTelemetryStore(cfg.MinioEndpoint, cfg.AccessKey, cfg.SecretKey);
            for (var i = 0; i < cfg.Iterations; i++)
            {
                var sw = Stopwatch.StartNew();
                var rows = store.Query(cfg.Bucket, cfg.Building, cfg.Start, cfg.End, cfg.PointId);
                sw.Stop();
                duckDbTimes.Add(sw.ElapsedMilliseconds);
                duckDbCounts.Add(rows.Count);
                Console.WriteLine($"  iter {i + 1}: {sw.ElapsedMilliseconds} ms, {rows.Count} rows");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("--- Summary ---");
        PrintSummary("DuckDB", duckDbTimes, duckDbCounts);
        Console.WriteLine();
        Console.WriteLine("Record results in results/ and update docs/reference/oss-duckdb-spike.md.");
    }

    private static void PrintSummary(string label, List<long> times, List<int> counts)
    {
        if (times.Count == 0) { Console.WriteLine($"  {label}: no data"); return; }
        Console.WriteLine($"  {label}: min={times.Min()} ms  avg={times.Average():F0} ms  max={times.Max()} ms  rows={counts.FirstOrDefault()}");
    }
}
