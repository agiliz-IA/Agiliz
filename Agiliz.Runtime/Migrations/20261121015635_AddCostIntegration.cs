using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agiliz.Runtime.Migrations
{
    /// <inheritdoc />
    public partial class AddCostIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IntegrationId",
                table: "CostRecords",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IntegrationId",
                table: "CostRecords");
        }
    }
}
