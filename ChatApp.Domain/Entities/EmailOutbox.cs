using System;

namespace ChatApp.Domain.Entities
{
    public enum EmailOutboxStatus
    {
        Pending = 0,
        Processing = 1,
        Sent = 2,
        Failed = 3
    }

    public class EmailOutbox
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string ToEmail { get; set; } = string.Empty;
        public string ToName { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string TemplateType { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = "{}";
        public EmailOutboxStatus Status { get; set; } = EmailOutboxStatus.Pending;
        public int RetryCount { get; set; } = 0;
        public int MaxRetries { get; set; } = 3;
        public DateTime? NextAttemptAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ProcessedAt { get; set; }
        public string? LastError { get; set; }
        public string? CorrelationId { get; set; }
    }
}
