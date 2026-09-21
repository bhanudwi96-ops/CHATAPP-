using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using ChatApp.Application.Interfaces;
using ChatApp.Application.DTOs;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Push;

namespace ChatApp.API.Hubs
{
    /// <summary>
    /// SignalR Hub for real-time chat functionality
    /// Fixed: Uses Group-only broadcasting, proper error handling, structured logging
    /// </summary>
    [Authorize]
    public class ChatHub : Hub
    {
        private readonly IMessageService _messageService;
        private readonly IPresenceService _presenceService;
        private readonly IConversationService _conversationService;
        private readonly ILogger<ChatHub> _logger;
        private readonly IPushProvider _pushProvider;
        private readonly IUserDeviceRepository _userDeviceRepository;

        public ChatHub(
            IMessageService messageService,
            IPresenceService presenceService,
            IConversationService conversationService,
            ILogger<ChatHub> logger,
            IPushProvider pushProvider,
            IUserDeviceRepository userDeviceRepository)
        {
            _messageService = messageService;
            _presenceService = presenceService;
            _conversationService = conversationService;
            _logger = logger;
        }

        public override async Task OnConnectedAsync()
        {
            try
            {
                var userId = GetUserId();
                var username = GetUsername();

                _logger.LogInformation("User connected: {Username} ({UserId}) ConnectionId: {ConnectionId}",
                    username, userId, Context.ConnectionId);

                await _presenceService.UserConnectedAsync(userId, Context.ConnectionId, "Web Browser");

                // Broadcast to ALL — online status is globally relevant
                await Clients.Others.SendAsync("UserOnline", new OnlineStatusDto
                {
                    UserId = userId,
                    Status = UserStatus.Online
                });

                await base.OnConnectedAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnConnectedAsync for ConnectionId: {ConnectionId}", Context.ConnectionId);
            }
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            try
            {
                var userId = GetUserId();
                var username = GetUsername();

                _logger.LogInformation("User disconnected: {Username} ({UserId}) ConnectionId: {ConnectionId}",
                    username, userId, Context.ConnectionId);

                if (exception != null)
                {
                    _logger.LogWarning(exception, "Disconnect exception for {Username}", username);
                }

                await _presenceService.UserDisconnectedAsync(Context.ConnectionId);

                var isStillOnline = await _presenceService.IsUserOnlineAsync(userId);

                if (!isStillOnline)
                {
                    await Clients.Others.SendAsync("UserOffline", new OnlineStatusDto
                    {
                        UserId = userId,
                        Status = UserStatus.Offline,
                        LastSeen = DateTime.UtcNow
                    });
                }

                await base.OnDisconnectedAsync(exception);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnDisconnectedAsync for ConnectionId: {ConnectionId}", Context.ConnectionId);
            }
        }

