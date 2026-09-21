using System;

namespace ChatApp.Domain.Entities
{
    /// <summary>
    /// Represents an emoji reaction added to a message by a user
    /// </summary>
    public class MessageReaction
    {
        public Guid Id { get; set; }
        public Guid MessageId { get; set; }
        public Guid UserId { get; set; }
        public string Emoji { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }

        // Navigation properties
        public virtual Message Message { get; set; } = null!;
        public virtual User User { get; set; } = null!;

        public MessageReaction()
        {
            Id = Guid.NewGuid();
            CreatedAt = DateTime.UtcNow;
        }
    }
}
