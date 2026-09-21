using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChatApp.Domain.Entities;
using ChatApp.Application.Interfaces;
using ChatApp.Application.DTOs;
using ChatApp.Application.Exceptions;

namespace ChatApp.Application.Services
{
    /// <summary>
    /// Service for conversation-related operations
    /// Fully decoupled from EF Core DbContext - uses clean repository contracts
    /// </summary>
    public class ConversationService : IConversationService
    {
        private readonly IConversationRepository _conversationRepository;
        private readonly IUserRepository _userRepository;
        private readonly IMessageRepository _messageRepository;

        public ConversationService(
            IConversationRepository conversationRepository,
            IUserRepository userRepository,
            IMessageRepository messageRepository)
        {
            _conversationRepository = conversationRepository;
            _userRepository = userRepository;
            _messageRepository = messageRepository;
        }

        public async Task<ConversationDto> CreateConversationAsync(Guid creatorId, CreateConversationDto dto)
        {
            var creator = await _userRepository.GetByIdAsync(creatorId)
                ?? throw new NotFoundException("User", creatorId);

            var allParticipantIds = new HashSet<Guid>(dto.ParticipantIds) { creatorId };

            foreach (var participantId in allParticipantIds)
            {
                if (!await _userRepository.ExistsAsync(participantId))
                {
                    throw new NotFoundException("User", participantId);
                }
            }

            var conversation = new Conversation
            {
                Name = dto.Name,
                IsGroupChat = dto.IsGroupChat,
                CreatedById = creatorId,
                CreatedAt = DateTime.UtcNow
            };

            conversation = await _conversationRepository.CreateWithParticipantsAsync(conversation, allParticipantIds, creatorId);

            return await BuildConversationDto(conversation, creatorId);
        }

        public async Task<List<ConversationDto>> GetUserConversationsAsync(Guid userId)
        {
            var conversations = await _conversationRepository.GetUserConversationsAsync(userId);

            // Batch fetch ALL unread counts in a single DB query instead of N separate queries
            var conversationIds = conversations.Select(c => c.Id).ToList();
            var unreadCounts = await _messageRepository.GetBatchUnreadCountsAsync(conversationIds, userId);

            var conversationDtos = new List<ConversationDto>(conversations.Count);
            foreach (var conversation in conversations)
            {
                unreadCounts.TryGetValue(conversation.Id, out var unreadCount);
                var dto = BuildConversationDtoSync(conversation, unreadCount);
                conversationDtos.Add(dto);
            }

            return conversationDtos;
        }

        public async Task<ConversationDto> GetConversationByIdAsync(Guid conversationId, Guid userId)
        {
            var conversation = await _conversationRepository.GetByIdAsync(conversationId)
                ?? throw new NotFoundException("Conversation", conversationId);

            var isParticipant = conversation.Participants.Any(p => p.UserId == userId && p.LeftAt == null);
            if (!isParticipant)
            {
                throw new ForbiddenException("User is not a participant in this conversation");
            }

            return await BuildConversationDto(conversation, userId);
        }

        public async Task<ConversationDto> GetOrCreatePrivateConversationAsync(Guid user1Id, Guid user2Id)
        {
            var existing = await _conversationRepository.GetPrivateConversationAsync(user1Id, user2Id);
            if (existing != null)
            {
                return await BuildConversationDto(existing, user1Id);
            }

            var createDto = new CreateConversationDto
            {
                ParticipantIds = new List<Guid> { user1Id, user2Id },
                IsGroupChat = false
            };

            return await CreateConversationAsync(user1Id, createDto);
        }

        public async Task<bool> DeleteConversationAsync(Guid conversationId, Guid userId)
        {
            var conversation = await _conversationRepository.GetByIdAsync(conversationId)
                ?? throw new NotFoundException("Conversation", conversationId);

            var participant = conversation.Participants.FirstOrDefault(p => p.UserId == userId);
            if (participant == null || !participant.IsAdmin)
            {
                throw new ForbiddenException("Only admins can delete conversations");
            }

            await _conversationRepository.DeleteAsync(conversationId);
            return true;
        }

