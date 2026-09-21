using System.Collections.Generic;
using System.Threading.Tasks;
using ChatApp.Application.DTOs;
using ChatApp.Application.Enums;

namespace ChatApp.Application.Interfaces
{
    public interface IEmailService
    {
        Task QueueAsync(EmailMessage message);
        Task QueueTemplateAsync(
            string to, 
            string toName, 
            string subject, 
            EmailTemplateType templateType, 
            Dictionary<string, string> templateData,
            string? correlationId = null);
    }
}
