using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using ChatApp.Application.DTOs;
using ChatApp.Application.Enums;
using ChatApp.Application.Interfaces;
using ChatApp.Domain.Entities;

namespace ChatApp.Infrastructure.Email
{
    public class EmailService : IEmailService
    {
        private readonly IEmailOutboxRepository _outboxRepository;

        public EmailService(IEmailOutboxRepository outboxRepository)
        {
            _outboxRepository = outboxRepository;
        }

        public async Task QueueAsync(EmailMessage message)
        {
            var outbox = new EmailOutbox
            {
                ToEmail = message.To,
                ToName = message.ToName ?? message.To,
                Subject = message.Subject,
                TemplateType = message.TemplateType?.ToString() ?? string.Empty,
                PayloadJson = JsonSerializer.Serialize(message.TemplateData),
                CorrelationId = message.CorrelationId
            };

            await _outboxRepository.AddAsync(outbox);
        }

        public async Task QueueTemplateAsync(
            string to, 
            string toName, 
            string subject, 
            EmailTemplateType templateType, 
            Dictionary<string, string> templateData,
            string? correlationId = null)
        {
            await QueueAsync(new EmailMessage
            {
                To = to,
                ToName = toName,
                Subject = subject,
                TemplateType = templateType,
                TemplateData = templateData,
                CorrelationId = correlationId
            });
        }
    }
}
