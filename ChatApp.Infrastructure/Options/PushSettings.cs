namespace ChatApp.Infrastructure.Options
{
    public class PushSettings
    {
        /// <summary>Path to the Firebase service-account JSON file.</summary>
        public string ServiceAccountPath { get; set; } = string.Empty;

        /// <summary>Optional FCM project ID override.</summary>
        public string? ProjectId { get; set; }

        /// <summary>Legacy server key (kept for config compatibility).</summary>
        public string ServerKey { get; set; } = string.Empty;
    }
}
