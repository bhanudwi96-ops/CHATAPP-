using System.Threading;
using System.Threading.Tasks;
using ChatApp.Application.DTOs;

namespace ChatApp.Application.Interfaces
{
    /// <summary>
    /// Application service orchestrating the complete Password Recovery flow:
    /// 1. Request OTP
    /// 2. Verify OTP -> issue short-lived single-use ResetToken
    /// 3. Reset Password (validates ResetToken + verifies Old Password + validates New Password + updates DB)
    /// </summary>
    public interface IPasswordRecoveryService
    {
        Task<ForgotPasswordResponseDto> RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default);

        Task<VerifyOtpResponseDto> VerifyPasswordResetOtpAsync(string email, string otp, CancellationToken cancellationToken = default);

        Task<bool> ResetPasswordAsync(ResetPasswordDto resetPasswordDto, CancellationToken cancellationToken = default);
    }
}
