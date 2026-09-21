using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ChatApp.API.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class UploadController : ControllerBase
    {
        private readonly IWebHostEnvironment _environment;

        private static readonly string[] AllowedImageExtensions =
            { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg" };

        private static readonly string[] AllowedDocExtensions =
            { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".csv", ".zip", ".rar", ".7z" };

        private static readonly string[] BlockedExtensions =
            { ".exe", ".bat", ".cmd", ".ps1", ".sh", ".dll", ".msi", ".scr", ".com", ".vbs", ".js", ".wsf", ".hta" };

        private static readonly long MaxFileSizeBytes = 25 * 1024 * 1024; // 25 MB

        public UploadController(IWebHostEnvironment environment)
        {
            _environment = environment;
        }

        [HttpPost]
        [RequestSizeLimit(25 * 1024 * 1024)]
        public async Task<IActionResult> UploadFile(IFormFile? file)
        {
            try
            {
                if (file == null || file.Length == 0)
                {
                    return BadRequest(new { error = "No file provided" });
                }

                if (file.Length > MaxFileSizeBytes)
                {
                    return BadRequest(new { error = "File size exceeds 25 MB limit" });
                }

                var originalFileName = Path.GetFileName(file.FileName);
                var extension = Path.GetExtension(originalFileName).ToLowerInvariant();

                // Block dangerous executable extensions
                if (BlockedExtensions.Contains(extension))
                {
                    return BadRequest(new { error = $"File type '{extension}' is not allowed for security reasons" });
                }

                // Only allow known safe extensions
                var allAllowed = AllowedImageExtensions.Concat(AllowedDocExtensions).ToArray();
                if (!allAllowed.Contains(extension))
                {
                    return BadRequest(new { error = $"File type '{extension}' is not supported. Allowed: images, PDF, Office docs, text, archives" });
                }

                var uploadsFolder = Path.Combine(_environment.ContentRootPath, "wwwroot", "uploads");
                if (!Directory.Exists(uploadsFolder))
                {
                    Directory.CreateDirectory(uploadsFolder);
                }

                var uniqueFileName = $"{Guid.NewGuid():N}{extension}";
                var filePath = Path.Combine(uploadsFolder, uniqueFileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                var isImage = AllowedImageExtensions.Contains(extension);
                var relativeUrl = $"/uploads/{uniqueFileName}";

                return Ok(new
                {
                    url = relativeUrl,
                    fileName = originalFileName,
                    fileSize = file.Length,
                    fileType = isImage ? "image" : "file"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Failed to upload file: " + ex.Message });
            }
        }
    }
}
