using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VPanel.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProxyDirectives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProxyDirectives",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProxyOptions",
                table: "Instances",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProxyDirectives",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "ProxyOptions",
                table: "Instances");
        }
    }
}
