using System;
using System.ComponentModel.DataAnnotations;
using ChatApp.Domain.Entities;

namespace ChatApp.Application.DTOs
{
    public class ForgotPasswordRequestDto
    {
        [Required(ErrorMessage = "Email is required"), EmailAddress(ErrorMessage = "Invalid email format")]
        public string Email { get; set; } = string.Empty;
    }

    public class ForgotPasswordResponseDto
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class VerifyOtpRequestDto
    {
        [Required(ErrorMessage = "Email is required"), EmailAddress(ErrorMessage = "Invalid email format")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "OTP is required"), RegularExpression(@"^\d{6}$", ErrorMessage = "OTP must be a 6-digit number")]
        public string Otp { get; set; } = string.Empty;
    }

    public class VerifyOtpResponseDto
    {
        public bool Verified { get; set; }
        public string? ResetToken { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class ResetPasswordDto
    {
        [Required(ErrorMessage = "Email is required"), EmailAddress(ErrorMessage = "Invalid email format")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "Reset token is required")]
        public string ResetToken { get; set; } = string.Empty;

        [Required(ErrorMessage = "Current password is required")]
        public string OldPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "New password is required")]
        public string NewPassword { get; set; } = string.Empty;
    }

    public class OtpResultDto
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
    }

    public class OtpVerificationResultDto
    {
        public bool IsValid { get; set; }
        public string? ResetToken { get; set; }
        public string Message { get; set; } = string.Empty;
        public int RemainingAttempts { get; set; }
    }
}
