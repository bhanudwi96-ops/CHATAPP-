using System;

namespace ChatApp.Domain.Entities
{
    public class Ticket
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        public string Issue { get; set; } = null!;
        public string Category { get; set; } = null!; // billing, technical, account, other
        public string Priority { get; set; } = null!; // low, medium, high
        public string Status { get; set; } = "Open";
        public DateTime CreatedAt { get; set; }
    }
}
