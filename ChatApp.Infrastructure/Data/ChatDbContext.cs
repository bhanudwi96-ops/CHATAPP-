using Microsoft.EntityFrameworkCore;
using ChatApp.Domain.Entities;

namespace ChatApp.Infrastructure.Data
{
    public class ChatDbContext : DbContext
    {
        public ChatDbContext(DbContextOptions<ChatDbContext> options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<Message> Messages { get; set; }
        public DbSet<Conversation> Conversations { get; set; }
        public DbSet<ConversationParticipant> ConversationParticipants { get; set; }
        public DbSet<UserConnection> UserConnections { get; set; }
        public DbSet<MessageReaction> MessageReactions { get; set; }
        public DbSet<UserDevice> UserDevices { get; set; }
        public DbSet<SupportTicket> SupportTickets { get; set; }
        public DbSet<EmailOutbox> EmailOutboxes { get; set; }
        public DbSet<OtpVerification> OtpVerifications { get; set; }

        public DbSet<ChatSession> ChatSessions { get; set; } = null!;
        public DbSet<ChatMessage> ChatMessages { get; set; } = null!;
        public DbSet<Ticket> Tickets { get; set; } = null!;
        public DbSet<KnowledgeBaseEntry> KnowledgeBase { get; set; } = null!;

        // Phase 2 — AI Orchestration audit trail
        public DbSet<AiAuditLog> AiAuditLogs { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // User configuration
            modelBuilder.Entity<User>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Username).IsRequired().HasMaxLength(50);
                entity.Property(e => e.Email).IsRequired().HasMaxLength(100);
                entity.Property(e => e.PasswordHash).IsRequired();
                entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(100);
                entity.HasIndex(e => e.Username).IsUnique();
                entity.HasIndex(e => e.Email).IsUnique();
            });

