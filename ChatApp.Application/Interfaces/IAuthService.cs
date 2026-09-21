using System;
using System.Threading.Tasks;
using ChatApp.Application.DTOs;

namespace ChatApp.Application.Interfaces
{
    public interface IAuthService
    {
        Task<LoginResponseDto> RegisterAsync(RegisterDto registerDto);
        Task<LoginResponseDto> LoginAsync(LoginDto loginDto);
        string GenerateJwtToken(Guid userId, string username);
        string HashPassword(string password);
        bool VerifyPassword(string password, string passwordHash);
    }
}
