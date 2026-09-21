using System;
using System.Threading;
using System.Threading.Tasks;
using ChatApp.Application.DTOs;
using ChatApp.Application.Exceptions;
using ChatApp.Application.Interfaces;
using ChatApp.Domain.Entities;

namespace ChatApp.Application.Services
{
    /// <summary>
    /// Implements the 3-step Password Recovery business logic, maintaining strict Clean Architecture
    /// (depends on domain abstractions and IAuthService / IOtpService without EF Core coupling).
    /// </summary>
    public class PasswordRecoveryService : IPasswordRecoveryService
    {
        private readonly IUserRepository _userRepository;
        private readonly IOtpService _otpService;
        private readonly IAuthService _authService;
        private readonly IPasswordValidator _passwordValidator;

        public PasswordRecoveryService(
            IUserRepository userRepository,
            IOtpService otpService,
            IAuthService authService,
            IPasswordValidator passwordValidator)
        {
            _userRepository = userRepository;
            _otpService = otpService;
            _authService = authService;
            _passwordValidator = passwordValidator;
        }

        public async Task<ForgotPasswordResponseDto> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default)
        {
            var normalizedEmail = email?.Trim().ToLowerInvariant() ?? string.Empty;
            var genericResponse = new ForgotPasswordResponseDto
            {
                Success = true,
                Message = "If an account exists for this email, a 6-digit OTP has been sent."
            };

            if (string.IsNullOrWhiteSpace(normalizedEmail))
            {
                return genericResponse;
            }

            var user = await _userRepository.GetByEmailAsync(normalizedEmail);
            if (user == null)
            {
                // Anti-enumeration protection: return identical response
                return genericResponse;
            }

            await _otpService.GenerateAndSendOtpAsync(
                OtpPurpose.PasswordReset,
                user.Email,
                user.DisplayName,
                user.Id,
                cancellationToken);

            return genericResponse;
        }

        public async Task<VerifyOtpResponseDto> VerifyPasswordResetOtpAsync(string email, string otp, CancellationToken cancellationToken = default)
        {
            var normalizedEmail = email?.Trim().ToLowerInvariant() ?? string.Empty;
            var result = await _otpService.VerifyOtpAsync(OtpPurpose.PasswordReset, normalizedEmail, otp, cancellationToken);

            if (!result.IsValid)
            {
                throw new BusinessRuleException(result.Message);
            }

            return new VerifyOtpResponseDto
            {
                Verified = true,
                ResetToken = result.ResetToken,
                Message = "OTP verified successfully. Please enter your old password and new password to complete the reset."
            };
        }

        public async Task<bool> ResetPasswordAsync(ResetPasswordDto resetPasswordDto, CancellationToken cancellationToken = default)
        {
            var normalizedEmail = resetPasswordDto.Email?.Trim().ToLowerInvariant() ?? string.Empty;

            // 1. Validate Reset Token (issued during Step 2 OTP verification)
            var isTokenValid = await _otpService.ValidateResetTokenAsync(
                OtpPurpose.PasswordReset,
                normalizedEmail,
                resetPasswordDto.ResetToken,
                cancellationToken);

            if (!isTokenValid)
            {
                throw new BusinessRuleException("The password reset session is invalid or has expired. Please request a new OTP.");
            }

            // 2. Fetch User
            var user = await _userRepository.GetByEmailAsync(normalizedEmail);
            if (user == null)
            {
                throw new NotFoundException("User account not found");
            }

            // 3. Mandatory Old Password Verification
            if (string.IsNullOrWhiteSpace(resetPasswordDto.OldPassword) ||
                !_authService.VerifyPassword(resetPasswordDto.OldPassword, user.PasswordHash))
            {
                throw new BusinessRuleException("The current (old) password you entered is incorrect.");
            }

            // 4. Ensure New Password differs from Old Password
            if (resetPasswordDto.NewPassword == resetPasswordDto.OldPassword)
            {
                throw new BusinessRuleException("New password cannot be identical to the old password.");
            }

            // 5. Validate New Password against application rules
            var (isValid, errorMessage) = _passwordValidator.Validate(resetPasswordDto.NewPassword);
            if (!isValid)
            {
                throw new BusinessRuleException(errorMessage ?? "New password does not meet security requirements.");
            }

            // 6. Hash New Password using existing BCrypt engine
            var newPasswordHash = _authService.HashPassword(resetPasswordDto.NewPassword);
            user.PasswordHash = newPasswordHash;
            user.UpdatedAt = DateTime.UtcNow;

            await _userRepository.UpdateAsync(user);

            // 7. Invalidate the single-use Reset Token and mark OTP as used
            await _otpService.ConsumeResetTokenAsync(
                OtpPurpose.PasswordReset,
                normalizedEmail,
                resetPasswordDto.ResetToken,
                cancellationToken);

            return true;
        }
    }
}
