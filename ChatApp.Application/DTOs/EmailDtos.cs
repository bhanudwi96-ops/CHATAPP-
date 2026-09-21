using System.Collections.Generic;
using ChatApp.Application.Enums;

namespace ChatApp.Application.DTOs
{
    public class EmailMessage
    {
        public string To { get; set; } = string.Empty;
        public string? ToName { get; set; }
        public string Subject { get; set; } = string.Empty;
        public EmailTemplateType? TemplateType { get; set; }
        public Dictionary<string, string> TemplateData { get; set; } = new();
        public string? CorrelationId { get; set; }
    }
}
