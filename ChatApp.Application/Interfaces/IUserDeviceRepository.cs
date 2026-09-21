using System;
using System.Threading.Tasks;

namespace ChatApp.Application.Interfaces
{
    public interface IUserDeviceRepository
    {
        /// <summary>Upsert a device token for a user (one token per user for now).</summary>
        Task UpsertAsync(Guid userId, string deviceToken);

        /// <summary>Get the stored FCM device token for a user, or null if none.</summary>
        Task<string?> GetTokenAsync(Guid userId);

        /// <summary>Remove a device token (e.g. on logout).</summary>
        Task RemoveAsync(Guid userId, string deviceToken);
    }
}
