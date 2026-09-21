using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using ChatApp.Domain.Entities;
using ChatApp.Application.Interfaces;
using ChatApp.Application.DTOs;
using ChatApp.Application.Exceptions;

namespace ChatApp.Infrastructure.Services
{
    /// <summary>
    /// Service for authentication, registration, and JWT token generation
    /// Lives in Infrastructure because it depends on BCrypt and JWT (infrastructure concerns)
    /// </summary>
    public class AuthService : IAuthService
    {
        private readonly IUserRepository _userRepository;
        private readonly IConfiguration _configuration;

        public AuthService(IUserRepository userRepository, IConfiguration configuration)
        {
            _userRepository = userRepository;
            _configuration = configuration;
        }

        public async Task<LoginResponseDto> RegisterAsync(RegisterDto registerDto)
        {
            if (await _userRepository.UsernameExistsAsync(registerDto.Username))
            {
                throw new ConflictException("Username already exists");
            }

            if (await _userRepository.EmailExistsAsync(registerDto.Email))
            {
                throw new ConflictException("Email already exists");
            }

            var user = new User
            {
                Username = registerDto.Username,
                Email = registerDto.Email,
                DisplayName = registerDto.DisplayName,
                PasswordHash = HashPassword(registerDto.Password),
                Status = UserStatus.Offline
            };

            user = await _userRepository.CreateAsync(user);

            var token = GenerateJwtToken(user.Id, user.Username);

            return new LoginResponseDto
            {
                Token = token,
                User = new UserDto
                {
                    Id = user.Id,
                    Username = user.Username,
                    Email = user.Email,
                    DisplayName = user.DisplayName,
                    ProfilePictureUrl = user.ProfilePictureUrl,
                    Status = user.Status
                }
            };
        }

        public async Task<LoginResponseDto> LoginAsync(LoginDto loginDto)
        {
            var user = await _userRepository.GetByUsernameAsync(loginDto.Username);

            if (user == null || !VerifyPassword(loginDto.Password, user.PasswordHash))
            {
                throw new BusinessRuleException("Invalid username or password");
            }

            var token = GenerateJwtToken(user.Id, user.Username);

            return new LoginResponseDto
            {
                Token = token,
                User = new UserDto
                {
                    Id = user.Id,
                    Username = user.Username,
                    Email = user.Email,
                    DisplayName = user.DisplayName,
                    ProfilePictureUrl = user.ProfilePictureUrl,
                    Status = user.Status
                }
            };
        }

        public string GenerateJwtToken(Guid userId, string username)
        {
            var jwtSettings = _configuration.GetSection("JwtSettings");
            var secretKey = jwtSettings["SecretKey"]
                ?? throw new InvalidOperationException("JwtSettings:SecretKey is required");
            var issuer = jwtSettings["Issuer"];
            var audience = jwtSettings["Audience"];
            var expiryMinutes = int.Parse(jwtSettings["ExpiryInMinutes"] ?? "1440");

            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, username),
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(expiryMinutes),
                signingCredentials: credentials
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        public string HashPassword(string password)
        {
            return BCrypt.Net.BCrypt.HashPassword(password);
        }

        public bool VerifyPassword(string password, string passwordHash)
        {
            return BCrypt.Net.BCrypt.Verify(password, passwordHash);
        }
    }
}
