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
    /// Service for message-related operations
    /// Supports messaging, quoted replies, editing, soft-deletion, and reactions
    /// </summary>
    public class MessageService : IMessageService
    {
        private readonly IMessageRepository _messageRepository;
        private readonly IConversationRepository _conversationRepository;
        private readonly IUserRepository _userRepository;

        public MessageService(
            IMessageRepository messageRepository,
            IConversationRepository conversationRepository,
            IUserRepository userRepository)
        {
            _messageRepository = messageRepository;
            _conversationRepository = conversationRepository;
            _userRepository = userRepository;
        }

        public async Task<MessageDto> SendMessageAsync(Guid senderId, SendMessageDto dto)
        {
            var sender = await _userRepository.GetByIdAsync(senderId)
                ?? throw new NotFoundException("User", senderId);

            var conversation = await _conversationRepository.GetByIdAsync(dto.ConversationId)
                ?? throw new NotFoundException("Conversation", dto.ConversationId);

            Message? replyToMessage = null;
            if (dto.ReplyToMessageId.HasValue)
            {
                replyToMessage = await _messageRepository.GetByIdAsync(dto.ReplyToMessageId.Value);
            }

            var message = new Message
            {
                ConversationId = dto.ConversationId,
                SenderId = senderId,
                Content = dto.Content,
                Type = dto.Type,
                AttachmentUrl = dto.AttachmentUrl,
                ReplyToMessageId = dto.ReplyToMessageId,
                SentAt = DateTime.UtcNow
            };

            message = await _messageRepository.CreateAsync(message);

            conversation.LastMessageAt = DateTime.UtcNow;
            await _conversationRepository.UpdateAsync(conversation);

            return new MessageDto
            {
                Id = message.Id,
                ConversationId = message.ConversationId,
                SenderId = message.SenderId,
                SenderName = sender.DisplayName,
                Content = message.Content,
                Type = message.Type,
                AttachmentUrl = message.AttachmentUrl,
                FileName = dto.FileName,
                FileSize = dto.FileSize,
                IsRead = message.IsRead,
                SentAt = message.SentAt,
                IsEdited = message.IsEdited,
                IsDeleted = message.IsDeleted,
                ReplyToMessageId = message.ReplyToMessageId,
                ReplyToSenderName = replyToMessage?.Sender?.DisplayName,
                ReplyToContent = replyToMessage?.Content,
                Reactions = new List<ReactionDto>()
            };
        }

        public async Task<List<MessageDto>> GetConversationMessagesAsync(Guid conversationId, Guid userId, int skip = 0, int take = 50)
        {
            var isParticipant = await _conversationRepository.IsParticipantAsync(conversationId, userId);
            if (!isParticipant)
            {
                var exists = await _conversationRepository.ExistsAsync(conversationId);
                if (!exists)
                {
                    throw new NotFoundException("Conversation", conversationId);
                }
                throw new ForbiddenException("User is not a participant in this conversation");
            }

            var messages = await _messageRepository.GetConversationMessagesAsync(conversationId, skip, take);
            return messages.Select(m => MapToDto(m, userId)).ToList();
        }

        public async Task<MessageDto> GetMessageByIdAsync(Guid messageId)
        {
            var message = await _messageRepository.GetByIdAsync(messageId)
                ?? throw new NotFoundException("Message", messageId);

            return MapToDto(message, Guid.Empty);
        }

        public async Task<MessageDto> EditMessageAsync(Guid messageId, Guid userId, string newContent)
        {
            var message = await _messageRepository.GetByIdAsync(messageId)
                ?? throw new NotFoundException("Message", messageId);

            if (message.SenderId != userId)
            {
                throw new ForbiddenException("Only the sender can edit this message");
            }

            if (message.IsDeleted)
            {
                throw new BusinessRuleException("Cannot edit a deleted message");
            }

            message.Content = newContent;
            message.IsEdited = true;
            message.EditedAt = DateTime.UtcNow;

            await _messageRepository.UpdateAsync(message);

            return MapToDto(message, userId);
        }

        public async Task<bool> MarkAsReadAsync(Guid messageId, Guid userId)
        {
            await _messageRepository.MarkAsReadAsync(messageId, userId);
            return true;
        }

        public async Task<bool> MarkConversationAsReadAsync(Guid conversationId, Guid userId)
        {
            await _messageRepository.MarkConversationAsReadAsync(conversationId, userId);
            return true;
        }

        public async Task<bool> DeleteMessageAsync(Guid messageId, Guid userId)
        {
            var message = await _messageRepository.GetByIdAsync(messageId)
                ?? throw new NotFoundException("Message", messageId);

            var isSender = message.SenderId == userId;
            var isGroupAdmin = false;

            if (!isSender && message.Conversation != null)
            {
                var participant = await _conversationRepository.GetParticipantAsync(message.ConversationId, userId);
                isGroupAdmin = participant?.IsAdmin == true;
            }

            if (!isSender && !isGroupAdmin)
            {
                throw new ForbiddenException("Only the sender or group admin can delete this message");
            }

            await _messageRepository.DeleteAsync(messageId);
            return true;
        }

        public async Task<ReactionUpdateDto> ToggleReactionAsync(Guid messageId, Guid userId, string emoji)
        {
            var reactions = await _messageRepository.ToggleReactionAsync(messageId, userId, emoji);
            var message = await _messageRepository.GetByIdAsync(messageId);

            var groupedReactions = GroupReactions(reactions, userId);

            return new ReactionUpdateDto
            {
                MessageId = messageId,
                ConversationId = message?.ConversationId ?? Guid.Empty,
                Reactions = groupedReactions
            };
        }

        private static MessageDto MapToDto(Message m, Guid currentUserId)
        {
            return new MessageDto
            {
                Id = m.Id,
                ConversationId = m.ConversationId,
                SenderId = m.SenderId,
                SenderName = m.Sender?.DisplayName ?? "Unknown",
                Content = m.IsDeleted ? "This message was deleted" : m.Content,
                Type = m.Type,
                AttachmentUrl = m.IsDeleted ? null : m.AttachmentUrl,
                IsRead = m.IsRead,
                SentAt = m.SentAt,
                IsEdited = m.IsEdited,
                EditedAt = m.EditedAt,
                IsDeleted = m.IsDeleted,
                ReplyToMessageId = m.ReplyToMessageId,
                ReplyToSenderName = m.ReplyToMessage?.Sender?.DisplayName,
                ReplyToContent = m.ReplyToMessage?.IsDeleted == true ? "This message was deleted" : m.ReplyToMessage?.Content,
                Reactions = m.IsDeleted ? new List<ReactionDto>() : GroupReactions(m.Reactions, currentUserId)
            };
        }

        private static List<ReactionDto> GroupReactions(IEnumerable<MessageReaction>? reactions, Guid currentUserId)
        {
            if (reactions == null) return new List<ReactionDto>();

            return reactions
                .GroupBy(r => r.Emoji)
                .Select(g => new ReactionDto
                {
                    Emoji = g.Key,
                    Count = g.Count(),
                    UserIds = g.Select(r => r.UserId).ToList(),
                    Usernames = g.Select(r => r.User?.DisplayName ?? "Unknown").ToList(),
                    HasReacted = currentUserId != Guid.Empty && g.Any(r => r.UserId == currentUserId)
                })
                .ToList();
        }
    }
}
