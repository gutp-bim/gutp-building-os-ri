using Microsoft.EntityFrameworkCore;

namespace BuildingOS.Shared
{
    public class ColdTelemetryContext(DbContextOptions<ColdTelemetryContext> options) : DbContext(options)
    {
        public DbSet<ValidTelemetryData> Telemetries { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ValidTelemetryData>(entity =>
            {
                entity.ToTable("sensordata"); // テーブル名

                entity.HasNoKey(); // Lakehouse テーブルには主キーが無いのでこれが必須！

                entity.Property(e => e.Building).HasColumnName("building");
                entity.Property(e => e.Data).HasColumnName("data");
                entity.Property(e => e.Datetime).HasColumnName("datetime");
                entity.Property(e => e.DeviceId).HasColumnName("device_id");
                entity.Property(e => e.Id).HasColumnName("id");
                entity.Property(e => e.Name).HasColumnName("name");
                entity.Property(e => e.PointId).HasColumnName("point_id");
                entity.Property(e => e.Value).HasColumnName("value");
            });
        }
    }
}