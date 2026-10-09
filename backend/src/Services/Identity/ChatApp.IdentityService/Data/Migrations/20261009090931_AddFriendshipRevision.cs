using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatApp.IdentityService.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendshipRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "revision",
                table: "friendships",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "revision",
                table: "friendships");
        }
    }
}
