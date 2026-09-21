using System;
using System.ComponentModel.DataAnnotations;
using ChatApp.Domain.Entities;

namespace ChatApp.Application.DTOs
{
    public class UserDto
    {
        public Guid Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? ProfilePictureUrl { get; set; }
        public UserStatus Status { get; set; }
        public DateTime? LastSeen { get; set; }
        public bool IsAdmin { get; set; }
    }

    public class RegisterDto
    {
        [Required(ErrorMessage = "Username is required")]
        [StringLength(50, MinimumLength = 3, ErrorMessage = "Username must be 3-50 characters")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Email is required")]
        [EmailAddress(ErrorMessage = "Invalid email format")]
        [StringLength(100)]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Password is required")]
        [StringLength(100, MinimumLength = 6, ErrorMessage = "Password must be at least 6 characters")]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "Display name is required")]
        [StringLength(100, MinimumLength = 1, ErrorMessage = "Display name must be 1-100 characters")]
        public string DisplayName { get; set; } = string.Empty;
    }

    public class LoginDto
    {
        [Required(ErrorMessage = "Username is required")]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "Password is required")]
        public string Password { get; set; } = string.Empty;
    }

    public class LoginResponseDto
    {
        public string Token { get; set; } = string.Empty;
        public UserDto User { get; set; } = new();
    }

    public class UpdateProfileDto
    {
        [StringLength(100, MinimumLength = 1, ErrorMessage = "Display name must be 1-100 characters")]
        public string? DisplayName { get; set; }

        [StringLength(500)]
        public string? ProfilePictureUrl { get; set; }
    }
}
