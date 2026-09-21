using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ChatApp.Application.Interfaces;
using ChatApp.Application.DTOs;

namespace ChatApp.API.Controllers
{
    /// <summary>
    /// Controller for message management endpoints
    /// </summary>
    [Authorize]
    public class MessageController : BaseApiController
    {
        private readonly IMessageService _messageService;

        public MessageController(IMessageService messageService)
        {
            _messageService = messageService;
        }

        /// <summary>
        /// Get messages for a conversation
        /// </summary>
        [HttpGet("conversation/{conversationId}")]
        public async Task<IActionResult> GetConversationMessages(
            Guid conversationId,
            [FromQuery] int skip = 0,
            [FromQuery] int take = 50)
        {
            var userId = GetCurrentUserId();
            var messages = await _messageService.GetConversationMessagesAsync(conversationId, userId, skip, take);
            return Ok(messages);
        }

        /// <summary>
        /// Get a specific message by ID
        /// </summary>
        [HttpGet("{id}")]
        public async Task<IActionResult> GetMessageById(Guid id)
        {
            var message = await _messageService.GetMessageByIdAsync(id);
            return Ok(message);
        }

        /// <summary>
        /// Send a message
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SendMessage([FromBody] SendMessageDto dto)
        {
            var userId = GetCurrentUserId();
            var message = await _messageService.SendMessageAsync(userId, dto);
            return Ok(message);
        }

        /// <summary>
        /// Edit a message
        /// </summary>
        [HttpPut("{id}")]
        public async Task<IActionResult> EditMessage(Guid id, [FromBody] EditMessageDto dto)
        {
            var userId = GetCurrentUserId();
            var updated = await _messageService.EditMessageAsync(id, userId, dto.Content);
            return Ok(updated);
        }

        /// <summary>
        /// Mark a message as read
        /// </summary>
        [HttpPut("{id}/read")]
        public async Task<IActionResult> MarkAsRead(Guid id)
        {
            var userId = GetCurrentUserId();
            await _messageService.MarkAsReadAsync(id, userId);
            return Ok(new { message = "Message marked as read" });
        }

        /// <summary>
        /// Mark all messages in a conversation as read
        /// </summary>
        [HttpPut("conversation/{conversationId}/read")]
        public async Task<IActionResult> MarkConversationAsRead(Guid conversationId)
        {
            var userId = GetCurrentUserId();
            await _messageService.MarkConversationAsReadAsync(conversationId, userId);
            return Ok(new { message = "Conversation marked as read" });
        }

        /// <summary>
        /// Delete a message
        /// </summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteMessage(Guid id)
        {
            var userId = GetCurrentUserId();
            await _messageService.DeleteMessageAsync(id, userId);
            return Ok(new { message = "Message deleted successfully" });
        }
    }
}
