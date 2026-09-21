namespace ChatApp.Infrastructure.Options
{
    public class OtpSettings
    {
        public int Length { get; set; } = 6;
        public int ExpirationMinutes { get; set; } = 5;
        public int MaxAttempts { get; set; } = 5;
        public int ResetTokenExpirationMinutes { get; set; } = 15;
    }
}
