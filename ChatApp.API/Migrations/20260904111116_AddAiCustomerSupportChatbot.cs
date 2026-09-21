using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ChatApp.API.Migrations
{
    public partial class AddAiCustomerSupportChatbot : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChatSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KnowledgeBase",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Topic = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnowledgeBase", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tickets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<int>(type: "int", nullable: false),
                    Issue = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Priority = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false, defaultValue: "Open"),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tickets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatMessages_ChatSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "ChatSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "KnowledgeBase",
                columns: new[] { "Id", "Content", "Topic" },
                values: new object[,]
                {
                    { 1, "To reset your password, click on 'Forgot Password' on the login screen. You will receive an email with a 6-digit verification code. Enter the code along with your current password to choose a new password.", "Password Reset" },
                    { 2, "We offer a 14-day money-back guarantee for all paid subscriptions. If you are not completely satisfied within the first 14 days of your initial purchase or renewal, contact billing support for a full refund.", "Refund Policy" },
                    { 3, "You can cancel your subscription at any time under Settings > Billing > Manage Subscription. Once cancelled, your subscription will remain active until the end of your current billing period with no further charges.", "Subscription Cancellation" },
                    { 4, "Subscriptions are billed automatically on a monthly or annual cycle. Invoices and receipts are emailed to the account owner after each payment and can also be downloaded directly from Settings > Billing > Invoices.", "Billing Cycle & Invoicing" },
                    { 5, "Our human customer support team is available Monday through Friday, 9:00 AM to 6:00 PM EST. You can submit a support ticket directly through this chat, or email support@chatapp.com for assistance.", "Contact Support" },
                    { 6, "To permanently delete your account, visit Settings > Account > Delete Account. Please be aware that deleting your account will permanently remove all conversations, uploaded media, and personal data. This action cannot be reversed.", "Account Deletion" },
                    { 7, "We accept all major credit and debit cards, including Visa, MasterCard, American Express, and Discover. We also support PayPal for international subscriptions. All payments are processed securely via Stripe with 256-bit encryption.", "Accepted Payment Methods" },
                    { 8, "We are fully GDPR and SOC-2 compliant. Your personal information, chats, and files are encrypted in transit via TLS 1.3 and at rest with AES-256 encryption. We never sell or share your personal data with third parties.", "Data Privacy & GDPR Compliance" },
                    { 9, "To enhance account security, navigate to Settings > Security > Two-Factor Authentication. Follow the prompt to scan the QR code with an authenticator app (such as Google Authenticator or Microsoft Authenticator) and verify with the 6-digit code.", "Two-Factor Authentication (2FA)" },
                    { 10, "You can upgrade or downgrade your plan at any time from Settings > Subscription. Upgrades take effect immediately with prorated billing. Free plans include up to 100 messages per day, while Pro plans offer unlimited messaging, priority support, and team collaboration features.", "Plan Upgrades & Feature Limits" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_CreatedAt",
                table: "ChatMessages",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_SessionId",
                table: "ChatMessages",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_CreatedAt",
                table: "ChatSessions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_CustomerId",
                table: "ChatSessions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_KnowledgeBase_Topic",
                table: "KnowledgeBase",
                column: "Topic");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CreatedAt",
                table: "Tickets",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CustomerId",
                table: "Tickets",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Status",
                table: "Tickets",
                column: "Status");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatMessages");

            migrationBuilder.DropTable(
                name: "KnowledgeBase");

            migrationBuilder.DropTable(
                name: "Tickets");

            migrationBuilder.DropTable(
                name: "ChatSessions");
        }
    }
}
