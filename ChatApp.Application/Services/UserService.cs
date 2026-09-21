using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChatApp.Domain.Entities;
using ChatApp.Application.Interfaces;
using ChatApp.Application.DTOs;
using ChatApp.Application.Exceptions;

namespace ChatApp.Application.Services
{
    /// <summary>
    /// Service for user-related operations
    /// Centralizes user logic that was previously scattered in UserController
    /// </summary>
    public class UserService : IUserService
    {
        private readonly IUserRepository _userRepository;
        private readonly IPresenceService _presenceService;

        public UserService(IUserRepository userRepository, IPresenceService presenceService)
        {
            _userRepository = userRepository;
            _presenceService = presenceService;
        }

        public async Task<UserDto> GetCurrentUserAsync(Guid userId)
        {
            var user = await _userRepository.GetByIdAsync(userId)
                ?? throw new NotFoundException("User", userId);

            return MapToDto(user, UserStatus.Online);
        }

        public async Task<UserDto> UpdateProfileAsync(Guid userId, UpdateProfileDto dto)
        {
            var updated = await _userRepository.UpdateProfileAsync(userId, dto.DisplayName, dto.ProfilePictureUrl)
                ?? throw new NotFoundException("User", userId);

            return MapToDto(updated, UserStatus.Online);
        }

        public async Task<UserDto> UpdateProfilePictureAsync(Guid userId, string? profilePictureUrl)
        {
            var updated = await _userRepository.UpdateProfileAsync(userId, null, profilePictureUrl)
                ?? throw new NotFoundException("User", userId);

            return MapToDto(updated, UserStatus.Online);
        }

        public async Task<List<UserDto>> SearchUsersAsync(string query, Guid excludeUserId)
        {
            var users = await _userRepository.SearchUsersAsync(query, excludeUserId, 30);
            return await MapUsersWithPresence(users);
        }

        public async Task<List<UserDto>> GetAllUsersAsync(Guid excludeUserId, int skip = 0, int take = 50)
        {
            var users = await _userRepository.GetAllAsync(skip, take);
            var otherUsers = users.Where(u => u.Id != excludeUserId).ToList();
            return await MapUsersWithPresence(otherUsers);
        }

        private async Task<List<UserDto>> MapUsersWithPresence(List<User> users)
        {
            var onlineUserIds = await _presenceService.GetOnlineUsersAsync(users.Select(u => u.Id).ToList());
            var onlineSet = new HashSet<Guid>(onlineUserIds);

            return users.Select(u => MapToDto(u, onlineSet.Contains(u.Id) ? UserStatus.Online : UserStatus.Offline)).ToList();
        }

        private static UserDto MapToDto(User user, UserStatus status)
        {
            return new UserDto
            {
                Id = user.Id,
                Username = user.Username,
                DisplayName = user.DisplayName,
                Email = user.Email,
                ProfilePictureUrl = user.ProfilePictureUrl,
                Status = status,
                LastSeen = user.LastSeen
            };
        }
    }
}
