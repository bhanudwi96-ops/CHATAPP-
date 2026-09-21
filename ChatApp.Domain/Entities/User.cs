using System;
using System.Collections.Generic;

namespace ChatApp.Domain.Entities
{
    /// <summary>
    /// Represents a user in the chat application
    /// </summary>
    public class User
    {
        public Guid Id { get; set; }
        public string Username { get; set; }
        public string Email { get; set; }
        public string PasswordHash { get; set; }
        public string DisplayName { get; set; }
        public string? ProfilePictureUrl { get; set; }
        public UserStatus Status { get; set; }
        public DateTime? LastSeen { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        // Navigation properties
        public virtual ICollection<Message> SentMessages { get; set; }
        public virtual ICollection<ConversationParticipant> Conversations { get; set; }
        public virtual ICollection<UserConnection> Connections { get; set; }

        public User()
        {
            Id = Guid.NewGuid();
            CreatedAt = DateTime.UtcNow;
            Status = UserStatus.Offline;
            SentMessages = new List<Message>();
            Conversations = new List<ConversationParticipant>();
            Connections = new List<UserConnection>();
        }
    }
}
