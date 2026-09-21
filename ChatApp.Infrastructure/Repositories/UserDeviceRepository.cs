using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ChatApp.Application.Interfaces;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Data;

namespace ChatApp.Infrastructure.Repositories
{
    public class UserDeviceRepository : IUserDeviceRepository
    {
        private readonly ChatDbContext _context;

        public UserDeviceRepository(ChatDbContext context)
        {
            _context = context;
        }

        public async Task UpsertAsync(Guid userId, string deviceToken)
        {
            var existing = await _context.UserDevices
                .FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceToken == deviceToken);

            if (existing != null)
            {
                existing.LastUpdated = DateTime.UtcNow;
            }
            else
            {
                _context.UserDevices.Add(new UserDevice
                {
                    UserId = userId,
                    DeviceToken = deviceToken,
                    LastUpdated = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();
        }

        public async Task<string?> GetTokenAsync(Guid userId)
        {
            var device = await _context.UserDevices
                .Where(d => d.UserId == userId)
                .OrderByDescending(d => d.LastUpdated)
                .FirstOrDefaultAsync();

            return device?.DeviceToken;
        }

        public async Task RemoveAsync(Guid userId, string deviceToken)
        {
            var device = await _context.UserDevices
                .FirstOrDefaultAsync(d => d.UserId == userId && d.DeviceToken == deviceToken);

            if (device != null)
            {
                _context.UserDevices.Remove(device);
                await _context.SaveChangesAsync();
            }
        }
    }
}