        public async Task<ConversationDto> AddParticipantsAsync(Guid conversationId, Guid currentUserId, List<Guid> newParticipantIds)
        {
            var conversation = await _conversationRepository.GetByIdAsync(conversationId)
                ?? throw new NotFoundException("Conversation", conversationId);

            var currentParticipant = conversation.Participants.FirstOrDefault(p => p.UserId == currentUserId && p.LeftAt == null)
                ?? throw new ForbiddenException("You are not a participant in this conversation");

            if (conversation.IsGroupChat && !currentParticipant.IsAdmin)
            {
                throw new ForbiddenException("Only group admins can add new members");
            }

            foreach (var userId in newParticipantIds)
            {
                if (await _userRepository.ExistsAsync(userId))
                {
                    await _conversationRepository.AddParticipantAsync(conversationId, userId, false);
                }
            }

            var reloaded = await _conversationRepository.GetByIdAsync(conversationId);
            return await BuildConversationDto(reloaded ?? conversation, currentUserId);
        }

        public async Task<bool> RemoveParticipantAsync(Guid conversationId, Guid currentUserId, Guid targetUserId)
        {
            var conversation = await _conversationRepository.GetByIdAsync(conversationId)
                ?? throw new NotFoundException("Conversation", conversationId);

            var currentParticipant = conversation.Participants.FirstOrDefault(p => p.UserId == currentUserId && p.LeftAt == null)
                ?? throw new ForbiddenException("You are not a participant in this conversation");

            var isSelf = currentUserId == targetUserId;
            if (!isSelf && !currentParticipant.IsAdmin)
            {
                throw new ForbiddenException("Only admins can remove other participants");
            }

            return await _conversationRepository.RemoveParticipantAsync(conversationId, targetUserId);
        }

        public async Task<bool> LeaveConversationAsync(Guid conversationId, Guid currentUserId)
        {
            return await RemoveParticipantAsync(conversationId, currentUserId, currentUserId);
        }

        public async Task<bool> UpdateAdminStatusAsync(Guid conversationId, Guid currentUserId, Guid targetUserId, bool isAdmin)
        {
            var conversation = await _conversationRepository.GetByIdAsync(conversationId)
                ?? throw new NotFoundException("Conversation", conversationId);

            var currentParticipant = conversation.Participants.FirstOrDefault(p => p.UserId == currentUserId && p.LeftAt == null);
            if (currentParticipant == null || !currentParticipant.IsAdmin)
            {
                throw new ForbiddenException("Only group admins can modify admin permissions");
            }

            return await _conversationRepository.UpdateAdminStatusAsync(conversationId, targetUserId, isAdmin);
        }

        private async Task<ConversationDto> BuildConversationDto(Conversation conversation, Guid currentUserId)
        {
            var unreadCount = await _messageRepository.GetUnreadCountAsync(conversation.Id, currentUserId);
            return BuildConversationDtoSync(conversation, unreadCount);
        }

        /// <summary>
        /// Synchronous DTO builder used with pre-fetched unread counts (batch path)
        /// </summary>
        private ConversationDto BuildConversationDtoSync(Conversation conversation, int unreadCount)
        {
            var dto = new ConversationDto
            {
                Id = conversation.Id,
                Name = conversation.Name,
                IsGroupChat = conversation.IsGroupChat,
                CreatedById = conversation.CreatedById,
                CreatedAt = conversation.CreatedAt,
                LastMessageAt = conversation.LastMessageAt,
                Participants = conversation.Participants
                    .Where(p => p.LeftAt == null && p.User != null)
                    .Select(p => new UserDto
                    {
                        Id = p.User.Id,
                        Username = p.User.Username,
                        DisplayName = p.User.DisplayName,
                        ProfilePictureUrl = p.User.ProfilePictureUrl,
                        Status = p.User.Status,
                        LastSeen = p.User.LastSeen,
                        IsAdmin = p.IsAdmin
                    }).ToList(),
                UnreadCount = unreadCount
            };

            if (conversation.Messages != null && conversation.Messages.Any())
            {
                var lastMessage = conversation.Messages.OrderByDescending(m => m.SentAt).FirstOrDefault();
                if (lastMessage != null)
                {
                    dto.LastMessage = new MessageDto
                    {
                        Id = lastMessage.Id,
                        ConversationId = lastMessage.ConversationId,
                        Content = lastMessage.IsDeleted ? "This message was deleted" : lastMessage.Content,
                        SentAt = lastMessage.SentAt,
                        SenderId = lastMessage.SenderId,
                        Type = lastMessage.Type,
                        AttachmentUrl = lastMessage.IsDeleted ? null : lastMessage.AttachmentUrl,
                        IsRead = lastMessage.IsRead,
                        IsEdited = lastMessage.IsEdited,
                        IsDeleted = lastMessage.IsDeleted
                    };
                }
            }

            return dto;
        }
    }
}
