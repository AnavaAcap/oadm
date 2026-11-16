using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oadm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DelegatedConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DelegationJson",
                table: "AccessKeys",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DelegationJson",
                table: "AccessKeys");
        }
    }
}
