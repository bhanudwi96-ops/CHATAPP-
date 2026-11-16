using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChatApp.API.Services
{
    public record TicketStatusResult(
        bool Found,
        string? ReferenceNumber,
        int? TicketId,
        string? Status,
        string? Priority,
        string? Category,
        string? Subject,
        DateTime? CreatedAt,
        string Message
    );

    public interface ITicketService
    {
        Task<Ticket> CreateTicketAsync(
            int customerId, 
            string issue, 
            string category, 
            string priority, 
            string? customerName = null, 
            string? customerEmail = null, 
            Guid? authenticatedUserId = null);

        Task<TicketStatusResult> GetTicketStatusAsync(
            string? ticketIdOrRef, 
            Guid? authenticatedUserId = null, 
            string? customerEmail = null);
    }

    public class TicketService : ITicketService
    {
        private static readonly Regex SecurityIncidentRegex = new(
            @"\b(leak|leaked|leaking|leakage|breach|breached|breaching|hacked|hack|hacker|compromised|compromise|unauthorized|stolen\s+chat|chats?\s+stolen)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

        public static bool IsSecurityIncident(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return SecurityIncidentRegex.IsMatch(text);
        }

        private readonly ChatDbContext _dbContext;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TicketService> _logger;
        private readonly IConfiguration? _configuration;

        public TicketService(
            ChatDbContext dbContext, 
            IServiceScopeFactory scopeFactory, 
            ILogger<TicketService> logger,
            IConfiguration? configuration = null)
        {
            _dbContext = dbContext;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _configuration = configuration;
        }

        public async Task<Ticket> CreateTicketAsync(
            int customerId, 
            string issue, 
            string category, 
            string priority, 
            string? customerName = null, 
            string? customerEmail = null, 
            Guid? authenticatedUserId = null)
        {
            var cleanIssue = string.IsNullOrWhiteSpace(issue) ? "Customer reported an unspecified issue." : issue.Trim();

            // Deterministic Security Incident Detection:
            // If the user reports chat leaks, breach, hacking, or compromise, deterministically enforce security category & critical/high priority
            var rawCategory = string.IsNullOrWhiteSpace(category) ? "other" : category.Trim().ToLowerInvariant();
            var isSecurity = rawCategory is "security" or "security_incident" || IsSecurityIncident(cleanIssue);

            var safeCategory = rawCategory;
            if (isSecurity)
            {
                safeCategory = "security";
            }
            else if (safeCategory is not ("billing" or "technical" or "account" or "other"))
            {
                safeCategory = "other";
            }

            var safePriority = string.IsNullOrWhiteSpace(priority) ? "medium" : priority.Trim().ToLowerInvariant();
            if (isSecurity)
            {
                safePriority = "critical";
            }
            else if (safePriority is not ("low" or "medium" or "high" or "critical"))
            {
                safePriority = "medium";
            }

            var fullIssue = (!string.IsNullOrWhiteSpace(customerName) || !string.IsNullOrWhiteSpace(customerEmail))
                ? $"[From: {customerName ?? "Customer"} | Email: {customerEmail ?? "Not provided"}] {cleanIssue}"
                : cleanIssue;

            // CustomerId = 0 explicitly indicates an unassigned guest (never silently attribute to Customer #1)
            var safeCustomerId = customerId > 0 ? customerId : 0;

            var ticket = new Ticket
            {
                CustomerId = safeCustomerId,
                Issue = fullIssue,
                Category = safeCategory,
                Priority = safePriority,
                Status = "Open",
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.Tickets.Add(ticket);
            await _dbContext.SaveChangesAsync();

            // Synchronize with SupportTickets table so authenticated users see this ticket in their "My Tickets" tab.
            // SECURITY FIX: Only link UserId if authenticated via verified JWT claims (authenticatedUserId).
            // Never perform unverified lookups by arbitrary email/name from chat input.
            try
            {
                var ticketCat = safeCategory switch
                {
                    "billing" => TicketCategory.Billing,
                    "technical" => TicketCategory.TechnicalIssue,
                    "account" => TicketCategory.AccountHelp,
                    "security" or "security_incident" => TicketCategory.SecurityIncident,
                    _ => isSecurity ? TicketCategory.SecurityIncident : TicketCategory.General
                };

                var subjectPrefix = isSecurity ? "[SECURITY INCIDENT] " : "";
                var baseSubject = cleanIssue.Length > 70 ? cleanIssue[..67] + "..." : cleanIssue;
                var finalSubject = isSecurity ? (subjectPrefix + baseSubject) : (cleanIssue.Length > 80 ? cleanIssue[..77] + "..." : cleanIssue);

                var supportTicket = new SupportTicket
                {
                    Id = Guid.NewGuid(),
                    ReferenceNumber = $"TICK-{ticket.Id}",
                    UserId = authenticatedUserId,
                    UserEmail = !string.IsNullOrWhiteSpace(customerEmail) ? customerEmail.Trim() : "support@chatapp.com",
                    UserName = !string.IsNullOrWhiteSpace(customerName) ? customerName.Trim() : "Valued Customer",
                    Category = ticketCat,
                    Subject = finalSubject,
                    Message = fullIssue,
                    Status = TicketStatus.Open,
                    CreatedAt = ticket.CreatedAt
                };

                _dbContext.SupportTickets.Add(supportTicket);

                // 1. Send receipt/confirmation to customer if email is provided
                if (!string.IsNullOrWhiteSpace(customerEmail))
                {
                    var outboxItem = new EmailOutbox
                    {
                        Id = Guid.NewGuid(),
                        ToEmail = customerEmail.Trim(),
                        ToName = !string.IsNullOrWhiteSpace(customerName) ? customerName.Trim() : "Valued Customer",
                        Subject = isSecurity 
                            ? $"[URGENT SECURITY TICKET #{supportTicket.ReferenceNumber}] Received: {supportTicket.Subject}"
                            : $"[Ticket #{supportTicket.ReferenceNumber}] Request Received: {supportTicket.Subject}",
                        TemplateType = "CustomerSupportConfirmation",
                        PayloadJson = System.Text.Json.JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, string>
                        {
                            { "UserName", !string.IsNullOrWhiteSpace(customerName) ? customerName.Trim() : "Valued Customer" },
                            { "ReferenceNumber", supportTicket.ReferenceNumber },
                            { "Subject", supportTicket.Subject },
                            { "Category", isSecurity ? "SECURITY INCIDENT (URGENT)" : supportTicket.Category.ToString() },
                            { "Message", isSecurity 
                                ? $"We have received your urgent security report regarding potential chat leakage or account compromise. Our security engineering team has been alerted immediately and is investigating. Please ensure you change your password and log out of all active sessions under Settings > Account.\n\nReport details: {supportTicket.Message}"
                                : supportTicket.Message },
                            { "CreatedAt", supportTicket.CreatedAt.AddHours(5.5).ToString("dd-MMM-yyyy hh:mm tt") + " IST" }
                        }),
                        Status = EmailOutboxStatus.Pending,
                        CreatedAt = DateTime.UtcNow,
                        CorrelationId = $"TICKET-CONFIRM-{supportTicket.Id}"
                    };
                    _dbContext.EmailOutboxes.Add(outboxItem);
                }

                // 2. If it's a security incident, immediately enqueue an Urgent Admin Security Alert email to SupportInboxEmail
                if (isSecurity)
                {
                    var adminEmail = _configuration?["EmailSettings:SupportInboxEmail"] ?? "bhanudwi96@gmail.com";
                    if (!string.IsNullOrWhiteSpace(adminEmail))
                    {
                        var adminAlert = new EmailOutbox
                        {
                            Id = Guid.NewGuid(),
                            ToEmail = adminEmail.Trim(),
                            ToName = "ChatApp Security Admin",
                            Subject = $"[ALERT: CRITICAL SECURITY INCIDENT] Ticket #{supportTicket.ReferenceNumber} - {supportTicket.Subject}",
                            TemplateType = "SupportTicketEscalation",
                            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new System.Collections.Generic.Dictionary<string, string>
                            {
                                { "UserName", !string.IsNullOrWhiteSpace(customerName) ? customerName.Trim() : "Valued Customer (Security Alert)" },
                                { "UserEmail", !string.IsNullOrWhiteSpace(customerEmail) ? customerEmail.Trim() : "Not provided" },
                                { "ReferenceNumber", supportTicket.ReferenceNumber },
                                { "Subject", supportTicket.Subject },
                                { "Category", "CRITICAL SECURITY INCIDENT" },
                                { "Message", $"CRITICAL ALERT: A user has reported a chat leak or security compromise.\nUser: {customerName ?? "Guest"} ({customerEmail ?? "No email"})\nAuthenticatedUserId: {authenticatedUserId?.ToString() ?? "Guest"}\nDetails: {fullIssue}" },
                                { "CreatedAt", supportTicket.CreatedAt.AddHours(5.5).ToString("dd-MMM-yyyy hh:mm tt") + " IST" }
                            }),
                            Status = EmailOutboxStatus.Pending,
                            CreatedAt = DateTime.UtcNow,
                            CorrelationId = $"SECURITY-ALERT-{supportTicket.Id}"
                        };
                        _dbContext.EmailOutboxes.Add(adminAlert);
                        _logger.LogWarning("Queued Critical Admin Security Alert Outbox entry for Ticket #{TicketId} (Ref: {Ref}) to {AdminEmail}",
                            ticket.Id, supportTicket.ReferenceNumber, adminEmail);
                    }
                }

                await _dbContext.SaveChangesAsync();
                _logger.LogInformation("Synchronized Ticket #{TicketId} into SupportTickets with Ref {RefNumber} and queued Outbox email (UserId: {UserId})",
                    ticket.Id, supportTicket.ReferenceNumber, authenticatedUserId?.ToString() ?? "Guest");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to synchronize Ticket #{TicketId} into SupportTickets table", ticket.Id);
            }

            _logger.LogInformation("Support ticket #{TicketId} created successfully for customer {CustomerId} (Priority: {Priority}, Name: {Name}, Email: {Email}, UserId: {UserId})", 
                ticket.Id, ticket.CustomerId, ticket.Priority, customerName ?? "Guest", customerEmail ?? "N/A", authenticatedUserId?.ToString() ?? "Guest");

            // Safe background email confirmation using IServiceScopeFactory and detached entity snapshot
            // to avoid ObjectDisposedException or concurrency hazards on the request-scoped context
            var ticketSnapshot = new Ticket
            {
                Id = ticket.Id,
                CustomerId = ticket.CustomerId,
                Issue = ticket.Issue,
                Category = ticket.Category,
                Priority = ticket.Priority,
                Status = ticket.Status,
                CreatedAt = ticket.CreatedAt
            };

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var scopedEmailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
                    await scopedEmailService.SendTicketConfirmationAsync(ticketSnapshot, customerEmail, customerName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send email confirmation for Ticket #{TicketId} in background task", ticketSnapshot.Id);
                }
            });

            return ticket;
        }

        public async Task<TicketStatusResult> GetTicketStatusAsync(
            string? ticketIdOrRef,
            Guid? authenticatedUserId = null,
            string? customerEmail = null)
        {
            var notFoundResult = new TicketStatusResult(
                false, null, null, null, null, null, null, null,
                !string.IsNullOrWhiteSpace(ticketIdOrRef)
                    ? $"No support ticket found '{ticketIdOrRef}'. Please double-check your ticket reference number (e.g. #12004 or TICK-12004)."
                    : "No support ticket found. Please double-check your ticket reference number (e.g. #12004 or TICK-12004).");

            bool IsAuthorized(SupportTicket t)
            {
                if (authenticatedUserId.HasValue)
                    return t.UserId.HasValue && t.UserId.Value == authenticatedUserId.Value;

                if (!string.IsNullOrWhiteSpace(customerEmail))
                    return string.Equals(t.UserEmail?.Trim(), customerEmail.Trim(), StringComparison.OrdinalIgnoreCase);

                return false;
            }

            if (!string.IsNullOrWhiteSpace(ticketIdOrRef))
            {
                var raw = ticketIdOrRef.Trim();
                var clean = raw.StartsWith("#") ? raw[1..].Trim() : raw;
                var isTickPrefix = clean.StartsWith("TICK-", StringComparison.OrdinalIgnoreCase);
                var numPart = isTickPrefix ? clean[5..].Trim() : clean;

                SupportTicket? supportTicket = null;

                if (int.TryParse(numPart, out var numericId))
                {
                    var refNum = $"TICK-{numericId}";
                    supportTicket = await _dbContext.SupportTickets.AsNoTracking()
                        .FirstOrDefaultAsync(st => st.ReferenceNumber == refNum || st.ReferenceNumber == clean);
                }

                if (supportTicket == null && Guid.TryParse(clean, out var ticketGuid))
                {
                    supportTicket = await _dbContext.SupportTickets.AsNoTracking()
                        .FirstOrDefaultAsync(st => st.Id == ticketGuid);
                }

                if (supportTicket == null)
                {
                    supportTicket = await _dbContext.SupportTickets.AsNoTracking()
                        .FirstOrDefaultAsync(st => st.ReferenceNumber == clean || st.ReferenceNumber == $"TICK-{clean}");
                }

                if (supportTicket != null)
                {
                    if (!IsAuthorized(supportTicket))
                    {
                        _logger.LogWarning(
                            "Blocked unauthorized ticket status lookup: Ref='{Ref}', RequesterUserId='{UserId}', RequesterEmail='{Email}'.",
                            supportTicket.ReferenceNumber, authenticatedUserId?.ToString() ?? "none", customerEmail ?? "none");
                        return notFoundResult;
                    }

                    int? parsedId = null;
                    if (supportTicket.ReferenceNumber.StartsWith("TICK-", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(supportTicket.ReferenceNumber[5..], out var pId))
                        parsedId = pId;

                    var ticketEntity = parsedId.HasValue
                        ? await _dbContext.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == parsedId.Value)
                        : null;

                    var priority = ticketEntity?.Priority ?? "medium";
                    var msg = $"Ticket {supportTicket.ReferenceNumber} is currently '{supportTicket.Status}' with '{priority.ToUpperInvariant()}' priority. Category: {supportTicket.Category}. Subject: \"{supportTicket.Subject}\". Created: {supportTicket.CreatedAt:yyyy-MM-dd HH:mm} UTC.";

                    return new TicketStatusResult(true, supportTicket.ReferenceNumber, parsedId, supportTicket.Status.ToString(),
                        priority, supportTicket.Category.ToString(), supportTicket.Subject, supportTicket.CreatedAt, msg);
                }
            }

            if (authenticatedUserId.HasValue)
            {
                var latestSt = await _dbContext.SupportTickets.AsNoTracking()
                    .Where(st => st.UserId == authenticatedUserId.Value)
                    .OrderByDescending(st => st.CreatedAt)
                    .FirstOrDefaultAsync();

                if (latestSt != null)
                {
                    int? parsedId = null;
                    if (latestSt.ReferenceNumber.StartsWith("TICK-", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(latestSt.ReferenceNumber[5..], out var pId))
                        parsedId = pId;

                    var ticketEntity = parsedId.HasValue
                        ? await _dbContext.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == parsedId.Value)
                        : null;

                    var priority = ticketEntity?.Priority ?? "medium";
                    var msg = $"Your latest ticket {latestSt.ReferenceNumber} is currently '{latestSt.Status}' with '{priority.ToUpperInvariant()}' priority. Category: {latestSt.Category}. Subject: \"{latestSt.Subject}\". Created: {latestSt.CreatedAt:yyyy-MM-dd HH:mm} UTC.";
                    return new TicketStatusResult(true, latestSt.ReferenceNumber, parsedId, latestSt.Status.ToString(), priority,
                        latestSt.Category.ToString(), latestSt.Subject, latestSt.CreatedAt, msg);
                }
            }
            else if (!string.IsNullOrWhiteSpace(customerEmail))
            {
                var latestSt = await _dbContext.SupportTickets.AsNoTracking()
                    .Where(st => st.UserEmail == customerEmail.Trim())
                    .OrderByDescending(st => st.CreatedAt)
                    .FirstOrDefaultAsync();

                if (latestSt != null)
                {
                    int? parsedId = null;
                    if (latestSt.ReferenceNumber.StartsWith("TICK-", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(latestSt.ReferenceNumber[5..], out var pId))
                        parsedId = pId;

                    var ticketEntity = parsedId.HasValue
                        ? await _dbContext.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == parsedId.Value)
                        : null;

                    var priority = ticketEntity?.Priority ?? "medium";
                    var msg = $"Your latest ticket {latestSt.ReferenceNumber} is currently '{latestSt.Status}' with '{priority.ToUpperInvariant()}' priority. Category: {latestSt.Category}. Subject: \"{latestSt.Subject}\". Created: {latestSt.CreatedAt:yyyy-MM-dd HH:mm} UTC.";
                    return new TicketStatusResult(true, latestSt.ReferenceNumber, parsedId, latestSt.Status.ToString(), priority,
                        latestSt.Category.ToString(), latestSt.Subject, latestSt.CreatedAt, msg);
                }
            }

            return notFoundResult;
        }
    }
}
