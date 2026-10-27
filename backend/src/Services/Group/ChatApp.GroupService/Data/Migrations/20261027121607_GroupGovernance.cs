using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatApp.GroupService.Data.Migrations
{
    /// <inheritdoc />
    public partial class GroupGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "invite_token_hash",
                table: "groups",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "requires_approval",
                table: "groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "group_audits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_audits", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "group_join_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reviewed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_join_requests", x => x.id);
                    table.CheckConstraint("ck_join_request_status", "status IN ('Pending','Approved','Rejected','Cancelled')");
                    table.ForeignKey(
                        name: "fk_group_join_requests_groups_group_id",
                        column: x => x.group_id,
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_groups_invite_token_hash",
                table: "groups",
                column: "invite_token_hash",
                unique: true,
                filter: "invite_token_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_group_single_owner",
                table: "group_members",
                column: "group_id",
                unique: true,
                filter: "role = 'Owner'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_group_members_role",
                table: "group_members",
                sql: "role IN ('Owner', 'Admin', 'Member')");

            migrationBuilder.CreateIndex(
                name: "ix_group_audits_group_id_created_at",
                table: "group_audits",
                columns: new[] { "group_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_group_join_requests_group_id_status_created_at",
                table: "group_join_requests",
                columns: new[] { "group_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_group_join_requests_group_id_user_id",
                table: "group_join_requests",
                columns: new[] { "group_id", "user_id" },
                unique: true,
                filter: "status = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_audits");

            migrationBuilder.DropTable(
                name: "group_join_requests");

            migrationBuilder.DropIndex(
                name: "ix_groups_invite_token_hash",
                table: "groups");

            migrationBuilder.DropIndex(
                name: "ux_group_single_owner",
                table: "group_members");

            migrationBuilder.DropCheckConstraint(
                name: "ck_group_members_role",
                table: "group_members");

            migrationBuilder.DropColumn(
                name: "invite_token_hash",
                table: "groups");

            migrationBuilder.DropColumn(
                name: "requires_approval",
                table: "groups");
        }
    }
}
