using Npgsql;
using System.Data;

namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// Warm-tier telemetry store backed by TimescaleDB via Npgsql.
/// Queries the `telemetry` hypertable for data within the last 90 days.
/// </summary>
public class NpgsqlWarmTelemetryStore : IWarmTelemetryStore
{
    private readonly string _connectionString;

    public NpgsqlWarmTelemetryStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<ValidTelemetryData[]> QueryAsync(string pointId, DateTime start, DateTime end, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(
            @"SELECT time, point_id, building, device_id, name, value, data::TEXT, id
              FROM telemetry
              WHERE point_id = @pointId
                AND time >= @start
                AND time <= @end
              ORDER BY time",
            conn);

        cmd.Parameters.AddWithValue("pointId", pointId);
        cmd.Parameters.AddWithValue("start", start.ToUniversalTime());
        cmd.Parameters.AddWithValue("end", end.ToUniversalTime());

        return await ReadTelemetriesAsync(cmd, cancellationToken);
    }

    public async Task<ValidTelemetryData?> QueryLatestAsync(string pointId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(
            @"SELECT time, point_id, building, device_id, name, value, data::TEXT, id
              FROM telemetry
              WHERE point_id = @pointId
              ORDER BY time DESC
              LIMIT 1",
            conn);

        cmd.Parameters.AddWithValue("pointId", pointId);

        var rows = await ReadTelemetriesAsync(cmd, cancellationToken);
        return rows.FirstOrDefault();
    }

    private static async Task<ValidTelemetryData[]> ReadTelemetriesAsync(NpgsqlCommand cmd, CancellationToken cancellationToken)
    {
        var results = new List<ValidTelemetryData>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ValidTelemetryData
            {
                Datetime = reader.GetDateTime(0).ToString("O"),
                PointId  = reader.IsDBNull(1) ? null : reader.GetString(1),
                Building = reader.IsDBNull(2) ? null : reader.GetString(2),
                DeviceId = reader.IsDBNull(3) ? null : reader.GetString(3),
                Name     = reader.IsDBNull(4) ? null : reader.GetString(4),
                Value    = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                Data     = reader.IsDBNull(6) ? null : reader.GetString(6),
                Id       = reader.IsDBNull(7) ? null : reader.GetString(7),
            });
        }
        return results.ToArray();
    }
}
