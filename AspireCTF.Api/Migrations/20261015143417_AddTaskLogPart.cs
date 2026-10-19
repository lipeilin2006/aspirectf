using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AspireCTF.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskLogPart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Part",
                table: "TaskLogs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Part",
                table: "TaskLogs");
        }
    }
}
