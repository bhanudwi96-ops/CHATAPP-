namespace ChatApp.Infrastructure.Options
{
    public class EmailSettings
    {
        public string Provider { get; set; } = "DevConsole"; // "DevConsole" or "Smtp"
        public string SupportInboxEmail { get; set; } = "support@chatapp.local";
        public string FromEmail { get; set; } = "no-reply@chatapp.local";
        public string FromName { get; set; } = "ChatApp Support";

        // SMTP Settings
        public string SmtpHost { get; set; } = "smtp.gmail.com";
        public int SmtpPort { get; set; } = 587;
        public bool UseSsl { get; set; } = true;
        public string SmtpUser { get; set; } = string.Empty;
        public string SmtpPass { get; set; } = string.Empty;
    }
}
