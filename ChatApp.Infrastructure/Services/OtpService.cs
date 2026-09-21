using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ChatApp.Application.DTOs;
using ChatApp.Application.Enums;
using ChatApp.Application.Interfaces;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatApp.Infrastructure.Services
{
    /// <summary>
    /// Implements generic, secure OTP generation, SHA-256 storage, brute-force protection,
    /// and outbox email delivery via IEmailService.
    /// </summary>
    public class OtpService : IOtpService
    {
        private readonly IOtpRepository _otpRepository;
        private readonly IEmailService _emailService;
        private readonly OtpSettings _settings;
        private readonly ILogger<OtpService> _logger;

        public OtpService(
            IOtpRepository otpRepository,
            IEmailService emailService,
            IOptions<OtpSettings> settings,
            ILogger<OtpService> logger)
        {
            _otpRepository = otpRepository;
            _emailService = emailService;
            _settings = settings.Value;
            _logger = logger;
        }

        public async Task<OtpResultDto> GenerateAndSendOtpAsync(
            OtpPurpose purpose,
            string recipient,
            string? displayName = null,
            Guid? userId = null,
            CancellationToken cancellationToken = default)
        {
            var normalizedRecipient = recipient.Trim().ToLowerInvariant();

            // 1. Invalidate any existing unused OTPs for this purpose and recipient
            await _otpRepository.InvalidatePreviousOtpsAsync(normalizedRecipient, purpose, cancellationToken);

            // 2. Generate a cryptographically secure 6-digit numeric OTP
            var rawOtp = GenerateCryptographicNumericOtp(_settings.Length);
            var hashedOtp = ComputeSha256Hash(rawOtp);
            var expiresAt = DateTime.UtcNow.AddMinutes(_settings.ExpirationMinutes);

            // 3. Persist hashed OTP entity
            var otpEntity = new OtpVerification
            {
                UserId = userId,
                Recipient = normalizedRecipient,
                Purpose = purpose,
                OtpHash = hashedOtp,
                ExpiresAt = expiresAt,
                MaxAttempts = _settings.MaxAttempts,
                Attempts = 0,
                IsUsed = false,
                CreatedAt = DateTime.UtcNow
            };

            await _otpRepository.CreateAsync(otpEntity, cancellationToken);

            // 4. Map template and subject according to purpose
            var (templateType, subject) = MapPurposeToEmailDetails(purpose);

            // 5. Dispatch email via decoupled IEmailService (durable SQL Outbox)
            var templateData = new Dictionary<string, string>
            {
                { "UserName", string.IsNullOrWhiteSpace(displayName) ? "User" : displayName },
                { "OtpCode", rawOtp },
                { "ExpiryMinutes", _settings.ExpirationMinutes.ToString() }
            };

            await _emailService.QueueTemplateAsync(
                normalizedRecipient,
                displayName ?? "ChatApp User",
                subject,
                templateType,
                templateData,
                $"OTP-{purpose}-{otpEntity.Id}");

            _logger.LogInformation("Generic OTP generated and queued for {Recipient} (Purpose: {Purpose})", normalizedRecipient, purpose);

            return new OtpResultDto
            {
                Success = true,
                Message = $"A 6-digit verification code has been dispatched to {normalizedRecipient}.",
                ExpiresAt = expiresAt
            };
        }

        public async Task<OtpVerificationResultDto> VerifyOtpAsync(
            OtpPurpose purpose,
            string recipient,
            string otp,
            CancellationToken cancellationToken = default)
        {
            var normalizedRecipient = recipient.Trim().ToLowerInvariant();
            var activeOtp = await _otpRepository.GetActiveOtpAsync(normalizedRecipient, purpose, cancellationToken);

            if (activeOtp == null)
            {
                return new OtpVerificationResultDto
                {
                    IsValid = false,
                    Message = "No active verification code found or it has expired. Please request a new code."
                };
            }

            // Increment attempt count
            activeOtp.Attempts++;

            var submittedOtpHash = ComputeSha256Hash(otp.Trim());
            var isMatch = CryptographicEquals(submittedOtpHash, activeOtp.OtpHash);

            if (isMatch)
            {
                activeOtp.VerifiedAt = DateTime.UtcNow;

                // Issue single-use 64-char hex reset authorization token
                var resetToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                activeOtp.ResetToken = resetToken;
                activeOtp.ResetTokenExpiresAt = DateTime.UtcNow.AddMinutes(_settings.ResetTokenExpirationMinutes);

                await _otpRepository.UpdateAsync(activeOtp, cancellationToken);

                _logger.LogInformation("OTP successfully verified for {Recipient} (Purpose: {Purpose})", normalizedRecipient, purpose);

                return new OtpVerificationResultDto
                {
                    IsValid = true,
                    ResetToken = resetToken,
                    Message = "OTP verified successfully."
                };
            }

            // Mismatch handling
            if (activeOtp.Attempts >= activeOtp.MaxAttempts)
            {
                activeOtp.IsUsed = true; // Invalidate due to brute-force
                await _otpRepository.UpdateAsync(activeOtp, cancellationToken);

                _logger.LogWarning("OTP brute-force limit reached for {Recipient} (Purpose: {Purpose})", normalizedRecipient, purpose);

                return new OtpVerificationResultDto
                {
                    IsValid = false,
                    RemainingAttempts = 0,
                    Message = "Maximum verification attempts exceeded. For your security, this code has been invalidated. Please request a new one."
                };
            }

            await _otpRepository.UpdateAsync(activeOtp, cancellationToken);
            var remaining = activeOtp.MaxAttempts - activeOtp.Attempts;

            return new OtpVerificationResultDto
            {
                IsValid = false,
                RemainingAttempts = remaining,
                Message = $"Incorrect code. {remaining} attempt(s) remaining."
            };
        }

        public async Task<bool> ValidateResetTokenAsync(
            OtpPurpose purpose,
            string recipient,
            string resetToken,
            CancellationToken cancellationToken = default)
        {
            var normalizedRecipient = recipient.Trim().ToLowerInvariant();
            var record = await _otpRepository.GetByResetTokenAsync(normalizedRecipient, purpose, resetToken, cancellationToken);
            return record != null;
        }

        public async Task ConsumeResetTokenAsync(
            OtpPurpose purpose,
            string recipient,
            string resetToken,
            CancellationToken cancellationToken = default)
        {
            var normalizedRecipient = recipient.Trim().ToLowerInvariant();
            var record = await _otpRepository.GetByResetTokenAsync(normalizedRecipient, purpose, resetToken, cancellationToken);
            if (record != null)
            {
                record.IsUsed = true;
                record.ResetToken = null;
                await _otpRepository.UpdateAsync(record, cancellationToken);
            }
        }

        private static string GenerateCryptographicNumericOtp(int length)
        {
            int min = (int)Math.Pow(10, length - 1);
            int max = (int)Math.Pow(10, length);
            return RandomNumberGenerator.GetInt32(min, max).ToString();
        }

        private static string ComputeSha256Hash(string input)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes);
        }

        private static bool CryptographicEquals(string a, string b)
        {
            var bytesA = Encoding.UTF8.GetBytes(a);
            var bytesB = Encoding.UTF8.GetBytes(b);
            return CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
        }

        private static (EmailTemplateType TemplateType, string Subject) MapPurposeToEmailDetails(OtpPurpose purpose)
        {
            return purpose switch
            {
                OtpPurpose.PasswordReset => (EmailTemplateType.PasswordResetOtp, "ChatApp Password Reset OTP"),
                OtpPurpose.ForgotUsername => (EmailTemplateType.OtpVerification, "ChatApp Username Recovery Code"),
                OtpPurpose.EmailVerification => (EmailTemplateType.OtpVerification, "ChatApp Email Verification Code"),
                OtpPurpose.TwoFactorAuthentication => (EmailTemplateType.OtpVerification, "ChatApp Two-Factor Authentication Code"),
                _ => (EmailTemplateType.OtpVerification, "Your ChatApp Verification Code")
            };
        }
    }
}
