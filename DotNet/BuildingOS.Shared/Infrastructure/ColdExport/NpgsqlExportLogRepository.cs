using Npgsql;

namespace BuildingOS.Shared.Infrastructure.ColdExport;

public sealed class NpgsqlExportLogRepository : IExportLogRepository
{
    private readonly string _connectionString;

    public NpgsqlExportLogRepository(string connectionString) => _connectionString = connectionString;

    public async Task<int> InsertAsync(
        DateTime from, DateTime to, string path, long rows, long bytes,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO cold_export_log
                  (chunk_start, chunk_end, parquet_path, rows_exported, bytes_written, verified)
              VALUES (@from, @to, @path, @rows, @bytes, FALSE)
              ON CONFLICT (chunk_start, chunk_end) DO UPDATE
                  SET parquet_path   = EXCLUDED.parquet_path,
                      rows_exported  = EXCLUDED.rows_exported,
                      bytes_written  = EXCLUDED.bytes_written,
                      exported_at    = NOW()
              RETURNING id",
            conn);
        cmd.Parameters.AddWithValue("from", from.ToUniversalTime());
        cmd.Parameters.AddWithValue("to", to.ToUniversalTime());
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("rows", rows);
        cmd.Parameters.AddWithValue("bytes", bytes);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    public async Task SetVerifiedAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand(
            "UPDATE cold_export_log SET verified = TRUE, verified_at = NOW() WHERE id = @id",
            conn);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
