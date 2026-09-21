using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChatApp.Application.DTOs;

namespace ChatApp.Application.Interfaces
{
    public interface ISupportTicketService
    {
        Task<SupportTicketDto> CreateTicketAsync(CreateSupportTicketDto dto, Guid? userId = null);
        Task<List<SupportTicketDto>> GetUserTicketsAsync(Guid userId);
    }
}
