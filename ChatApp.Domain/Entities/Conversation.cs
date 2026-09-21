using System;
using System.Collections.Generic;

namespace ChatApp.Domain.Entities
{
    /// <summary>
    /// Represents a conversation between users (can be 1-on-1 or group chat)
    /// </summary>
    public class Conversation
    {
        public Guid Id { get; set; }
        public string? Name { get; set; } // Null for 1-on-1 chats
        public bool IsGroupChat { get; set; }
        public Guid? CreatedById { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastMessageAt { get; set; }

        // Navigation properties
        public virtual ICollection<Message> Messages { get; set; }
        public virtual ICollection<ConversationParticipant> Participants { get; set; }
        public virtual User? CreatedBy { get; set; }

        public Conversation()
        {
            Id = Guid.NewGuid();
            CreatedAt = DateTime.UtcNow;
            IsGroupChat = false;
            Messages = new List<Message>();
            Participants = new List<ConversationParticipant>();
        }
    }
}
