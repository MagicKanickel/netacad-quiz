using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuizWeb.Migrations
{
    /// <inheritdoc />
    public partial class AddTextKeyToQuestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TextKey",
                table: "Questions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TextKey",
                table: "Questions");
        }
    }
}
