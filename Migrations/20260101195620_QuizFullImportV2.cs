using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuizWeb.Migrations
{
    /// <inheritdoc />
    public partial class QuizFullImportV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TextKey",
                table: "Questions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TextKey",
                table: "Questions",
                type: "TEXT",
                nullable: true);
        }
    }
}
