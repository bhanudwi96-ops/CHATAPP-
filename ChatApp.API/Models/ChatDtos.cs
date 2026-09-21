using System;
using System.ComponentModel.DataAnnotations;

namespace ChatApp.API.Models
{
    public class ChatRequestDto
    {
        [Required]
        public Guid SessionId { get; set; }

        public int CustomerId { get; set; } = 1;

        public string? Username { get; set; }

        public string? DisplayName { get; set; }

        public string? Email { get; set; }

        [Required]
        [MinLength(1, ErrorMessage = "Message cannot be empty.")]
        public string Message { get; set; } = string.Empty;
    }

    public class ChatResponseDto
    {
        public string Reply { get; set; } = string.Empty;
        public bool TicketCreated { get; set; }
        public int? TicketId { get; set; }
    }

    public class CreateSessionRequestDto
    {
        public int? CustomerId { get; set; }
        public string? Username { get; set; }
        public string? DisplayName { get; set; }
        public string? Email { get; set; }
    }

    public class SessionResponseDto
    {
        public Guid SessionId { get; set; }
    }

    public class MessageHistoryDto
    {
        public Guid Id { get; set; }
        public Guid SessionId { get; set; }
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}
