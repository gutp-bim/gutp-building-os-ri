using Amazon.Runtime;
using Amazon.S3;
using BuildingOS.Shared.Infrastructure.BlobStorage;
using BuildingOS.Shared.Infrastructure.ColdExport;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;
using Microsoft.Extensions.Caching.Memory;

// ── BuildingOS lake backfill CLI (#218) ───────────────────────────────────────
// Migrates existing TimescaleDB `telemetry` rows into the Parquet lake (MinIO), one deterministic
// part-backfill object per building-hour (idempotent re-run). New OSS deployments never need this —
// they start in parquet mode. See docs/operations/oss-lake-backfill-runbook.md.
//
// Usage:
//   dotnet run --project DotNet/BuildingOS.LakeBackfill -- \
//     --from 2026-01-01T00:00:00Z --to 2026-06-01T00:00:00Z [--building b1] [--dry-run]
//
// Connection settings come from flags or env (TIMESCALE_CONNECTION_STRING / MINIO_ENDPOINT /
// MINIO_ACCESS_KEY / MINIO_SECRET_KEY).

var opts = ParseArgs(args);
if (opts is null)
{
    PrintUsage();
    return 1;
}

var (from, to, building, dryRun, timescaleDsn, minioEndpoint, minioKey, minioSecret) = opts.Value;

if (string.IsNullOrEmpty(timescaleDsn))
{
    Console.Error.WriteLine("ERROR: TimescaleDB connection string required (--timescale or TIMESCALE_CONNECTION_STRING).");
    return 1;
}
if (string.IsNullOrEmpty(minioEndpoint))
{
    Console.Error.WriteLine("ERROR: MinIO endpoint required (--minio or MINIO_ENDPOINT).");
    return 1;
}

Console.WriteLine($"Backfill range [{from:o} .. {to:o}){(building is null ? "" : $", building={building}")}{(dryRun ? " (DRY RUN)" : "")}");

var reader = new NpgsqlExportDataReader(timescaleDsn);

if (dryRun)
{
    // Dry run: report the hour windows and source row counts without writing to the lake.
    long totalRows = 0;
    foreach (var w in BackfillPlanner.HourWindows(from, to))
    {
        var rows = await reader.ReadAsync(w.ReadFromUtc, w.ReadToUtc);
        var n = building is null ? rows.Length : rows.Count(r => r.Building == building);
        totalRows += n;
        if (n > 0) Console.WriteLine($"  {w.HourUtc:yyyy-MM-ddTHH}:00 — {n} rows");
    }
    Console.WriteLine($"DRY RUN: would back-fill {totalRows} rows. No objects written.");
    return 0;
}

var s3 = new AmazonS3Client(
    new BasicAWSCredentials(minioKey, minioSecret),
    new AmazonS3Config { ServiceURL = minioEndpoint, ForcePathStyle = true });
var storage = new MinioBlobStorage(s3);
using var cache = new MemoryCache(new MemoryCacheOptions());
var service = new LakeBackfillService(reader, storage, cache);

var result = await service.RunAsync(from, to, building, Console.WriteLine);

Console.WriteLine();
Console.WriteLine("=== Reconciliation ===");
Console.WriteLine($"  hours processed : {result.HoursProcessed}");
Console.WriteLine($"  rows read       : {result.RowsRead}");
Console.WriteLine($"  rows written    : {result.RowsWritten}");
Console.WriteLine($"  objects written : {result.ObjectsWritten}");
if (result.RowsRead != result.RowsWritten)
{
    Console.WriteLine($"  NOTE: {result.RowsRead - result.RowsWritten} duplicate-id rows were de-duplicated on write.");
}
return 0;

static (DateTime From, DateTime To, string? Building, bool DryRun, string? Dsn, string? Minio, string Key, string Secret)?
    ParseArgs(string[] args)
{
    DateTime? from = null, to = null;
    string? building = null, dsn = Environment.GetEnvironmentVariable("TIMESCALE_CONNECTION_STRING");
    string? minio = Environment.GetEnvironmentVariable("MINIO_ENDPOINT");
    var key = Environment.GetEnvironmentVariable("MINIO_ACCESS_KEY") ?? "buildingos";
    var secret = Environment.GetEnvironmentVariable("MINIO_SECRET_KEY") ?? "buildingos123";
    var dryRun = false;

    try
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--from": from = ParseUtc(NextArg(args, ref i)); break;
                case "--to": to = ParseUtc(NextArg(args, ref i)); break;
                case "--building": building = NextArg(args, ref i); break;
                case "--timescale": dsn = NextArg(args, ref i); break;
                case "--minio": minio = NextArg(args, ref i); break;
                case "--minio-key": key = NextArg(args, ref i); break;
                case "--minio-secret": secret = NextArg(args, ref i); break;
                case "--dry-run": dryRun = true; break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return null;
            }
        }
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException)
    {
        // Missing flag value (NextArg) or an unparseable timestamp (ParseUtc) → usage, not a stack trace.
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        return null;
    }

    if (from is null || to is null)
    {
        Console.Error.WriteLine("ERROR: --from and --to are required (ISO-8601 UTC).");
        return null;
    }
    return (from.Value, to.Value, building, dryRun, dsn, minio, key, secret);
}

static string NextArg(string[] args, ref int i)
    => ++i < args.Length ? args[i] : throw new ArgumentException("missing value for " + args[i - 1]);

static DateTime ParseUtc(string s)
    => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);

static void PrintUsage()
{
    Console.Error.WriteLine(
        "Usage: BuildingOS.LakeBackfill --from <iso-utc> --to <iso-utc> [--building <id>] [--dry-run]\n" +
        "       [--timescale <dsn>] [--minio <endpoint>] [--minio-key <k>] [--minio-secret <s>]\n" +
        "Connection settings fall back to TIMESCALE_CONNECTION_STRING / MINIO_ENDPOINT / MINIO_ACCESS_KEY / MINIO_SECRET_KEY.");
}
