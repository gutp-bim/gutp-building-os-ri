using BuildingOS.Shared.Domain.Grouping;
using Microsoft.EntityFrameworkCore;

namespace BuildingOS.Shared.Domain.Configuration;

/// <summary>EF Core-backed <see cref="ISystemConfigStore"/> over the <c>system_config</c> table (#148).</summary>
public sealed class SystemConfigStore : ISystemConfigStore
{
    private readonly RelationalDbContext _context;

    public SystemConfigStore(RelationalDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<SettingOverride>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await _context.SystemConfigEntries
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .Select(r => new SettingOverride(
                r.Key,
                r.Value,
                Enum.TryParse<SettingSource>(r.Source, ignoreCase: true, out var s) ? s : SettingSource.Ui,
                r.UpdatedAt,
                r.UpdatedBy))
            .ToList();
    }

    public async Task UpsertAsync(
        string key, string value, SettingSource source, string? updatedBy, CancellationToken ct = default)
    {
        var existing = await _context.SystemConfigEntries
            .FirstOrDefaultAsync(e => e.Key == key, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _context.SystemConfigEntries.Add(new SystemConfigEntry
            {
                Key = key,
                Value = value,
                Source = source.ToString(),
                UpdatedAt = DateTime.UtcNow,
                UpdatedBy = updatedBy,
            });
        }
        else
        {
            existing.Value = value;
            existing.Source = source.ToString();
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedBy = updatedBy;
        }

        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        var existing = await _context.SystemConfigEntries
            .FirstOrDefaultAsync(e => e.Key == key, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return false;
        }

        _context.SystemConfigEntries.Remove(existing);
        await _context.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}
