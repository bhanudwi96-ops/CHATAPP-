using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ChatApp.Application.DTOs;
using ChatApp.Application.Enums;
using ChatApp.Application.Interfaces;
using ChatApp.Domain.Entities;

namespace ChatApp.Application.Services
{
    public class SupportTicketService : ISupportTicketService
    {
        private readonly ISupportTicketRepository _ticketRepository;

        public SupportTicketService(ISupportTicketRepository ticketRepository)
        {
            _ticketRepository = ticketRepository;
        }

        public async Task<SupportTicketDto> CreateTicketAsync(CreateSupportTicketDto dto, Guid? userId = null)
        {
            var refNumber = $"TICK-{Guid.NewGuid().ToString("N")[..6].ToUpper()}";

            var ticket = new SupportTicket
            {
                ReferenceNumber = refNumber,
                UserId = userId,
                UserEmail = dto.UserEmail.Trim(),
                UserName = string.IsNullOrWhiteSpace(dto.UserName) ? "Valued Customer" : dto.UserName.Trim(),
                Category = dto.Category,
                Subject = dto.Subject.Trim(),
                Message = dto.Message.Trim(),
                Status = TicketStatus.Open,
                CreatedAt = DateTime.UtcNow
            };

            var outboxMessages = new List<EmailOutbox>
            {
                // 1. Auto-reply Confirmation to User
                new EmailOutbox
                {
                    ToEmail = ticket.UserEmail,
                    ToName = ticket.UserName,
                    Subject = $"[Ticket #{ticket.ReferenceNumber}] Request Received: {ticket.Subject}",
                    TemplateType = EmailTemplateType.CustomerSupportConfirmation.ToString(),
                    PayloadJson = JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        { "UserName", ticket.UserName },
                        { "ReferenceNumber", ticket.ReferenceNumber },
                        { "Subject", ticket.Subject },
                        { "Category", ticket.Category.ToString() },
                        { "Message", ticket.Message },
                        { "CreatedAt", ticket.CreatedAt.AddHours(5.5).ToString("dd-MMM-yyyy hh:mm tt") + " IST" }
                    }),
                    CorrelationId = $"TICKET-CONFIRM-{ticket.Id}"
                },
                // 2. Escalation Alert to Support Inbox
                new EmailOutbox
                {
                    ToEmail = "SUPPORT_INBOX_PLACEHOLDER", // Replaced with EmailSettings.SupportInboxEmail at dispatch
                    ToName = "ChatApp Support Desk",
                    Subject = $"[NEW TICKET] #{ticket.ReferenceNumber} - {ticket.Category} - {ticket.Subject}",
                    TemplateType = EmailTemplateType.SupportTicketEscalation.ToString(),
                    PayloadJson = JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        { "ReferenceNumber", ticket.ReferenceNumber },
                        { "UserEmail", ticket.UserEmail },
                        { "UserName", ticket.UserName },
                        { "Category", ticket.Category.ToString() },
                        { "Subject", ticket.Subject },
                        { "Message", ticket.Message },
                        { "CreatedAt", ticket.CreatedAt.AddHours(5.5).ToString("dd-MMM-yyyy hh:mm tt") + " IST" }
                    }),
                    CorrelationId = $"TICKET-ESCALATE-{ticket.Id}"
                }
            };

            // Atomically commit Ticket + Outbox in one single SQL transaction
            var savedTicket = await _ticketRepository.CreateWithOutboxAsync(ticket, outboxMessages);
            return MapToDto(savedTicket);
        }

        public async Task<List<SupportTicketDto>> GetUserTicketsAsync(Guid userId)
        {
            var tickets = await _ticketRepository.GetByUserIdAsync(userId);
            return tickets.Select(MapToDto).ToList();
        }

        private static SupportTicketDto MapToDto(SupportTicket t) => new()
        {
            Id = t.Id,
            ReferenceNumber = t.ReferenceNumber,
            UserEmail = t.UserEmail,
            UserName = t.UserName,
            Category = t.Category.ToString(),
            Subject = t.Subject,
            Message = t.Message,
            Status = t.Status.ToString(),
            CreatedAt = t.CreatedAt
        };
    }
}
