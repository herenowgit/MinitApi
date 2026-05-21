using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace workspace.Migrations
{
    /// <inheritdoc />
    public partial class AddOneTimeInviteCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "one_time_invite_codes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UsedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    MaxRedemptions = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    RedemptionCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_one_time_invite_codes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_one_time_invite_codes_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_one_time_invite_codes_users_UsedByUserId",
                        column: x => x.UsedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_one_time_invite_codes_OwnerUserId_ExpiresAtUtc",
                table: "one_time_invite_codes",
                columns: new[] { "OwnerUserId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_one_time_invite_codes_TokenHash",
                table: "one_time_invite_codes",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_one_time_invite_codes_UsedByUserId",
                table: "one_time_invite_codes",
                column: "UsedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "one_time_invite_codes");
        }
    }
}
