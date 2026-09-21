using System;
using System.Threading;
using System.Threading.Tasks;
using ChatApp.Application.DTOs;
using ChatApp.Domain.Entities;

namespace ChatApp.Application.Interfaces
{
    /// <summary>
    /// Generic, purpose-aware OTP lifecycle engine.
    /// Decoupled from specific business features (PasswordReset, ForgotUsername, EmailVerification, 2FA).
    /// </summary>
    public interface IOtpService
    {
        Task<OtpResultDto> GenerateAndSendOtpAsync(
            OtpPurpose purpose, 
            string recipient, 
            string? displayName = null, 
            Guid? userId = null, 
            CancellationToken cancellationToken = default);

        Task<OtpVerificationResultDto> VerifyOtpAsync(
            OtpPurpose purpose, 
            string recipient, 
            string otp, 
            CancellationToken cancellationToken = default);

        Task<bool> ValidateResetTokenAsync(
            OtpPurpose purpose, 
            string recipient, 
            string resetToken, 
            CancellationToken cancellationToken = default);

        Task ConsumeResetTokenAsync(
            OtpPurpose purpose, 
            string recipient, 
            string resetToken, 
            CancellationToken cancellationToken = default);
    }
}
