using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChatApp.Application.Interfaces;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Infrastructure.Repositories
{
    public class OtpRepository : IOtpRepository
    {
        private readonly ChatDbContext _context;

        public OtpRepository(ChatDbContext context)
        {
            _context = context;
        }

        public async Task<OtpVerification> CreateAsync(OtpVerification otpVerification, CancellationToken cancellationToken = default)
        {
            await _context.OtpVerifications.AddAsync(otpVerification, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
            return otpVerification;
        }

        public async Task<OtpVerification?> GetActiveOtpAsync(string recipient, OtpPurpose purpose, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            return await _context.OtpVerifications
                .Where(o => o.Recipient == recipient 
                         && o.Purpose == purpose 
                         && !o.IsUsed 
                         && o.ExpiresAt > now 
                         && o.Attempts < o.MaxAttempts)
                .OrderByDescending(o => o.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task<OtpVerification?> GetByResetTokenAsync(string recipient, OtpPurpose purpose, string resetToken, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            return await _context.OtpVerifications
                .Where(o => o.Recipient == recipient 
                         && o.Purpose == purpose 
                         && !o.IsUsed 
                         && o.ResetToken == resetToken 
                         && o.ResetTokenExpiresAt != null 
                         && o.ResetTokenExpiresAt > now)
                .FirstOrDefaultAsync(cancellationToken);
        }

        public async Task UpdateAsync(OtpVerification otpVerification, CancellationToken cancellationToken = default)
        {
            _context.OtpVerifications.Update(otpVerification);
            await _context.SaveChangesAsync(cancellationToken);
        }

        public async Task InvalidatePreviousOtpsAsync(string recipient, OtpPurpose purpose, CancellationToken cancellationToken = default)
        {
            var activeOtps = await _context.OtpVerifications
                .Where(o => o.Recipient == recipient && o.Purpose == purpose && !o.IsUsed)
                .ToListAsync(cancellationToken);

            if (activeOtps.Any())
            {
                foreach (var otp in activeOtps)
                {
                    otp.IsUsed = true;
                }
                await _context.SaveChangesAsync(cancellationToken);
            }
        }
    }
}
