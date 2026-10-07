using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatApp.NotificationService.Data.Migrations
{
    /// <inheritdoc />
    public partial class MemberSnapshotTombstone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Dòng có sẵn từ Bước 2a là thành viên (cách cũ xóa dòng khi bị xóa) → is_member = true.
            // last_event_at = 0001-01-01, last_event_id = 0...0: "cũ nhất có thể" → mọi sự kiện thật đến sau đều được áp.
            // (Sửa tay giá trị mặc định do EF sinh: false → true.)
            migrationBuilder.AddColumn<bool>(
                name: "is_member",
                table: "group_member_snapshots",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_event_at",
                table: "group_member_snapshots",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "last_event_id",
                table: "group_member_snapshots",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_member",
                table: "group_member_snapshots");

            migrationBuilder.DropColumn(
                name: "last_event_at",
                table: "group_member_snapshots");

            migrationBuilder.DropColumn(
                name: "last_event_id",
                table: "group_member_snapshots");
        }
    }
}
