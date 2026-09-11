using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BuildingOS.Shared.Migrations
{
    /// <inheritdoc />
    public partial class AddPointControlAuditActor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand-written rather than the scaffolded AddColumn calls, for two reasons.
            //
            // 1. IF NOT EXISTS, like the migration that created this table: environments that ran the
            //    legacy Flyway V002__point_control_audit.sql reach here with a table EF never created.
            // 2. The scaffolded form leaves a permanent DEFAULT '' on actor_sub, so any insert that
            //    omits the column would quietly produce an un-attributed row — the exact hole #461 is
            //    closing. The default exists only to fill rows written before this migration; those
            //    predate the actor entirely and cannot be backfilled with a real one, so they get the
            //    same "unknown" sentinel ControlActor uses for an unresolvable principal. Then the
            //    default is dropped and every new row must name its actor.
            migrationBuilder.Sql(@"
                ALTER TABLE point_control_audit
                    ADD COLUMN IF NOT EXISTS actor_sub  VARCHAR(200) NOT NULL DEFAULT 'unknown',
                    ADD COLUMN IF NOT EXISTS actor_name VARCHAR(200);
                ALTER TABLE point_control_audit ALTER COLUMN actor_sub DROP DEFAULT;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE point_control_audit
                    DROP COLUMN IF EXISTS actor_sub,
                    DROP COLUMN IF EXISTS actor_name;
            ");
        }
    }
}
