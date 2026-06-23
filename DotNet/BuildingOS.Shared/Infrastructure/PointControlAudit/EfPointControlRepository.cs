using BuildingOS.Shared.Domain.Grouping;
using BuildingOS.Shared.Domain.PointControl;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BuildingOS.Shared.Infrastructure.PointControlAudit;

public sealed class EfPointControlRepository : IPointControlRepository
{
    private const string UniqueViolation = "23505";

    private readonly RelationalDbContext _context;

    public EfPointControlRepository(RelationalDbContext context) => _context = context;

    public async Task<PointControlInfo?> GetPointControlInfoAsync(Guid id)
    {
        var entry = await _context.PointControlAudits
            .FindAsync(id)
            .ConfigureAwait(false);
        return entry is null ? null : PointControlAuditSerializer.ToDomain(entry);
    }

    public async Task CreatePointControlInfoAsync(PointControlInfo info)
    {
        var entry = PointControlAuditSerializer.ToEntry(info);
        _context.PointControlAudits.Add(entry);
        try
        {
            await _context.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && pg.SqlState == UniqueViolation)
        {
            // PK already exists — equivalent to ON CONFLICT DO NOTHING
            _context.Entry(entry).State = EntityState.Detached;
        }
    }

    public async Task UpdatePointControlInfoAsync(PointControlInfo info)
    {
        var entry = await _context.PointControlAudits
            .FindAsync(info.id)
            .ConfigureAwait(false);
        if (entry is null) return;

        PointControlAuditSerializer.ApplyResult(entry, info);
        await _context.SaveChangesAsync().ConfigureAwait(false);
    }
}
