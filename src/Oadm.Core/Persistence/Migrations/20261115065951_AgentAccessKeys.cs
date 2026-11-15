using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oadm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentAccessKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The access keys of the first design (rows of AuthTokens, never released) end: a key is now a row of AccessKeys with
            // a role and devices of its own, and an old key row must never be read as a login session.
            migrationBuilder.Sql("DELETE FROM AuthTokens WHERE Kind = 1;");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "AuthTokens");

            migrationBuilder.DropColumn(
                name: "LastUsedUtc",
                table: "AuthTokens");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "AuthTokens");

            migrationBuilder.DropColumn(
                name: "ReadOnly",
                table: "AuthTokens");

            migrationBuilder.CreateTable(
                name: "AccessKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RoleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AllDevices = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeviceGroupId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessKeys_DeviceGroups_DeviceGroupId",
                        column: x => x.DeviceGroupId,
                        principalTable: "DeviceGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccessKeys_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessKeys_DeviceGroupId",
                table: "AccessKeys",
                column: "DeviceGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessKeys_NormalizedName",
                table: "AccessKeys",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessKeys_RoleId",
                table: "AccessKeys",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_AccessKeys_TokenHash",
                table: "AccessKeys",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessKeys");

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "AuthTokens",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastUsedUtc",
                table: "AuthTokens",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "AuthTokens",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "ReadOnly",
                table: "AuthTokens",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
