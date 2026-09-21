using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks;
using ChatApp.Application.Interfaces;
using ChatApp.Application.DTOs;

namespace ChatApp.API.Controllers
{
    /// <summary>
    /// Controller for authentication endpoints (Register, Login)
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly IPasswordRecoveryService _passwordRecoveryService;

        public AuthController(
            IAuthService authService,
            IPasswordRecoveryService passwordRecoveryService)
        {
            _authService = authService;
            _passwordRecoveryService = passwordRecoveryService;
        }

        /// <summary>
        /// Register a new user
        /// POST /api/auth/register
        /// </summary>
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterDto registerDto)
        {
            try
            {
                var response = await _authService.RegisterAsync(registerDto);
                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Login with username and password
        /// POST /api/auth/login
        /// </summary>
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto loginDto)
        {
            try
            {
                var response = await _authService.LoginAsync(loginDto);
                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Step 1: Request 6-digit OTP for password reset
        /// POST /api/auth/forgot-password
        /// </summary>
        [HttpPost("forgot-password")]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequestDto requestDto)
        {
            try
            {
                var response = await _passwordRecoveryService.RequestPasswordResetAsync(requestDto.Email);
                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Step 2: Validate 6-digit OTP and receive short-lived ResetToken
        /// POST /api/auth/forgot-password/verify-otp
        /// </summary>
        [HttpPost("forgot-password/verify-otp")]
        public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpRequestDto requestDto)
        {
            try
            {
                var response = await _passwordRecoveryService.VerifyPasswordResetOtpAsync(requestDto.Email, requestDto.Otp);
                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Step 3: Complete password reset with ResetToken, Old Password, and New Password
        /// POST /api/auth/reset-password
        /// </summary>
        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordDto requestDto)
        {
            try
            {
                await _passwordRecoveryService.ResetPasswordAsync(requestDto);
                return Ok(new { success = true, message = "Password changed successfully. You may now log in with your new password." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        /// <summary>
        /// Test endpoint to verify API is working
        /// GET /api/auth/test
        /// </summary>
        [HttpGet("test")]
        public IActionResult Test()
        {
            return Ok(new { message = "Auth API is working!", timestamp = DateTime.UtcNow });
        }
    }
}
