using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ChatApp.Infrastructure.Options;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ChatApp.Infrastructure.Email
{
    public class SmtpEmailProvider : IEmailProvider
    {
        private readonly EmailSettings _settings;
        private readonly ILogger<SmtpEmailProvider> _logger;

        public SmtpEmailProvider(IOptions<EmailSettings> options, ILogger<SmtpEmailProvider> logger)
        {
            _settings = options.Value;
            _logger = logger;
        }

        public async Task SendAsync(
            string toEmail, 
            string toName, 
            string subject, 
            string htmlBody, 
            string? plainText = null, 
            CancellationToken cancellationToken = default)
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromEmail));
            message.To.Add(new MailboxAddress(toName, toEmail));
            message.Subject = subject;

            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = htmlBody,
                TextBody = plainText
            };
            message.Body = bodyBuilder.ToMessageBody();

            using var client = new SmtpClient();

            // Resolve DNS and prioritize IPv6 to bypass IPv4 timeouts on local network/ISP
            IPAddress[] v6Addresses = Array.Empty<IPAddress>();
            try
            {
                v6Addresses = await Dns.GetHostAddressesAsync(_settings.SmtpHost, AddressFamily.InterNetworkV6, cancellationToken);
            }
            catch {}

            IPAddress[] v4Addresses = Array.Empty<IPAddress>();
            try
            {
                v4Addresses = await Dns.GetHostAddressesAsync(_settings.SmtpHost, AddressFamily.InterNetwork, cancellationToken);
            }
            catch {}

            if (v6Addresses.Length == 0 && (_settings.SmtpHost.EndsWith("gmail.com", StringComparison.OrdinalIgnoreCase) || _settings.SmtpHost.Contains("google", StringComparison.OrdinalIgnoreCase)))
            {
                v6Addresses = new[]
                {
                    IPAddress.Parse("2404:6800:4000:1025::6c"),
                    IPAddress.Parse("2404:6800:4000:1025::6d")
                };
            }

            var sorted = v6Addresses.Concat(v4Addresses).ToArray();

            Socket? socket = null;
            foreach (var ip in sorted)
            {
                try
                {
                    var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                    await s.ConnectAsync(ip, _settings.SmtpPort, linkedCts.Token);
                    socket = s;
                    break;
                }
                catch
                {
                    // Try next address
                }
            }

            var sslOption = _settings.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
            if (socket != null)
            {
                await client.ConnectAsync(socket, _settings.SmtpHost, _settings.SmtpPort, sslOption, cancellationToken);
            }
            else
            {
                await client.ConnectAsync(_settings.SmtpHost, _settings.SmtpPort, sslOption, cancellationToken);
            }

            if (!string.IsNullOrEmpty(_settings.SmtpUser))
            {
                var password = _settings.SmtpPass?.Replace(" ", "").Trim();
                await client.AuthenticateAsync(_settings.SmtpUser, password, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);

            _logger.LogInformation("SMTP Email sent successfully to {ToEmail} with subject '{Subject}'", toEmail, subject);
        }
    }
}
