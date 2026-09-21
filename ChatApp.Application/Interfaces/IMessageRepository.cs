using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChatApp.Domain.Entities;

namespace ChatApp.Application.Interfaces
{
    public interface IMessageRepository
    {
        Task<Message?> GetByIdAsync(Guid id);
        Task<List<Message>> GetConversationMessagesAsync(Guid conversationId, int skip = 0, int take = 50);
        Task<Message> CreateAsync(Message message);
        Task<Message> UpdateAsync(Message message);
        Task DeleteAsync(Guid id);
        Task<int> GetUnreadCountAsync(Guid conversationId, Guid userId);
        Task<Dictionary<Guid, int>> GetBatchUnreadCountsAsync(IEnumerable<Guid> conversationIds, Guid userId);
        Task MarkAsReadAsync(Guid messageId, Guid userId);
        Task MarkConversationAsReadAsync(Guid conversationId, Guid userId);
        Task<List<MessageReaction>> ToggleReactionAsync(Guid messageId, Guid userId, string emoji);
        Task<List<MessageReaction>> GetMessageReactionsAsync(Guid messageId);
    }
}
