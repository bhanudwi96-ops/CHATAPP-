using System;
using ChatApp.Domain.Entities;

namespace ChatApp.Application.DTOs
{
    public class TypingIndicatorDto
    {
        public Guid ConversationId { get; set; }
        public Guid UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public bool IsTyping { get; set; }
    }

    public class OnlineStatusDto
    {
        public Guid UserId { get; set; }
        public UserStatus Status { get; set; }
        public DateTime? LastSeen { get; set; }
    }
}
