using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AspireCTF.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAdditionalPrompt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdditionalPrompt",
                table: "Tasks",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdditionalPrompt",
                table: "Tasks");
        }
    }
}
