using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ChatApp.Application.Interfaces
{
    public interface IPresenceService
    {
        Task UserConnectedAsync(Guid userId, string connectionId, string userAgent);
        Task UserDisconnectedAsync(string connectionId);
        Task<bool> IsUserOnlineAsync(Guid userId);
        Task<List<Guid>> GetOnlineUsersAsync(List<Guid> userIds);
    }
}
