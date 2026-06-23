using System.Data;
using DuckDB.NET.Data;

namespace BuildingOS.DuckDbSpike;

/// <summary>
/// Reads telemetry data from the Parquet lake using DuckDB's S3 extension (#221 spike).
/// Competes with <c>ParquetLakeTelemetryStore</c> (Parquet.Net) in the benchmark runner.
/// </summary>
public sealed class DuckDbLakeTelemetryStore : IDisposable
{
    private readonly DuckDBConnection _conn;

    public DuckDbLakeTelemetryStore(string minioEndpoint, string accessKey, string secretKey)
    {
        _conn = new DuckDBConnection("Data Source=:memory:");
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSTALL httpfs; LOAD httpfs;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = DuckDbQueryBuilder.BuildS3Config(minioEndpoint, accessKey, secretKey);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Queries rows for a single point using <c>read_parquet</c> over S3.
    /// Returns raw rows as (pointId, datetime, value) tuples for benchmarking.
    /// </summary>
    public List<(string PointId, string Datetime, string? Value)> Query(
        string bucket, string building, DateTime start, DateTime end, string pointId)
    {
        var sql = DuckDbQueryBuilder.BuildPointQuery(bucket, building, start, end, pointId);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var rows = new List<(string, string, string?)>();
        while (reader.Read())
        {
            rows.Add((
                reader["point_id"]?.ToString() ?? "",
                reader["datetime"]?.ToString() ?? "",
                reader["value"]?.ToString()));
        }
        return rows;
    }

    public void Dispose() => _conn.Dispose();
}
