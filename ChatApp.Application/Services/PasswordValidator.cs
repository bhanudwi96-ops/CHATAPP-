using ChatApp.Application.Interfaces;

namespace ChatApp.Application.Services
{
    /// <summary>
    /// Default password validator enforcing existing application password standards (minimum 6 characters)
    /// </summary>
    public class PasswordValidator : IPasswordValidator
    {
        public const int MinimumLength = 6;
        public const int MaximumLength = 100;

        public (bool IsValid, string? ErrorMessage) Validate(string password)
        {
            if (string.IsNullOrWhiteSpace(password))
            {
                return (false, "Password is required");
            }

            if (password.Length < MinimumLength)
            {
                return (false, $"Password must be at least {MinimumLength} characters");
            }

            if (password.Length > MaximumLength)
            {
                return (false, $"Password cannot exceed {MaximumLength} characters");
            }

            return (true, null);
        }
    }
}
