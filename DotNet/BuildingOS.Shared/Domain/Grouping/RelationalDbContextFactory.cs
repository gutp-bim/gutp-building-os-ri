namespace BuildingOS.Shared.Domain.Grouping;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

/// <summary>
/// EF Core マイグレーション生成用のデザイン時ファクトリ
/// </summary>
public class RelationalDbContextFactory : IDesignTimeDbContextFactory<RelationalDbContext>
{
    public RelationalDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION_STRING")
            ?? "Host=localhost;Port=5433;Database=buildingos;Username=buildingos;Password=buildingos";

        var optionsBuilder = new DbContextOptionsBuilder<RelationalDbContext>();
        optionsBuilder.UseNpgsql(connectionString);

        return new RelationalDbContext(optionsBuilder.Options);
    }
}
