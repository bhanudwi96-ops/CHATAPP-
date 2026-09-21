using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChatApp.Domain.Entities;

namespace ChatApp.Application.Interfaces
{
    public interface ISupportTicketRepository
    {
        Task<SupportTicket> CreateWithOutboxAsync(SupportTicket ticket, IEnumerable<EmailOutbox> outboxItems);
        Task<List<SupportTicket>> GetByUserIdAsync(Guid userId);
        Task<SupportTicket?> GetByIdAsync(Guid ticketId);
    }
}
