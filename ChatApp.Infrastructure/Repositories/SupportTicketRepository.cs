using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChatApp.Application.Interfaces;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Infrastructure.Repositories
{
    public class SupportTicketRepository : ISupportTicketRepository
    {
        private readonly ChatDbContext _context;

        public SupportTicketRepository(ChatDbContext context)
        {
            _context = context;
        }

        public async Task<SupportTicket> CreateWithOutboxAsync(SupportTicket ticket, IEnumerable<EmailOutbox> outboxItems)
        {
            await _context.SupportTickets.AddAsync(ticket);
            if (outboxItems != null)
            {
                await _context.EmailOutboxes.AddRangeAsync(outboxItems);
            }
            await _context.SaveChangesAsync(); // Atomic SQL commit
            return ticket;
        }

        public async Task<List<SupportTicket>> GetByUserIdAsync(Guid userId)
        {
            return await _context.SupportTickets
                .Where(t => t.UserId == userId)
                .OrderByDescending(t => t.CreatedAt)
                .ToListAsync();
        }

        public async Task<SupportTicket?> GetByIdAsync(Guid ticketId)
        {
            return await _context.SupportTickets.FirstOrDefaultAsync(t => t.Id == ticketId);
        }
    }
}
