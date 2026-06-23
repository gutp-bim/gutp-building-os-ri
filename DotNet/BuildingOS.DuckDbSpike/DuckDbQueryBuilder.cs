namespace BuildingOS.DuckDbSpike;

/// <summary>
/// Builds DuckDB SQL for scanning the Parquet lake on MinIO/S3 (#221 spike).
/// Pure — no DuckDB dependency. Generates parameterised SQL strings using the
/// S3 extension <c>read_parquet</c> function with the same
/// <c>building_id=X/year=Y/month=MM/day=DD/hour=HH</c> layout as the production lake.
/// </summary>
public static class DuckDbQueryBuilder
{
    /// <summary>
    /// Returns a DuckDB <c>CREATE OR REPLACE SECRET</c> statement that configures the S3 extension
    /// to talk to a path-style MinIO (or S3-compatible) endpoint without TLS.
    /// </summary>
    public static string BuildS3Config(string endpoint, string accessKey, string secretKey) =>
        $"""
         CREATE OR REPLACE SECRET minio (
             TYPE S3,
             KEY_ID '{EscapeString(accessKey)}',
             SECRET '{EscapeString(secretKey)}',
             ENDPOINT '{EscapeString(endpoint)}',
             URL_STYLE 'path',
             USE_SSL false
         );
         """;

    /// <summary>
    /// Returns a DuckDB SELECT statement that scans all Parquet partitions covering
    /// [<paramref name="start"/>, <paramref name="end"/>] for the given building and point.
    /// One hour of grace is added on each side to handle clock skew between writer and reader clocks.
    /// </summary>
    public static string BuildPointQuery(
        string bucket, string building, DateTime start, DateTime end, string pointId)
    {
        var paths       = EnumerateHourPaths(bucket, building, start.AddHours(-1), end.AddHours(1));
        var pathsLit    = string.Join(",\n        ", paths.Select(p => $"'{p}'"));
        var startIso    = start.ToString("O");
        var endIso      = end.ToString("O");

        return $"""
                SELECT point_id, building, device_id, name, value, datetime, data, id
                FROM read_parquet([{pathsLit}],
                    hive_partitioning = true, union_by_name = true)
                WHERE point_id = '{EscapeString(pointId)}'
                  AND datetime >= TIMESTAMPTZ '{startIso}'
                  AND datetime <= TIMESTAMPTZ '{endIso}'
                ORDER BY datetime;
                """;
    }

    /// <summary>
    /// Returns a DuckDB SELECT that fetches the most-recent row for a point from the last
    /// <paramref name="lookbackHours"/> worth of partitions.
    /// </summary>
    public static string BuildLatestQuery(
        string bucket, string building, DateTime since, string pointId, int lookbackHours = 24)
    {
        var paths    = EnumerateHourPaths(bucket, building, since, since.AddHours(lookbackHours));
        var pathsLit = string.Join(",\n        ", paths.Select(p => $"'{p}'"));

        return $"""
                SELECT point_id, building, device_id, name, value, datetime, data, id
                FROM read_parquet([{pathsLit}],
                    hive_partitioning = true, union_by_name = true)
                WHERE point_id = '{EscapeString(pointId)}'
                ORDER BY datetime DESC
                LIMIT 1;
                """;
    }

    // --- helpers ---

    private static IEnumerable<string> EnumerateHourPaths(
        string bucket, string building, DateTime start, DateTime end)
    {
        var cur = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Utc);
        var endHour = new DateTime(end.Year, end.Month, end.Day, end.Hour, 0, 0, DateTimeKind.Utc);
        while (cur <= endHour)
        {
            yield return $"s3://{bucket}/building_id={building}" +
                         $"/year={cur.Year:D4}/month={cur.Month:D2}/day={cur.Day:D2}/hour={cur.Hour:D2}/*.parquet";
            cur = cur.AddHours(1);
        }
    }

    private static string EscapeString(string s) => s.Replace("'", "''");
}
