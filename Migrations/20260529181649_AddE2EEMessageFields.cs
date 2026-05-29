using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace workspace.Migrations
{
    /// <inheritdoc />
    public partial class AddE2EEMessageFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Content",
                table: "messages");

            migrationBuilder.AddColumn<string>(
                name: "PublicKey",
                table: "users",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PublicKeyUpdatedAt",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EncryptedKey",
                table: "messages",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EncryptedKeyForSender",
                table: "messages",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EncryptedMessage",
                table: "messages",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Iv",
                table: "messages",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PublicKey",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PublicKeyUpdatedAt",
                table: "users");

            migrationBuilder.DropColumn(
                name: "EncryptedKey",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "EncryptedKeyForSender",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "EncryptedMessage",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "Iv",
                table: "messages");

            migrationBuilder.AddColumn<string>(
                name: "Content",
                table: "messages",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");
        }
    }
}
