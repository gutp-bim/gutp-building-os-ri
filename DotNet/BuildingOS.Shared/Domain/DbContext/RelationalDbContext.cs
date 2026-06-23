namespace BuildingOS.Shared.Domain.Grouping;

using BuildingOS.Shared.Domain.AdminAudit;
using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Domain.Configuration;
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
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.Property(e => e.UpdatedAt).IsRequired();
            entity.HasIndex(e => e.Name);
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
            entity.HasIndex(e => new { e.PointId, e.CreatedAt }).HasDatabaseName("IX_point_control_audit_point_id_created_at");
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
    }
}
