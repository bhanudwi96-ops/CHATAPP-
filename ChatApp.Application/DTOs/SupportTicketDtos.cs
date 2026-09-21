using System;
using System.ComponentModel.DataAnnotations;
using ChatApp.Domain.Entities;

namespace ChatApp.Application.DTOs
{
    public class CreateSupportTicketDto
    {
        [Required, EmailAddress]
        public string UserEmail { get; set; } = string.Empty;

        public string? UserName { get; set; }

        [Required]
        public TicketCategory Category { get; set; }

        [Required, MinLength(3), MaxLength(150)]
        public string Subject { get; set; } = string.Empty;

        [Required, MinLength(10), MaxLength(3000)]
        public string Message { get; set; } = string.Empty;
    }

    public class SupportTicketDto
    {
        public Guid Id { get; set; }
        public string ReferenceNumber { get; set; } = string.Empty;
        public string UserEmail { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }
}
