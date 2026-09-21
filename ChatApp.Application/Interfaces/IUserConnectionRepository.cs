using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChatApp.Domain.Entities;

namespace ChatApp.Application.Interfaces
{
    public interface IUserConnectionRepository
    {
        Task<UserConnection> AddConnectionAsync(Guid userId, string connectionId, string userAgent);
        Task RemoveConnectionAsync(string connectionId);
        Task<List<UserConnection>> GetUserConnectionsAsync(Guid userId);
        Task<bool> IsUserOnlineAsync(Guid userId);
        Task<List<Guid>> GetOnlineUserIdsAsync(IEnumerable<Guid> userIds);
    }
}
