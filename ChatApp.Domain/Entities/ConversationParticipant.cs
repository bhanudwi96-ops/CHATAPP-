using System;

namespace ChatApp.Domain.Entities
{
    /// <summary>
    /// Junction table linking Users to Conversations (many-to-many)
    /// </summary>
    public class ConversationParticipant
    {
        public Guid Id { get; set; }
        public Guid ConversationId { get; set; }
        public Guid UserId { get; set; }
        public DateTime JoinedAt { get; set; }
        public DateTime? LeftAt { get; set; }
        public bool IsAdmin { get; set; } // For group chats
        public DateTime? LastReadAt { get; set; }

        // Navigation properties
        public virtual Conversation Conversation { get; set; }
        public virtual User User { get; set; }

        public ConversationParticipant()
        {
            Id = Guid.NewGuid();
            JoinedAt = DateTime.UtcNow;
            IsAdmin = false;
        }
    }
}
