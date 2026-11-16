using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatApp.API.Migrations
{
    /// <inheritdoc />
    public partial class Sprint1_AddToolAuditFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AuthenticatedUserId",
                table: "AiAuditLogs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlockReason",
                table: "AiAuditLogs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ToolCallsJson",
                table: "AiAuditLogs",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthenticatedUserId",
                table: "AiAuditLogs");

            migrationBuilder.DropColumn(
                name: "BlockReason",
                table: "AiAuditLogs");

            migrationBuilder.DropColumn(
                name: "ToolCallsJson",
                table: "AiAuditLogs");
        }
    }
}
