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
    /// Repository for Conversation entity operations
    /// High-performance read queries using AsNoTracking and optimized joins
    /// </summary>
    public class ConversationRepository : IConversationRepository
    {
        private readonly ChatDbContext _context;

        public ConversationRepository(ChatDbContext context)
        {
            _context = context;
        }

        public async Task<Conversation?> GetByIdAsync(Guid id)
        {
            return await _context.Conversations
                .AsNoTracking()
                .Include(c => c.Participants)
                    .ThenInclude(p => p.User)
                .FirstOrDefaultAsync(c => c.Id == id);
        }

        public async Task<List<Conversation>> GetUserConversationsAsync(Guid userId)
        {
            var conversations = await _context.Conversations
                .AsNoTracking()
                .Include(c => c.Participants)
                    .ThenInclude(p => p.User)
                .Where(c => c.Participants.Any(p => p.UserId == userId && p.LeftAt == null))
                .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
                .ToListAsync();

            if (!conversations.Any())
            {
                return conversations;
            }

            var conversationIds = conversations.Select(c => c.Id).ToList();

            // Fetch the latest message for each of these conversations using an index seek on (ConversationId, SentAt)
            var latestMessages = await _context.Messages
                .AsNoTracking()
                .Where(m => conversationIds.Contains(m.ConversationId))
                .Where(m => !_context.Messages.Any(m2 => m2.ConversationId == m.ConversationId && m2.SentAt > m.SentAt))
                .ToListAsync();

            var messageLookup = latestMessages
                .GroupBy(m => m.ConversationId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.SentAt).First());

            foreach (var conv in conversations)
            {
                if (messageLookup.TryGetValue(conv.Id, out var msg))
                {
                    conv.Messages = new List<Message> { msg };
                }
            }

            return conversations;
        }

        public async Task<Conversation?> GetPrivateConversationAsync(Guid user1Id, Guid user2Id)
        {
            return await _context.Conversations
                .AsNoTracking()
                .Where(c => !c.IsGroupChat)
                .Where(c => c.Participants.Any(p => p.UserId == user1Id && p.LeftAt == null)
                         && c.Participants.Any(p => p.UserId == user2Id && p.LeftAt == null))
                .Include(c => c.Participants)
                    .ThenInclude(p => p.User)
                .FirstOrDefaultAsync();
        }

        public async Task<Conversation> CreateAsync(Conversation conversation)
        {
            _context.Conversations.Add(conversation);
            await _context.SaveChangesAsync();
            return conversation;
        }

        public async Task<Conversation> CreateWithParticipantsAsync(Conversation conversation, IEnumerable<Guid> participantIds, Guid creatorId)
        {
            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                _context.Conversations.Add(conversation);
                await _context.SaveChangesAsync();

                foreach (var participantId in participantIds)
                {
                    var participant = new ConversationParticipant
                    {
                        ConversationId = conversation.Id,
                        UserId = participantId,
                        JoinedAt = DateTime.UtcNow,
                        IsAdmin = participantId == creatorId
                    };
                    _context.ConversationParticipants.Add(participant);
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                var reloaded = await GetByIdAsync(conversation.Id);
                return reloaded ?? conversation;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task<Conversation> UpdateAsync(Conversation conversation)
        {
            var existing = await _context.Conversations.FindAsync(conversation.Id);
            if (existing != null)
            {
                existing.LastMessageAt = conversation.LastMessageAt;
                existing.Name = conversation.Name;
                await _context.SaveChangesAsync();
                return existing;
            }

            _context.Conversations.Update(conversation);
            await _context.SaveChangesAsync();
            return conversation;
        }

        public async Task DeleteAsync(Guid id)
        {
            var conversation = await _context.Conversations.FirstOrDefaultAsync(c => c.Id == id);
            if (conversation != null)
            {
                _context.Conversations.Remove(conversation);
                await _context.SaveChangesAsync();
            }
        }

        public async Task<ConversationParticipant?> GetParticipantAsync(Guid conversationId, Guid userId)
        {
            return await _context.ConversationParticipants
                .Include(p => p.User)
                .FirstOrDefaultAsync(p => p.ConversationId == conversationId && p.UserId == userId);
        }

        public async Task<ConversationParticipant> AddParticipantAsync(Guid conversationId, Guid userId, bool isAdmin = false)
        {
            var existing = await _context.ConversationParticipants
                .FirstOrDefaultAsync(p => p.ConversationId == conversationId && p.UserId == userId);

            if (existing != null)
            {
                if (existing.LeftAt != null)
                {
                    existing.LeftAt = null;
                    existing.JoinedAt = DateTime.UtcNow;
                    existing.IsAdmin = isAdmin;
                    await _context.SaveChangesAsync();
                }
                return existing;
            }

            var participant = new ConversationParticipant
            {
                ConversationId = conversationId,
                UserId = userId,
                JoinedAt = DateTime.UtcNow,
                IsAdmin = isAdmin
            };

            _context.ConversationParticipants.Add(participant);
            await _context.SaveChangesAsync();
            return participant;
        }

        public async Task<bool> RemoveParticipantAsync(Guid conversationId, Guid userId)
        {
            var participant = await _context.ConversationParticipants
                .FirstOrDefaultAsync(p => p.ConversationId == conversationId && p.UserId == userId);

            if (participant != null)
            {
                participant.LeftAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return true;
            }

            return false;
        }

        public async Task<bool> UpdateAdminStatusAsync(Guid conversationId, Guid userId, bool isAdmin)
        {
            var participant = await _context.ConversationParticipants
                .FirstOrDefaultAsync(p => p.ConversationId == conversationId && p.UserId == userId && p.LeftAt == null);

            if (participant != null)
            {
                participant.IsAdmin = isAdmin;
                await _context.SaveChangesAsync();
                return true;
            }

            return false;
        }

        public async Task<bool> IsParticipantAsync(Guid conversationId, Guid userId)
        {
            return await _context.ConversationParticipants
                .AsNoTracking()
                .AnyAsync(p => p.ConversationId == conversationId && p.UserId == userId && p.LeftAt == null);
        }

        public async Task<bool> ExistsAsync(Guid conversationId)
        {
            return await _context.Conversations
                .AsNoTracking()
                .AnyAsync(c => c.Id == conversationId);
        }
    }
}
