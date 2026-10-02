namespace BuildingOS.Shared.Domain.Grouping;

using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Configuration;
using BuildingOS.Shared.Domain.GatewayPointListCache;
using BuildingOS.Shared.Domain.Health;
using BuildingOS.Shared.Domain.Grouping.Entities;
using BuildingOS.Shared.Domain.PointControl;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// PostgreSQL (OSS共有インスタンス) 用のDbContext
/// ResourceGroupとGroupResourceItemを管理（将来的に他のエンティティも追加予定）
/// </summary>
public class RelationalDbContext : DbContext
{
    public DbSet<ResourceGroup> ResourceGroups => Set<ResourceGroup>();
    public DbSet<GroupResourceItem> GroupResourceItems => Set<GroupResourceItem>();
    public DbSet<ResourceIdMapping> ResourceIdMappings => Set<ResourceIdMapping>();
    public DbSet<SystemConfigEntry> SystemConfigEntries => Set<SystemConfigEntry>();
    public DbSet<PointControlAuditEntry> PointControlAudits => Set<PointControlAuditEntry>();
    public DbSet<AdminAuditEntry> AdminAudits => Set<AdminAuditEntry>();
    public DbSet<GatewayPointListCacheEntry> GatewayPointListCacheEntries => Set<GatewayPointListCacheEntry>();
    public DbSet<HealthEventEntry> HealthEvents => Set<HealthEventEntry>();

