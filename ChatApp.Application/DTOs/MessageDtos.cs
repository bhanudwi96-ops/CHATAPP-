using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using ChatApp.Domain.Entities;

namespace ChatApp.Application.DTOs
{
    public class ReactionDto
    {
        public string Emoji { get; set; } = string.Empty;
        public int Count { get; set; }
        public List<Guid> UserIds { get; set; } = new();
        public List<string> Usernames { get; set; } = new();
        public bool HasReacted { get; set; }
    }

    public class MessageDto
    {
        public Guid Id { get; set; }
        public Guid ConversationId { get; set; }
        public Guid SenderId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public MessageType Type { get; set; } = MessageType.Text;
        public string? AttachmentUrl { get; set; }
        public string? FileName { get; set; }
        public long? FileSize { get; set; }
        public bool IsRead { get; set; }
        public DateTime SentAt { get; set; }
        public bool IsEdited { get; set; }
        public DateTime? EditedAt { get; set; }
        public bool IsDeleted { get; set; }

        // Quoted Reply Context
        public Guid? ReplyToMessageId { get; set; }
        public string? ReplyToSenderName { get; set; }
        public string? ReplyToContent { get; set; }

        public List<ReactionDto> Reactions { get; set; } = new();
    }

    public class SendMessageDto
    {
        [Required]
        public Guid ConversationId { get; set; }

        [Required(ErrorMessage = "Message content is required")]
        [StringLength(5000, ErrorMessage = "Message cannot exceed 5000 characters")]
        public string Content { get; set; } = string.Empty;

        public MessageType Type { get; set; } = MessageType.Text;

        [StringLength(500)]
        public string? AttachmentUrl { get; set; }

        [StringLength(255)]
        public string? FileName { get; set; }

        public long? FileSize { get; set; }
        public Guid? ReplyToMessageId { get; set; }
    }

    public class EditMessageDto
    {
        public Guid MessageId { get; set; }

        [Required(ErrorMessage = "Message content is required")]
        [StringLength(5000, ErrorMessage = "Message cannot exceed 5000 characters")]
        public string Content { get; set; } = string.Empty;
    }

    public class DeleteMessageDto
    {
        public Guid MessageId { get; set; }
        public Guid ConversationId { get; set; }
    }

    public class ReactionUpdateDto
    {
        public Guid MessageId { get; set; }
        public Guid ConversationId { get; set; }
        public List<ReactionDto> Reactions { get; set; } = new();
    }
}
