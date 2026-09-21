using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatApp.API.Migrations
{
    public partial class Phase2_AiOrchestration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmbeddingJson",
                table: "KnowledgeBase",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HistorySummary",
                table: "ChatSessions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MemoryJson",
                table: "ChatSessions",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiAuditLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Intent = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IntentConfidence = table.Column<float>(type: "real", nullable: false),
                    KbArticlesUsedJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModelUsed = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    InputTokens = table.Column<int>(type: "int", nullable: false),
                    OutputTokens = table.Column<int>(type: "int", nullable: false),
                    LatencyMs = table.Column<int>(type: "int", nullable: false),
                    TicketRef = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    UserMessageSnippet = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiAuditLogs_SessionId",
                table: "AiAuditLogs",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AiAuditLogs_Timestamp",
                table: "AiAuditLogs",
                column: "Timestamp");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiAuditLogs");

            migrationBuilder.DropColumn(
                name: "EmbeddingJson",
                table: "KnowledgeBase");

            migrationBuilder.DropColumn(
                name: "HistorySummary",
                table: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "MemoryJson",
                table: "ChatSessions");
        }
    }
}
