using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuizWeb.Migrations
{
    /// <inheritdoc />
    public partial class FixModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserQuestionStats");

            migrationBuilder.CreateTable(
                name: "Mistakes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    QuestionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChosenChoiceIdsCsv = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mistakes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegistrationKeys",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Used = table.Column<bool>(type: "INTEGER", nullable: false),
                    UsedByUserId = table.Column<string>(type: "TEXT", nullable: true),
                    UsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationKeys", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Mistakes");

            migrationBuilder.DropTable(
                name: "RegistrationKeys");

            migrationBuilder.CreateTable(
                name: "UserQuestionStats",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CorrectEver = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastAnsweredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    QuestionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TimeoutCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    WrongCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserQuestionStats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserQuestionStats_UserId_QuestionId",
                table: "UserQuestionStats",
                columns: new[] { "UserId", "QuestionId" },
                unique: true);
        }
    }
}