    public RelationalDbContext(DbContextOptions<RelationalDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ResourceGroup>(entity =>
        {
            entity.ToTable("resource_groups");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(100);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.CreatedBy).HasMaxLength(200);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => e.Name);
            entity.HasIndex(e => e.CreatedBy);
        });

        modelBuilder.Entity<GroupResourceItem>(entity =>
        {
            entity.ToTable("group_resource_items");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(100);
            entity.Property(e => e.GroupId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ResourceType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.ResourceId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.CreatedAt).IsRequired();

            entity.HasOne(e => e.Group)
                  .WithMany(g => g.ResourceItems)
                  .HasForeignKey(e => e.GroupId)
                  .OnDelete(DeleteBehavior.Cascade);

            // 同一グループ内で同じリソースは1つだけ
            entity.HasIndex(e => new { e.GroupId, e.ResourceType, e.ResourceId })
                  .IsUnique();

            // リソースからグループを逆引きするためのインデックス
            entity.HasIndex(e => new { e.ResourceType, e.ResourceId });
        });

        modelBuilder.Entity<ResourceIdMapping>(entity =>
        {
            entity.ToTable("resource_id_mappings");
            entity.HasKey(e => e.HashedId);
            entity.Property(e => e.HashedId).HasMaxLength(56);
            entity.Property(e => e.ResourceType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.OriginalId).IsRequired().HasMaxLength(500);
            entity.Property(e => e.DisplayName).HasMaxLength(500);
            entity.Property(e => e.CreatedAt).IsRequired();

            // リソースタイプで検索するためのインデックス
            entity.HasIndex(e => e.ResourceType);
        });

        modelBuilder.Entity<SystemConfigEntry>(entity =>
        {
            entity.ToTable("system_config");
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(200);
            entity.Property(e => e.Value).IsRequired().HasMaxLength(2000);
            entity.Property(e => e.Source).IsRequired().HasMaxLength(50);
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.Property(e => e.UpdatedBy).HasMaxLength(200);
        });

        modelBuilder.Entity<PointControlAuditEntry>(entity =>
        {
            entity.ToTable("point_control_audit");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.PointId).HasColumnName("point_id");
            entity.Property(e => e.Request).HasColumnName("request").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.Result).HasColumnName("result").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.Property(e => e.CompletedAt).HasColumnName("completed_at");
            // #461: same column names / types as admin_audit below, so the two audit trails join.
            entity.Property(e => e.ActorSub).HasColumnName("actor_sub").IsRequired().HasMaxLength(200);
            entity.Property(e => e.ActorName).HasColumnName("actor_name").HasMaxLength(200);
            entity.HasIndex(e => new { e.PointId, e.CreatedAt }).HasDatabaseName("IX_point_control_audit_point_id_created_at");
        });

        modelBuilder.Entity<HealthEventEntry>(entity =>
        {
            entity.ToTable("health_event");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.SubjectType).HasColumnName("subject_type").IsRequired().HasMaxLength(20);
            entity.Property(e => e.SubjectId).HasColumnName("subject_id").IsRequired().HasMaxLength(500);
            entity.Property(e => e.Kind).HasColumnName("kind").IsRequired().HasMaxLength(30);
            entity.Property(e => e.Severity).HasColumnName("severity").IsRequired().HasMaxLength(20);
            entity.Property(e => e.RaisedAt).HasColumnName("raised_at").IsRequired();
            entity.Property(e => e.ClearedAt).HasColumnName("cleared_at");
            entity.Property(e => e.AcknowledgedAt).HasColumnName("acknowledged_at");
            entity.Property(e => e.AcknowledgedBy).HasColumnName("acknowledged_by").HasMaxLength(200);
            entity.Property(e => e.AcknowledgedByName).HasColumnName("acknowledged_by_name").HasMaxLength(200);
            entity.Property(e => e.Detail).HasColumnName("detail").HasColumnType("jsonb").IsRequired();
            entity.Ignore(e => e.IsOpen);

            // "Open once per subject × kind" (#455). subject_type + subject_id rather than a nullable
            // point_id / gateway_id pair: PostgreSQL treats NULLs as distinct in a unique index, so a
            // nullable pair would let a gateway event be opened many times.
            entity.HasIndex(e => new { e.SubjectType, e.SubjectId, e.Kind })
                  .IsUnique()
                  .HasFilter("cleared_at IS NULL")
                  .HasDatabaseName("UX_health_event_open_subject_kind");
            // History / list queries: newest first, and per subject.
            entity.HasIndex(e => e.RaisedAt).HasDatabaseName("IX_health_event_raised_at");
            entity.HasIndex(e => new { e.SubjectType, e.SubjectId, e.RaisedAt })
                  .HasDatabaseName("IX_health_event_subject_raised_at");
            entity.HasIndex(e => e.ClearedAt).HasDatabaseName("IX_health_event_cleared_at");
        });

        modelBuilder.Entity<AdminAuditEntry>(entity =>
        {
            entity.ToTable("admin_audit");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.SubjectType).HasColumnName("subject_type").IsRequired().HasMaxLength(50);
            entity.Property(e => e.Action).HasColumnName("action").IsRequired().HasMaxLength(100);
            entity.Property(e => e.TargetId).HasColumnName("target_id").HasMaxLength(500);
            entity.Property(e => e.ActorSub).HasColumnName("actor_sub").IsRequired().HasMaxLength(200);
            entity.Property(e => e.ActorName).HasColumnName("actor_name").HasMaxLength(200);
            entity.Property(e => e.Result).HasColumnName("result").IsRequired().HasMaxLength(20);
            entity.Property(e => e.Detail).HasColumnName("detail").HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
            entity.HasIndex(e => new { e.SubjectType, e.CreatedAt }).HasDatabaseName("IX_admin_audit_subject_type_created_at");
            entity.HasIndex(e => new { e.TargetId, e.CreatedAt }).HasDatabaseName("IX_admin_audit_target_id_created_at");
        });

        modelBuilder.Entity<GatewayPointListCacheEntry>(entity =>
        {
            entity.ToTable("gateway_pointlist_cache");
            entity.HasKey(e => e.GatewayId);
            entity.Property(e => e.GatewayId).HasColumnName("gateway_id").HasMaxLength(200);
            entity.Property(e => e.Etag).HasColumnName("etag").IsRequired().HasMaxLength(200);
            entity.Property(e => e.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.PointCount).HasColumnName("point_count").IsRequired();
            entity.Property(e => e.MaterializedAt).HasColumnName("materialized_at").IsRequired();
        });
    }
}
