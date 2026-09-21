using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ChatApp.Domain.Entities;
using ChatApp.Application.Interfaces;
using ChatApp.Infrastructure.Data;

namespace ChatApp.Infrastructure.Repositories
{
    /// <summary>
    /// Repository for Message entity operations
    /// Highly optimized with AsNoTracking for instant message retrieval
    /// </summary>
    public class MessageRepository : IMessageRepository
    {
        private readonly ChatDbContext _context;

        public MessageRepository(ChatDbContext context)
        {
            _context = context;
        }

        public async Task<Message?> GetByIdAsync(Guid id)
        {
            return await _context.Messages
                .AsNoTracking()
                .Include(m => m.Sender)
                .Include(m => m.Conversation)
                .Include(m => m.ReplyToMessage)
                    .ThenInclude(rm => rm.Sender)
                .Include(m => m.Reactions)
                    .ThenInclude(r => r.User)
                .FirstOrDefaultAsync(m => m.Id == id);
        }

        public async Task<List<Message>> GetConversationMessagesAsync(Guid conversationId, int skip = 0, int take = 50)
        {
            return await _context.Messages
                .AsNoTracking()
                .Where(m => m.ConversationId == conversationId)
                .Include(m => m.Sender)
                .Include(m => m.ReplyToMessage)
                    .ThenInclude(rm => rm.Sender)
                .Include(m => m.Reactions)
                    .ThenInclude(r => r.User)
                .OrderByDescending(m => m.SentAt)
                .Skip(skip)
                .Take(take)
                .ToListAsync();
        }

        public async Task<Message> CreateAsync(Message message)
        {
            _context.Messages.Add(message);
            await _context.SaveChangesAsync();
            return message;
        }

        public async Task<Message> UpdateAsync(Message message)
        {
            var existing = await _context.Messages.FindAsync(message.Id);
            if (existing != null)
            {
                existing.Content = message.Content;
                existing.IsRead = message.IsRead;
                existing.ReadAt = message.ReadAt;
                existing.IsEdited = message.IsEdited;
                existing.EditedAt = message.EditedAt;
                existing.IsDeleted = message.IsDeleted;
                await _context.SaveChangesAsync();
                return existing;
            }

            _context.Messages.Update(message);
            await _context.SaveChangesAsync();
            return message;
        }

        public async Task DeleteAsync(Guid id)
        {
            var message = await _context.Messages.FindAsync(id);
            if (message != null)
            {
                message.IsDeleted = true;
                message.Content = "This message was deleted";
                await _context.SaveChangesAsync();
            }
        }

        public async Task<int> GetUnreadCountAsync(Guid conversationId, Guid userId)
        {
            var participant = await _context.ConversationParticipants
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.ConversationId == conversationId && p.UserId == userId);

            if (participant == null || participant.LastReadAt == null)
            {
                return await _context.Messages
                    .AsNoTracking()
                    .Where(m => m.ConversationId == conversationId
                        && m.SenderId != userId
                        && !m.IsDeleted)
                    .CountAsync();
            }

            return await _context.Messages
                .AsNoTracking()
                .Where(m => m.ConversationId == conversationId
                    && m.SenderId != userId
                    && m.SentAt > participant.LastReadAt
                    && !m.IsDeleted)
                .CountAsync();
        }

        public async Task<Dictionary<Guid, int>> GetBatchUnreadCountsAsync(IEnumerable<Guid> conversationIds, Guid userId)
        {
            var idList = conversationIds.ToList();
            if (!idList.Any()) return new Dictionary<Guid, int>();

            // 1. Fetch all participant LastReadAt values for this user in one query
            var participantLookup = await _context.ConversationParticipants
                .AsNoTracking()
                .Where(p => idList.Contains(p.ConversationId) && p.UserId == userId)
                .ToDictionaryAsync(p => p.ConversationId, p => p.LastReadAt);

            // 2. Fetch candidate unread messages in ONE single batch query (projecting only ID and SentAt)
            var candidateMessages = await _context.Messages
                .AsNoTracking()
                .Where(m => idList.Contains(m.ConversationId)
                    && m.SenderId != userId
                    && !m.IsDeleted)
                .Select(m => new { m.ConversationId, m.SentAt })
                .ToListAsync();

            // 3. Tally counts in memory preserving all exact cutoff and null semantics
            var result = new Dictionary<Guid, int>(idList.Count);
            foreach (var cid in idList)
            {
                result[cid] = 0;
            }

            foreach (var m in candidateMessages)
            {
                if (participantLookup.TryGetValue(m.ConversationId, out var lastReadAt))
                {
                    if (lastReadAt == null || m.SentAt > lastReadAt)
                    {
                        result[m.ConversationId]++;
                    }
                }
                else
                {
                    result[m.ConversationId]++;
                }
            }

            return result;
        }

        public async Task MarkAsReadAsync(Guid messageId, Guid userId)
        {
            var message = await _context.Messages.FirstOrDefaultAsync(m => m.Id == messageId);
            if (message != null && message.SenderId != userId)
            {
                message.IsRead = true;
                message.ReadAt = DateTime.UtcNow;

                var participant = await _context.ConversationParticipants
                    .FirstOrDefaultAsync(p => p.ConversationId == message.ConversationId && p.UserId == userId);

                if (participant != null)
                {
                    participant.LastReadAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync();
            }
        }

        public async Task MarkConversationAsReadAsync(Guid conversationId, Guid userId)
        {
            var now = DateTime.UtcNow;

            // Update participant's LastReadAt
            var participant = await _context.ConversationParticipants
                .FirstOrDefaultAsync(p => p.ConversationId == conversationId && p.UserId == userId);

            if (participant != null)
            {
                participant.LastReadAt = now;
            }

            // Bulk update messages using raw SQL — avoids loading thousands of messages into memory
            await _context.Database.ExecuteSqlRawAsync(
                "UPDATE Messages SET IsRead = 1, ReadAt = {0} WHERE ConversationId = {1} AND SenderId != {2} AND IsRead = 0 AND IsDeleted = 0",
                now, conversationId, userId);

            await _context.SaveChangesAsync();
        }

        public async Task<List<MessageReaction>> ToggleReactionAsync(Guid messageId, Guid userId, string emoji)
        {
            var existingSameEmoji = await _context.MessageReactions
                .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == userId && r.Emoji == emoji);

            if (existingSameEmoji != null)
            {
                // Toggle off: user clicked the same emoji they previously reacted with
                _context.MessageReactions.Remove(existingSameEmoji);
            }
            else
            {
                // Switch/Replace: remove any other reaction this user had on this message
                var otherReactions = await _context.MessageReactions
                    .Where(r => r.MessageId == messageId && r.UserId == userId)
                    .ToListAsync();

                if (otherReactions.Any())
                {
                    _context.MessageReactions.RemoveRange(otherReactions);
                }

                // Add the new reaction
                var reaction = new MessageReaction
                {
                    MessageId = messageId,
                    UserId = userId,
                    Emoji = emoji,
                    CreatedAt = DateTime.UtcNow
                };
                _context.MessageReactions.Add(reaction);
            }

            await _context.SaveChangesAsync();

            return await GetMessageReactionsAsync(messageId);
        }

        public async Task<List<MessageReaction>> GetMessageReactionsAsync(Guid messageId)
        {
            return await _context.MessageReactions
                .AsNoTracking()
                .Include(r => r.User)
                .Where(r => r.MessageId == messageId)
                .ToListAsync();
        }
    }
}
