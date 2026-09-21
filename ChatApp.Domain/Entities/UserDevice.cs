using System;

namespace ChatApp.Domain.Entities
{
    public class UserDevice
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid UserId { get; set; }
        public string DeviceToken { get; set; } = string.Empty;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        // Navigation
        public User? User { get; set; }
    }
}
