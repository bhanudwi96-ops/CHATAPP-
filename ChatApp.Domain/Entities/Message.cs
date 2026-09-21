using System;
using System.Collections.Generic;

namespace ChatApp.Domain.Entities
{
    /// <summary>
    /// Represents a message sent in a conversation
    /// </summary>
    public class Message
    {
        public Guid Id { get; set; }
        public Guid ConversationId { get; set; }
        public Guid SenderId { get; set; }
        public string Content { get; set; }
        public MessageType Type { get; set; }
        public string? AttachmentUrl { get; set; }
        public bool IsRead { get; set; }
        public DateTime SentAt { get; set; }
        public DateTime? ReadAt { get; set; }
        public bool IsEdited { get; set; }
        public DateTime? EditedAt { get; set; }
        public bool IsDeleted { get; set; }
        public Guid? ReplyToMessageId { get; set; }

        // Navigation properties
        public virtual Conversation Conversation { get; set; }
        public virtual User Sender { get; set; }
        public virtual Message? ReplyToMessage { get; set; }
        public virtual ICollection<MessageReaction> Reactions { get; set; } = new List<MessageReaction>();

        public Message()
        {
            Id = Guid.NewGuid();
            SentAt = DateTime.UtcNow;
            IsRead = false;
            IsEdited = false;
            IsDeleted = false;
            Type = MessageType.Text;
        }
    }
}
