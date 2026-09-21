using System;

namespace ChatApp.Domain.Entities
{
    /// <summary>
    /// Represents an OTP verification entity supporting generic purposes (PasswordReset, ForgotUsername, etc.)
    /// Raw OTPs are NEVER stored; only cryptographic hashes are persisted.
    /// </summary>
    public class OtpVerification
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        public Guid? UserId { get; set; }

        public string Recipient { get; set; } = string.Empty;

        public OtpPurpose Purpose { get; set; }

        public string OtpHash { get; set; } = string.Empty;

        /// <summary>
        /// Short-lived authorization token issued upon successful OTP verification.
        /// Required by Step 3 (Reset Password) to prevent unauthorized state transitions.
        /// </summary>
        public string? ResetToken { get; set; }

        public DateTime? ResetTokenExpiresAt { get; set; }

        public DateTime ExpiresAt { get; set; }

        public int Attempts { get; set; } = 0;

        public int MaxAttempts { get; set; } = 5;

        public bool IsUsed { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? VerifiedAt { get; set; }
    }
}
