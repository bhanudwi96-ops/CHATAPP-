using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ChatApp.Application.DTOs;
using ChatApp.Application.Interfaces;

namespace ChatApp.API.Controllers
{
    [Authorize]
    public class UserController : BaseApiController
    {
        private readonly IUserService _userService;

        public UserController(IUserService userService)
        {
            _userService = userService;
        }

        [HttpGet("me")]
        public async Task<IActionResult> GetCurrentUser()
        {
            var userId = GetCurrentUserId();
            var user = await _userService.GetCurrentUserAsync(userId);
            return Ok(user);
        }

        [HttpPut("profile")]
        public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileDto dto)
        {
            var userId = GetCurrentUserId();
            var updated = await _userService.UpdateProfileAsync(userId, dto);
            return Ok(updated);
        }

        [HttpPut("profile-picture")]
        public async Task<IActionResult> UpdateProfilePicture([FromBody] UpdateProfileDto dto)
        {
            var userId = GetCurrentUserId();
            var updated = await _userService.UpdateProfilePictureAsync(userId, dto.ProfilePictureUrl);
            return Ok(updated);
        }

        [HttpGet("search")]
        public async Task<IActionResult> Search([FromQuery] string? query = null)
        {
            var userId = GetCurrentUserId();
            var users = await _userService.SearchUsersAsync(query ?? string.Empty, userId);
            return Ok(users);
        }

        [HttpGet("all")]
        public async Task<IActionResult> GetAll([FromQuery] int skip = 0, [FromQuery] int take = 50)
        {
            var userId = GetCurrentUserId();
            var users = await _userService.GetAllUsersAsync(userId, skip, take);
            return Ok(users);
        }
    }
}
