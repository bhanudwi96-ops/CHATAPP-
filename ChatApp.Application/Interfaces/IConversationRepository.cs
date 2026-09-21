using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChatApp.Domain.Entities;

namespace ChatApp.Application.Interfaces
{
    public interface IConversationRepository
    {
        Task<Conversation?> GetByIdAsync(Guid id);
        Task<List<Conversation>> GetUserConversationsAsync(Guid userId);
        Task<Conversation?> GetPrivateConversationAsync(Guid user1Id, Guid user2Id);
        Task<Conversation> CreateAsync(Conversation conversation);
        Task<Conversation> CreateWithParticipantsAsync(Conversation conversation, IEnumerable<Guid> participantIds, Guid creatorId);
        Task<Conversation> UpdateAsync(Conversation conversation);
        Task DeleteAsync(Guid id);
        Task<ConversationParticipant?> GetParticipantAsync(Guid conversationId, Guid userId);
        Task<ConversationParticipant> AddParticipantAsync(Guid conversationId, Guid userId, bool isAdmin = false);
        Task<bool> RemoveParticipantAsync(Guid conversationId, Guid userId);
        Task<bool> UpdateAdminStatusAsync(Guid conversationId, Guid userId, bool isAdmin);
        Task<bool> IsParticipantAsync(Guid conversationId, Guid userId);
        Task<bool> ExistsAsync(Guid conversationId);
    }
}
