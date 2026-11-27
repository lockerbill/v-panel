using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VPanel.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAdoptedStacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BuildMode",
                table: "Instances",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Build");

            migrationBuilder.AddColumn<string>(
                name: "ComposeFileName",
                table: "Instances",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ComposeProjectName",
                table: "Instances",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeployedProxyAlias",
                table: "Instances",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportInfoJson",
                table: "Instances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TlsInternal",
                table: "Instances",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BuildMode",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "ComposeFileName",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "ComposeProjectName",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "DeployedProxyAlias",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "ImportInfoJson",
                table: "Instances");

            migrationBuilder.DropColumn(
                name: "TlsInternal",
                table: "Instances");
        }
    }
}
