using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChatApp.Application.Interfaces;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Infrastructure.Repositories
{
    public class EmailOutboxRepository : IEmailOutboxRepository
    {
        private readonly ChatDbContext _context;

        public EmailOutboxRepository(ChatDbContext context)
        {
            _context = context;
        }

        public async Task AddAsync(EmailOutbox outbox, CancellationToken cancellationToken = default)
        {
            await _context.EmailOutboxes.AddAsync(outbox, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task<List<EmailOutbox>> GetPendingBatchAsync(int batchSize = 10, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            return await _context.EmailOutboxes
                .Where(e => (e.Status == EmailOutboxStatus.Pending || e.Status == EmailOutboxStatus.Failed)
                            && e.RetryCount < e.MaxRetries
                            && (e.NextAttemptAt == null || e.NextAttemptAt <= now))
                .OrderBy(e => e.CreatedAt)
                .Take(batchSize)
                .ToListAsync(cancellationToken);
        }

        public async Task UpdateStatusAsync(EmailOutbox outbox, CancellationToken cancellationToken = default)
        {
            _context.EmailOutboxes.Update(outbox);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
