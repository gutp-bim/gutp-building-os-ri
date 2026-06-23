using Npgsql;

namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// Queries TimescaleDB continuous aggregates (telemetry_hourly / telemetry_daily).
/// Throws on SQL errors; callers (OssTelemetryQueryRouter) catch and degrade to raw warm.
/// </summary>
public class NpgsqlAggregatedTelemetryStore : IAggregatedTelemetryStore
{
    private const string HourlyView = "telemetry_hourly";
    private const string DailyView = "telemetry_daily";

    private readonly string _connectionString;

    public NpgsqlAggregatedTelemetryStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public Task<ValidTelemetryData[]> QueryHourlyAsync(
        string pointId, DateTime start, DateTime end,
        CancellationToken cancellationToken = default)
        => QueryViewAsync(HourlyView, pointId, start, end, cancellationToken);

    public Task<ValidTelemetryData[]> QueryDailyAsync(
        string pointId, DateTime start, DateTime end,
        CancellationToken cancellationToken = default)
        => QueryViewAsync(DailyView, pointId, start, end, cancellationToken);

    private async Task<ValidTelemetryData[]> QueryViewAsync(
        string view, string pointId, DateTime start, DateTime end,
        CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(
            $@"SELECT time, point_id, building, device_id, name, value
               FROM {view}
               WHERE point_id = @pointId
                 AND time >= @start
                 AND time <= @end
               ORDER BY time",
            conn);

        cmd.Parameters.AddWithValue("pointId", pointId);
        cmd.Parameters.AddWithValue("start", start.ToUniversalTime());
        cmd.Parameters.AddWithValue("end", end.ToUniversalTime());

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
            });
        }

        return results.ToArray();
    }
}
