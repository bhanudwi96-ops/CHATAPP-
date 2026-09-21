using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChatApp.Application.DTOs;

namespace ChatApp.Application.Interfaces
{
    public interface IConversationService
    {
        Task<ConversationDto> CreateConversationAsync(Guid creatorId, CreateConversationDto dto);
        Task<List<ConversationDto>> GetUserConversationsAsync(Guid userId);
        Task<ConversationDto> GetConversationByIdAsync(Guid conversationId, Guid userId);
        Task<ConversationDto> GetOrCreatePrivateConversationAsync(Guid user1Id, Guid user2Id);
        Task<bool> DeleteConversationAsync(Guid conversationId, Guid userId);
        Task<ConversationDto> AddParticipantsAsync(Guid conversationId, Guid currentUserId, List<Guid> newParticipantIds);
        Task<bool> RemoveParticipantAsync(Guid conversationId, Guid currentUserId, Guid targetUserId);
        Task<bool> LeaveConversationAsync(Guid conversationId, Guid currentUserId);
        Task<bool> UpdateAdminStatusAsync(Guid conversationId, Guid currentUserId, Guid targetUserId, bool isAdmin);
    }
}
