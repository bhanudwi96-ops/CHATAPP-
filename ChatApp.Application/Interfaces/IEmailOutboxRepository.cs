using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ChatApp.Domain.Entities;

namespace ChatApp.Application.Interfaces
{
    public interface IEmailOutboxRepository
    {
        Task AddAsync(EmailOutbox outbox, CancellationToken cancellationToken = default);
        Task<List<EmailOutbox>> GetPendingBatchAsync(int batchSize = 10, CancellationToken cancellationToken = default);
        Task UpdateStatusAsync(EmailOutbox outbox, CancellationToken cancellationToken = default);
    }
}
