using System;

namespace ChatApp.Domain.Entities
{
    public enum TicketCategory
    {
        General = 0,
        TechnicalIssue = 1,
        AccountHelp = 2,
        Billing = 3,
        FeatureRequest = 4
    }

    public enum TicketStatus
    {
        Open = 0,
        InProgress = 1,
        Resolved = 2,
        Closed = 3
    }

    public class SupportTicket
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string ReferenceNumber { get; set; } = string.Empty;
        public Guid? UserId { get; set; }
        public string UserEmail { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public TicketCategory Category { get; set; } = TicketCategory.General;
        public string Subject { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public TicketStatus Status { get; set; } = TicketStatus.Open;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ResolvedAt { get; set; }

        public User? User { get; set; }
    }
}
