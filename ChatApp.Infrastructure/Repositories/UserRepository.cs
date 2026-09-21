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
    /// Repository for User entity operations
    /// </summary>
    public class UserRepository : IUserRepository
    {
        private readonly ChatDbContext _context;

        public UserRepository(ChatDbContext context)
        {
            _context = context;
        }

        public async Task<User?> GetByIdAsync(Guid id)
        {
            return await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == id);
        }

        public async Task<User?> GetByUsernameAsync(string username)
        {
            // Used by AuthService for login — needs PasswordHash
            return await _context.Users
                .FirstOrDefaultAsync(u => u.Username == username);
        }

        public async Task<User?> GetByEmailAsync(string email)
        {
            return await _context.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Email == email);
        }

        public async Task<List<User>> GetAllAsync()
        {
            return await _context.Users
                .AsNoTracking()
                .OrderBy(u => u.Username)
                .ToListAsync();
        }

        public async Task<List<User>> GetAllAsync(int skip, int take)
        {
            return await _context.Users
                .AsNoTracking()
                .OrderBy(u => u.Username)
                .Skip(skip)
                .Take(take)
                .ToListAsync();
        }

        public async Task<User> CreateAsync(User user)
        {
            _context.Users.Add(user);
            await _context.SaveChangesAsync();
            return user;
        }

        public async Task<User> UpdateAsync(User user)
        {
            user.UpdatedAt = DateTime.UtcNow;
            _context.Users.Update(user);
            await _context.SaveChangesAsync();
            return user;
        }

        public async Task DeleteAsync(Guid id)
        {
            var user = await GetByIdAsync(id);
            if (user != null)
            {
                _context.Users.Remove(user);
                await _context.SaveChangesAsync();
            }
        }

        public async Task<bool> ExistsAsync(Guid id)
        {
            return await _context.Users.AnyAsync(u => u.Id == id);
        }

        public async Task<bool> UsernameExistsAsync(string username)
        {
            return await _context.Users.AnyAsync(u => u.Username == username);
        }

        public async Task<bool> EmailExistsAsync(string email)
        {
            return await _context.Users.AnyAsync(u => u.Email == email);
        }

        public async Task<List<User>> SearchUsersAsync(string query, Guid excludeUserId, int limit = 20)
        {
            var q = (query ?? string.Empty).Trim().ToLower();

            var queryable = _context.Users
                .Where(u => u.Id != excludeUserId);

            if (!string.IsNullOrWhiteSpace(q))
            {
                queryable = queryable.Where(u =>
                    u.Username.ToLower().Contains(q) ||
                    u.DisplayName.ToLower().Contains(q) ||
                    u.Email.ToLower().Contains(q));
            }

            return await queryable
                .OrderBy(u => u.DisplayName)
                .Take(limit)
                .ToListAsync();
        }

        public async Task<User?> UpdateProfileAsync(Guid userId, string? displayName, string? profilePictureUrl)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return null;

            if (!string.IsNullOrWhiteSpace(displayName))
            {
                user.DisplayName = displayName.Trim();
            }

            if (profilePictureUrl != null)
            {
                user.ProfilePictureUrl = profilePictureUrl.Trim();
            }

            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return user;
        }
    }
}
