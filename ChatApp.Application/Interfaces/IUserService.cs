using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChatApp.Application.DTOs;

namespace ChatApp.Application.Interfaces
{
    /// <summary>
    /// Service interface for user-related operations
    /// </summary>
    public interface IUserService
    {
        Task<UserDto> GetCurrentUserAsync(Guid userId);
        Task<UserDto> UpdateProfileAsync(Guid userId, UpdateProfileDto dto);
        Task<UserDto> UpdateProfilePictureAsync(Guid userId, string? profilePictureUrl);
        Task<List<UserDto>> SearchUsersAsync(string query, Guid excludeUserId);
        Task<List<UserDto>> GetAllUsersAsync(Guid excludeUserId, int skip = 0, int take = 50);
    }
}
