using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AspireCTF.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWriteup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Writeup",
                table: "Tasks",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Writeup",
                table: "Tasks");
        }
    }
}
