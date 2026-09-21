using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChatApp.Application.DTOs;

namespace ChatApp.Application.Interfaces
{
    public interface IMessageService
    {
        Task<MessageDto> SendMessageAsync(Guid senderId, SendMessageDto dto);
        Task<List<MessageDto>> GetConversationMessagesAsync(Guid conversationId, Guid userId, int skip = 0, int take = 50);
        Task<MessageDto> GetMessageByIdAsync(Guid messageId);
        Task<MessageDto> EditMessageAsync(Guid messageId, Guid userId, string newContent);
        Task<bool> MarkAsReadAsync(Guid messageId, Guid userId);
        Task<bool> MarkConversationAsReadAsync(Guid conversationId, Guid userId);
        Task<bool> DeleteMessageAsync(Guid messageId, Guid userId);
        Task<ReactionUpdateDto> ToggleReactionAsync(Guid messageId, Guid userId, string emoji);
    }
}