            // Message configuration
            modelBuilder.Entity<Message>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Content).IsRequired();

                entity.HasOne(e => e.Sender)
                    .WithMany(u => u.SentMessages)
                    .HasForeignKey(e => e.SenderId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(e => e.Conversation)
                    .WithMany(c => c.Messages)
                    .HasForeignKey(e => e.ConversationId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(e => e.ReplyToMessage)
                    .WithMany()
                    .HasForeignKey(e => e.ReplyToMessageId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasIndex(e => e.ConversationId);
                entity.HasIndex(e => e.SenderId);
                entity.HasIndex(e => e.SentAt);
                entity.HasIndex(e => e.ReplyToMessageId);

                // Composite indexes for hot query paths
                entity.HasIndex(e => new { e.ConversationId, e.SentAt });
                entity.HasIndex(e => new { e.ConversationId, e.SenderId, e.IsDeleted });
            });

            // Conversation configuration
            modelBuilder.Entity<Conversation>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Name).HasMaxLength(100);

                entity.HasOne(e => e.CreatedBy)
                    .WithMany()
                    .HasForeignKey(e => e.CreatedById)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            // ConversationParticipant configuration
            modelBuilder.Entity<ConversationParticipant>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasOne(e => e.User)
                    .WithMany(u => u.Conversations)
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(e => e.Conversation)
                    .WithMany(c => c.Participants)
                    .HasForeignKey(e => e.ConversationId)
                    .OnDelete(DeleteBehavior.Cascade);

                // Ensure a user can't be added to the same conversation twice
                entity.HasIndex(e => new { e.ConversationId, e.UserId }).IsUnique();
                // Composite index for "get user conversations" query
                entity.HasIndex(e => new { e.UserId, e.LeftAt });
            });

            // UserConnection configuration
            modelBuilder.Entity<UserConnection>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.ConnectionId).IsRequired();

                entity.HasOne(e => e.User)
                    .WithMany(u => u.Connections)
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(e => e.ConnectionId);
                entity.HasIndex(e => e.UserId);
                // Composite index for presence checks
                entity.HasIndex(e => new { e.UserId, e.IsActive });
            });

            // MessageReaction configuration
            modelBuilder.Entity<MessageReaction>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Emoji).IsRequired().HasMaxLength(20);

                entity.HasOne(e => e.Message)
                    .WithMany(m => m.Reactions)
                    .HasForeignKey(e => e.MessageId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(e => e.User)
                    .WithMany()
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasIndex(e => new { e.MessageId, e.UserId, e.Emoji }).IsUnique();
            });

            // UserDevice configuration
            modelBuilder.Entity<UserDevice>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.DeviceToken).IsRequired().HasMaxLength(512);

                entity.HasOne(e => e.User)
                    .WithMany()
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.Cascade);

                // One token per user (upsert pattern)
                entity.HasIndex(e => new { e.UserId, e.DeviceToken }).IsUnique();
                entity.HasIndex(e => e.UserId);
            });

            // SupportTicket configuration
            modelBuilder.Entity<SupportTicket>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.ReferenceNumber).IsRequired().HasMaxLength(20);
                entity.Property(e => e.UserEmail).IsRequired().HasMaxLength(150);
                entity.Property(e => e.UserName).HasMaxLength(100);
                entity.Property(e => e.Subject).IsRequired().HasMaxLength(200);
                entity.Property(e => e.Message).IsRequired();

                entity.HasOne(e => e.User)
                    .WithMany()
                    .HasForeignKey(e => e.UserId)
                    .OnDelete(DeleteBehavior.SetNull);

                entity.HasIndex(e => e.ReferenceNumber).IsUnique();
                entity.HasIndex(e => e.UserId);
                entity.HasIndex(e => e.CreatedAt);
                entity.HasIndex(e => e.Status);
            });

            // EmailOutbox configuration
            modelBuilder.Entity<EmailOutbox>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.ToEmail).IsRequired().HasMaxLength(150);
                entity.Property(e => e.ToName).HasMaxLength(100);
                entity.Property(e => e.Subject).IsRequired().HasMaxLength(200);
                entity.Property(e => e.TemplateType).IsRequired().HasMaxLength(100);
                entity.Property(e => e.PayloadJson).IsRequired();
                entity.Property(e => e.CorrelationId).HasMaxLength(100);

                // Index for high performance polling by background worker
                entity.HasIndex(e => new { e.Status, e.NextAttemptAt, e.CreatedAt });
                entity.HasIndex(e => e.CorrelationId);
            });

            // OtpVerification configuration
            modelBuilder.Entity<OtpVerification>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Recipient).IsRequired().HasMaxLength(150);
                entity.Property(e => e.OtpHash).IsRequired().HasMaxLength(128);
                entity.Property(e => e.ResetToken).HasMaxLength(128);

                entity.HasIndex(e => new { e.Recipient, e.Purpose, e.IsUsed });
                entity.HasIndex(e => e.ResetToken);
                entity.HasIndex(e => e.ExpiresAt);
            });

            // ChatSession configuration
            modelBuilder.Entity<ChatSession>(entity =>
            {
                entity.ToTable("ChatSessions");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.CustomerId).IsRequired();
                entity.Property(e => e.CreatedAt).IsRequired();
                entity.HasIndex(e => e.CustomerId);
                entity.HasIndex(e => e.CreatedAt);
            });

            // ChatMessage configuration
            modelBuilder.Entity<ChatMessage>(entity =>
            {
                entity.ToTable("ChatMessages");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Role).IsRequired().HasMaxLength(20);
                entity.Property(e => e.Content).IsRequired();
                entity.Property(e => e.CreatedAt).IsRequired();

                entity.HasOne(e => e.Session)
                    .WithMany(s => s.Messages)
                    .HasForeignKey(e => e.SessionId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasIndex(e => e.SessionId);
                entity.HasIndex(e => e.CreatedAt);
            });

            // Ticket configuration
            modelBuilder.Entity<Ticket>(entity =>
            {
                entity.ToTable("Tickets");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedOnAdd();
                entity.Property(e => e.CustomerId).IsRequired();
                entity.Property(e => e.Issue).IsRequired();
                entity.Property(e => e.Category).IsRequired().HasMaxLength(100);
                entity.Property(e => e.Priority).IsRequired().HasMaxLength(20);
                entity.Property(e => e.Status).IsRequired().HasMaxLength(20).HasDefaultValue("Open");
                entity.Property(e => e.CreatedAt).IsRequired();

                entity.HasIndex(e => e.CustomerId);
                entity.HasIndex(e => e.CreatedAt);
                entity.HasIndex(e => e.Status);
            });

            // KnowledgeBase configuration & seeding
            modelBuilder.Entity<KnowledgeBaseEntry>(entity =>
            {
                entity.ToTable("KnowledgeBase");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedOnAdd();
                entity.Property(e => e.Topic).IsRequired().HasMaxLength(200);
                entity.Property(e => e.Content).IsRequired();

                entity.HasIndex(e => e.Topic);

                entity.HasData(
                    new KnowledgeBaseEntry
                    {
                        Id = 1,
                        Topic = "Password Reset",
                        Content = "To reset your password, click on 'Forgot Password' on the login screen. You will receive an email with a 6-digit verification code. Enter the code along with your current password to choose a new password."
                    },
                    new KnowledgeBaseEntry
                    {
                        Id = 2,
                        Topic = "Refund Policy",
                        Content = "We offer a 14-day money-back guarantee for all paid subscriptions. If you are not completely satisfied within the first 14 days of your initial purchase or renewal, contact billing support for a full refund."
                    },
                    new KnowledgeBaseEntry
                    {
                        Id = 3,
                        Topic = "Subscription Cancellation",
                        Content = "You can cancel your subscription at any time under Settings > Billing > Manage Subscription. Once cancelled, your subscription will remain active until the end of your current billing period with no further charges."
                    },
                    new KnowledgeBaseEntry
                    {
                        Id = 4,
                        Topic = "Billing Cycle & Invoicing",
                        Content = "Subscriptions are billed automatically on a monthly or annual cycle. Invoices and receipts are emailed to the account owner after each payment and can also be downloaded directly from Settings > Billing > Invoices."
                    },
                    new KnowledgeBaseEntry
                    {
                        Id = 5,
                        Topic = "Contact Support",
                        Content = "Our human customer support team is available Monday through Friday, 9:00 AM to 6:00 PM EST. You can submit a support ticket directly through this chat, or email support@chatapp.com for assistance."
                    },
                    new KnowledgeBaseEntry
                    {
                        Id = 6,
                        Topic = "Account Deletion",
                        Content = "To permanently delete your account, visit Settings > Account > Delete Account. Please be aware that deleting your account will permanently remove all conversations, uploaded media, and personal data. This action cannot be reversed."
                    },
                    new KnowledgeBaseEntry
                    {
                        Id = 7,
                        Topic = "Accepted Payment Methods",
                        Content = "We accept all major credit and debit cards, including Visa, MasterCard, American Express, and Discover. We also support PayPal for international subscriptions. All payments are processed securely via Stripe with 256-bit encryption."
                    },
                    new KnowledgeBaseEntry
                    {
                        Id = 8,
                        Topic = "Data Privacy & GDPR Compliance",
                        Content = "We are fully GDPR and SOC-2 compliant. Your personal information, chats, and files are encrypted in transit via TLS 1.3 and at rest with AES-256 encryption. We never sell or share your personal data with third parties."
                    },
                    new KnowledgeBaseEntry
                    {
                        Id = 9,
                        Topic = "Two-Factor Authentication (2FA)",
                        Content = "To enhance account security, navigate to Settings > Security > Two-Factor Authentication. Follow the prompt to scan the QR code with an authenticator app (such as Google Authenticator or Microsoft Authenticator) and verify with the 6-digit code."
                    },
                    new KnowledgeBaseEntry
                    {
                        Id = 10,
                        Topic = "Plan Upgrades & Feature Limits",
                        Content = "You can upgrade or downgrade your plan at any time from Settings > Subscription. Upgrades take effect immediately with prorated billing. Free plans include up to 100 messages per day, while Pro plans offer unlimited messaging, priority support, and team collaboration features."
                    }
                );
            });

            // Phase 2 — configure new nullable JSON columns (nvarchar(MAX) for embedding vectors & memory)
            modelBuilder.Entity<KnowledgeBaseEntry>(entity =>
            {
                entity.Property(e => e.EmbeddingJson).HasColumnType("nvarchar(max)");
            });

            modelBuilder.Entity<ChatSession>(entity =>
            {
                entity.Property(e => e.MemoryJson).HasColumnType("nvarchar(max)");
                entity.Property(e => e.HistorySummary).HasColumnType("nvarchar(max)");
            });

            // Phase 2 — AiAuditLog table
            modelBuilder.Entity<AiAuditLog>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Intent).IsRequired().HasMaxLength(50);
                entity.Property(e => e.ModelUsed).HasMaxLength(100);
                entity.Property(e => e.TicketRef).HasMaxLength(30);
                entity.Property(e => e.UserMessageSnippet).HasMaxLength(500);
                entity.Property(e => e.KbArticlesUsedJson).HasColumnType("nvarchar(max)");
                entity.HasIndex(e => e.SessionId);
                entity.HasIndex(e => e.Timestamp);
            });
        }
    }
}
