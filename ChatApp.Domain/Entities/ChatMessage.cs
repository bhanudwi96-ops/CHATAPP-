using System;

namespace ChatApp.Domain.Entities
{
    public class ChatMessage
    {
        public Guid Id { get; set; }
        public Guid SessionId { get; set; }
        public string Role { get; set; } = null!; // 'user' or 'assistant'
        public string Content { get; set; } = null!;
        public DateTime CreatedAt { get; set; }

        public ChatSession? Session { get; set; }
    }
}
