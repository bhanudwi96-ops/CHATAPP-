using System;

namespace ChatApp.Domain.Entities
{
    /// <summary>
    /// Tracks active SignalR connections for each user
    /// (Users can have multiple connections - different devices/tabs)
    /// </summary>
    public class UserConnection
    {
        public Guid Id { get; set; }
        public Guid UserId { get; set; }
        public string ConnectionId { get; set; } // SignalR connection ID
        public string? UserAgent { get; set; } // Browser/device info
        public DateTime ConnectedAt { get; set; }
        public DateTime? DisconnectedAt { get; set; }
        public bool IsActive { get; set; }

        // Navigation property
        public virtual User User { get; set; }

        public UserConnection()
        {
            Id = Guid.NewGuid();
            ConnectedAt = DateTime.UtcNow;
            IsActive = true;
        }
    }
}
