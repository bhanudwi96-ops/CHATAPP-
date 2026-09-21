using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ChatApp.Infrastructure.Email
{
    public class DevConsoleEmailProvider : IEmailProvider
    {
        private readonly ILogger<DevConsoleEmailProvider> _logger;

        public DevConsoleEmailProvider(ILogger<DevConsoleEmailProvider> logger)
        {
            _logger = logger;
        }

        public Task SendAsync(
            string toEmail, 
            string toName, 
            string subject, 
            string htmlBody, 
            string? plainText = null, 
            CancellationToken cancellationToken = default)
        {
            var separator = new string('=', 70);
            var log = $"\n{separator}\n" +
                      $"📧 [DEV CONSOLE EMAIL DISPATCHED]\n" +
                      $"To:      {toName} <{toEmail}>\n" +
                      $"Subject: {subject}\n" +
                      $"Time:    {DateTime.UtcNow:u}\n" +
                      $"{separator}\n" +
                      $"{htmlBody}\n" +
                      $"{separator}\n";

            _logger.LogInformation(log);
            return Task.CompletedTask;
        }
    }
}
