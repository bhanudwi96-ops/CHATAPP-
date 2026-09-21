using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ChatApp.Application.DTOs
{
    public class ConversationDto
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public bool IsGroupChat { get; set; }
        public Guid? CreatedById { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastMessageAt { get; set; }
        public List<UserDto> Participants { get; set; } = new();
        public MessageDto? LastMessage { get; set; }
        public int UnreadCount { get; set; }
    }

    public class CreateConversationDto
    {
        [Required(ErrorMessage = "At least one participant is required")]
        [MinLength(1, ErrorMessage = "At least one participant is required")]
        public List<Guid> ParticipantIds { get; set; } = new();

        [StringLength(100, ErrorMessage = "Group name cannot exceed 100 characters")]
        public string? Name { get; set; }

        public bool IsGroupChat { get; set; }
    }

    public class AddParticipantsDto
    {
        [Required(ErrorMessage = "At least one user ID is required")]
        [MinLength(1, ErrorMessage = "At least one user ID is required")]
        public List<Guid> UserIds { get; set; } = new();
    }

    public class UpdateAdminDto
    {
        public bool IsAdmin { get; set; }
    }
}
