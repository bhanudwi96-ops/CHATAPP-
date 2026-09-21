namespace ChatApp.Domain.Entities
{
    public enum OtpPurpose
    {
        PasswordReset = 0,
        ForgotUsername = 1,
        EmailVerification = 2,
        TwoFactorAuthentication = 3
    }
}
