using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChatApp.Application.Interfaces;

namespace ChatApp.Application.Services
{
    /// <summary>
    /// Service for managing user presence (online/offline status)
    /// Optimized with batch queries
    /// </summary>
    public class PresenceService : IPresenceService
    {
        private readonly IUserConnectionRepository _connectionRepository;

        public PresenceService(IUserConnectionRepository connectionRepository)
        {
            _connectionRepository = connectionRepository;
        }

        public async Task UserConnectedAsync(Guid userId, string connectionId, string userAgent)
        {
            await _connectionRepository.AddConnectionAsync(userId, connectionId, userAgent);
        }

        public async Task UserDisconnectedAsync(string connectionId)
        {
            await _connectionRepository.RemoveConnectionAsync(connectionId);
        }

        public async Task<bool> IsUserOnlineAsync(Guid userId)
        {
            return await _connectionRepository.IsUserOnlineAsync(userId);
        }

        public async Task<List<Guid>> GetOnlineUsersAsync(List<Guid> userIds)
        {
            if (userIds == null || userIds.Count == 0)
            {
                return new List<Guid>();
            }

            // Perform single batch query
            return await _connectionRepository.GetOnlineUserIdsAsync(userIds);
        }
    }
}