        public async Task SendMessage(SendMessageDto dto)
        {
            try
            {
                var userId = GetUserId();
                _logger.LogInformation("User {UserId} sending message to conversation {ConversationId}",
                    userId, dto.ConversationId);

                var message = await _messageService.SendMessageAsync(userId, dto);

                // Send to conversation group only — only participants should see this
                await Clients.Group(dto.ConversationId.ToString()).SendAsync("ReceiveMessage", message);

                // Send notification to others for sidebar unread updates
                await Clients.Others.SendAsync("ReceiveMessageNotification", message);

                // Fire FCM push to offline participants
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var conversation = await _conversationService.GetConversationByIdAsync(dto.ConversationId, userId);
                        foreach (var participant in conversation.Participants)
                        {
                            if (participant.Id == userId) continue; // skip sender
                            var deviceToken = await _userDeviceRepository.GetTokenAsync(participant.Id);
                            if (!string.IsNullOrEmpty(deviceToken))
                            {
                                await _pushProvider.SendAsync(
                                    deviceToken,
                                    title: message.SenderName ?? "New message",
                                    body: message.Content ?? "",
                                    data: new System.Collections.Generic.Dictionary<string, string>
                                    {
                                        { "conversationId", dto.ConversationId.ToString() }
                                    });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to send push notifications for message in conversation {ConversationId}", dto.ConversationId);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending message to conversation {ConversationId}", dto.ConversationId);
                throw new HubException("Failed to send message");
            }
        }

        public async Task EditMessage(Guid messageId, string newContent)
        {
            try
            {
                var userId = GetUserId();
                var updatedMessage = await _messageService.EditMessageAsync(messageId, userId, newContent);

                // Only send to conversation group — not globally
                await Clients.Group(updatedMessage.ConversationId.ToString()).SendAsync("MessageEdited", updatedMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error editing message {MessageId}", messageId);
                throw new HubException("Failed to edit message");
            }
        }

        public async Task DeleteMessage(Guid messageId)
        {
            try
            {
                var userId = GetUserId();
                var message = await _messageService.GetMessageByIdAsync(messageId);
                var conversationId = message.ConversationId;

                await _messageService.DeleteMessageAsync(messageId, userId);

                var deleteDto = new DeleteMessageDto
                {
                    MessageId = messageId,
                    ConversationId = conversationId
                };

                // Only send to conversation group — not globally
                await Clients.Group(conversationId.ToString()).SendAsync("MessageDeleted", deleteDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting message {MessageId}", messageId);
                throw new HubException("Failed to delete message");
            }
        }

        public async Task Typing(Guid conversationId, bool isTyping)
        {
            var userId = GetUserId();
            var username = GetUsername();

            await Clients.OthersInGroup(conversationId.ToString()).SendAsync("UserTyping", new TypingIndicatorDto
            {
                ConversationId = conversationId,
                UserId = userId,
                Username = username,
                IsTyping = isTyping
            });
        }

        public async Task MarkAsRead(Guid messageId)
        {
            try
            {
                var userId = GetUserId();
                await _messageService.MarkAsReadAsync(messageId, userId);

                var message = await _messageService.GetMessageByIdAsync(messageId);
                await Clients.User(message.SenderId.ToString()).SendAsync("MessageRead", new
                {
                    MessageId = messageId,
                    ReadBy = userId,
                    ReadAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking message {MessageId} as read", messageId);
            }
        }

        public async Task MarkConversationAsRead(Guid conversationId)
        {
            try
            {
                var userId = GetUserId();
                await _messageService.MarkConversationAsReadAsync(conversationId, userId);

                await Clients.OthersInGroup(conversationId.ToString()).SendAsync("ConversationRead", new
                {
                    ConversationId = conversationId,
                    ReadBy = userId,
                    ReadAt = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking conversation {ConversationId} as read", conversationId);
            }
        }

        public async Task ReactToMessage(Guid messageId, string emoji)
        {
            try
            {
                var userId = GetUserId();
                var updateDto = await _messageService.ToggleReactionAsync(messageId, userId, emoji);

                // Only broadcast to conversation group — single send, no duplicates
                if (updateDto.ConversationId != Guid.Empty)
                {
                    await Clients.Group(updateDto.ConversationId.ToString()).SendAsync("MessageReactionUpdated", updateDto);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error toggling reaction on message {MessageId}", messageId);
                throw new HubException("Failed to update reaction");
            }
        }

        public async Task NotifyGroupUpdated(Guid conversationId)
        {
            // Only notify group members
            await Clients.Group(conversationId.ToString()).SendAsync("GroupUpdated", new { ConversationId = conversationId });
        }

        public async Task JoinConversation(Guid conversationId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, conversationId.ToString());
            _logger.LogDebug("User joined conversation group: {ConversationId}", conversationId);
        }

        public async Task LeaveConversation(Guid conversationId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, conversationId.ToString());
            _logger.LogDebug("User left conversation group: {ConversationId}", conversationId);
        }

        private Guid GetUserId()
        {
            var userIdClaim = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return Guid.Parse(userIdClaim ?? throw new UnauthorizedAccessException("User not authenticated"));
        }

        private string GetUsername()
        {
            return Context.User?.FindFirst(ClaimTypes.Name)?.Value
                ?? throw new UnauthorizedAccessException("User not authenticated");
        }

        /// <summary>
        /// Pushes a ticket resolution notification to a specific user by their userId.
        /// Called server-side (e.g. by TicketOrchestratorService or admin action).
        /// Client event: "ticketResolved"
        /// </summary>
        public static async Task NotifyTicketResolvedAsync(
            IHubContext<ChatHub> hubContext,
            string userId,
            string referenceNumber,
            string resolutionSummary)
        {
            await hubContext.Clients.User(userId).SendAsync("ticketResolved", new
            {
                referenceNumber,
                resolutionSummary,
                resolvedAt = DateTime.UtcNow
            });
        }
    }
}
