using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace workspace.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountDeletionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AutoDeleteAccountMode",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 4);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastActivityAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "IX_users_AutoDeleteAccountMode_LastActivityAt",
                table: "users",
                columns: new[] { "AutoDeleteAccountMode", "LastActivityAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_AutoDeleteAccountMode_LastActivityAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "AutoDeleteAccountMode",
                table: "users");

            migrationBuilder.DropColumn(
                name: "LastActivityAt",
                table: "users");
        }
    }
}
