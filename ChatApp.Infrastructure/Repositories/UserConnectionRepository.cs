using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ChatApp.Domain.Entities;
using ChatApp.Application.Interfaces;
using ChatApp.Infrastructure.Data;

namespace ChatApp.Infrastructure.Repositories
{
    /// <summary>
    /// Repository for UserConnection entity (SignalR connection tracking)
    /// Fixed: Uses single SaveChangesAsync per operation for atomic consistency
    /// </summary>
    public class UserConnectionRepository : IUserConnectionRepository
    {
        private readonly ChatDbContext _context;

        public UserConnectionRepository(ChatDbContext context)
        {
            _context = context;
        }

        public async Task<UserConnection> AddConnectionAsync(Guid userId, string connectionId, string userAgent)
        {
            var connection = new UserConnection
            {
                UserId = userId,
                ConnectionId = connectionId,
                UserAgent = userAgent ?? "Unknown",
                ConnectedAt = DateTime.UtcNow,
                IsActive = true
            };

            _context.UserConnections.Add(connection);

            // Update user status in the SAME SaveChanges call for atomicity
            var user = await _context.Users.FindAsync(userId);
            if (user != null)
            {
                user.Status = UserStatus.Online;
                user.LastSeen = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
            return connection;
        }

        public async Task RemoveConnectionAsync(string connectionId)
        {
            var connection = await _context.UserConnections
                .FirstOrDefaultAsync(c => c.ConnectionId == connectionId && c.IsActive);

            if (connection == null) return;

            connection.IsActive = false;
            connection.DisconnectedAt = DateTime.UtcNow;

            // Check if user has other active connections
            var hasOtherConnections = await _context.UserConnections
                .AnyAsync(c => c.UserId == connection.UserId && c.IsActive && c.Id != connection.Id);

            if (!hasOtherConnections)
            {
                var user = await _context.Users.FindAsync(connection.UserId);
                if (user != null)
                {
                    user.Status = UserStatus.Offline;
                    user.LastSeen = DateTime.UtcNow;
                }
            }

            // Single atomic SaveChanges for all changes
            await _context.SaveChangesAsync();
        }

        public async Task<List<UserConnection>> GetUserConnectionsAsync(Guid userId)
        {
            return await _context.UserConnections
                .Where(c => c.UserId == userId && c.IsActive)
                .ToListAsync();
        }

        public async Task<bool> IsUserOnlineAsync(Guid userId)
        {
            return await _context.UserConnections
                .AnyAsync(c => c.UserId == userId && c.IsActive);
        }

        public async Task<List<Guid>> GetOnlineUserIdsAsync(IEnumerable<Guid> userIds)
        {
            var idList = userIds.ToList();
            if (!idList.Any())
            {
                return new List<Guid>();
            }

            return await _context.UserConnections
                .Where(c => idList.Contains(c.UserId) && c.IsActive)
                .Select(c => c.UserId)
                .Distinct()
                .ToListAsync();
        }
    }
}
