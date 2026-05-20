using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace workspace.Migrations
{
    /// <inheritdoc />
    public partial class AddCalleeUserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CalleeUserId",
                table: "call_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_call_sessions_CalleeUserId",
                table: "call_sessions",
                column: "CalleeUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_call_sessions_CalleeUserId",
                table: "call_sessions");

            migrationBuilder.DropColumn(
                name: "CalleeUserId",
                table: "call_sessions");
        }
    }
}
