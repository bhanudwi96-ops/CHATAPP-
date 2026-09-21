using System;
using System.Threading;
using System.Threading.Tasks;
using ChatApp.Domain.Entities;

namespace ChatApp.Application.Interfaces
{
    /// <summary>
    /// Persistence abstraction for OTP entities (Application layer, decoupled from EF Core)
    /// </summary>
    public interface IOtpRepository
    {
        Task<OtpVerification> CreateAsync(OtpVerification otpVerification, CancellationToken cancellationToken = default);

        Task<OtpVerification?> GetActiveOtpAsync(string recipient, OtpPurpose purpose, CancellationToken cancellationToken = default);

        Task<OtpVerification?> GetByResetTokenAsync(string recipient, OtpPurpose purpose, string resetToken, CancellationToken cancellationToken = default);

        Task UpdateAsync(OtpVerification otpVerification, CancellationToken cancellationToken = default);

        Task InvalidatePreviousOtpsAsync(string recipient, OtpPurpose purpose, CancellationToken cancellationToken = default);
    }
}
