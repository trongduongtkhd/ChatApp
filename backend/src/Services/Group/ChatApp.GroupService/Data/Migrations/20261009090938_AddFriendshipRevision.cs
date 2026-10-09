using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatApp.GroupService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendshipRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "friendship_revision",
                table: "groups",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "friendship_revision",
                table: "groups");
        }
    }
}
