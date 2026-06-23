using Npgsql;

namespace BuildingOS.Shared.Infrastructure.ColdExport;

public sealed class NpgsqlExportDataReader : IExportDataReader
{
    private readonly string _connectionString;

    public NpgsqlExportDataReader(string connectionString) => _connectionString = connectionString;

    public async Task<ValidTelemetryData[]> ReadAsync(
        DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(
            @"SELECT time, point_id, building, device_id, name, value, data::TEXT, id
              FROM telemetry
              WHERE time >= @from AND time < @to
              ORDER BY time",
            conn);
        cmd.Parameters.AddWithValue("from", from.ToUniversalTime());
        cmd.Parameters.AddWithValue("to", to.ToUniversalTime());

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

    public async Task<DateTime?> GetLastExportEndAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(
            "SELECT MAX(chunk_end) FROM cold_export_log WHERE verified = TRUE",
            conn);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is DBNull or null ? null : (DateTime)result;
    }
}
