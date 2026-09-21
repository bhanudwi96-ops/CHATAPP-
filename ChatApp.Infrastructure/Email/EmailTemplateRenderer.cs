using System;
using System.Collections.Generic;
using System.IO;

namespace ChatApp.Infrastructure.Email
{
    public interface IEmailTemplateRenderer
    {
        string Render(string templateType, Dictionary<string, string> data);
    }

    public class EmailTemplateRenderer : IEmailTemplateRenderer
    {
        public string Render(string templateType, Dictionary<string, string> data)
        {
            var fileName = $"{templateType}.html";
            
            // Look for template in the application base directory or project directory
            var candidatePaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Email", "Templates", fileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Templates", fileName),
                Path.Combine(Directory.GetCurrentDirectory(), "Email", "Templates", fileName),
                Path.Combine(AppContext.BaseDirectory, fileName)
            };

            string templateContent = string.Empty;
            foreach (var path in candidatePaths)
            {
                if (File.Exists(path))
                {
                    templateContent = File.ReadAllText(path);
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(templateContent))
            {
                // Fallback default clean template if file is not on disk
                templateContent = @"
<!DOCTYPE html>
<html>
<head><meta charset='utf-8'></head>
<body style='font-family:sans-serif;background:#0f172a;color:#e2e8f0;padding:20px;'>
  <div style='max-width:550px;margin:auto;background:#1e293b;padding:24px;border-radius:10px;border:1px solid #334155;'>
    <h2 style='color:#38bdf8;'>{{Subject}}</h2>
    <p>Hi <strong>{{UserName}}</strong>,</p>
    <div style='background:#0f172a;padding:12px;border-left:4px solid #38bdf8;margin:15px 0;'>{{Message}}</div>
    <p style='font-size:12px;color:#94a3b8;'>Reference: {{ReferenceNumber}}</p>
  </div>
</body>
</html>";
            }

            foreach (var kvp in data)
            {
                templateContent = templateContent.Replace($"{{{{{kvp.Key}}}}}", kvp.Value ?? string.Empty);
            }

            return templateContent;
        }
    }
}
