using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ChatApp.Domain.Entities;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ChatApp.API.Services
{
    public interface IEmailService
    {
        Task SendTicketConfirmationAsync(Ticket ticket, string? customerEmail = null, string? customerName = null);
    }

    public class EmailService : IEmailService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task SendTicketConfirmationAsync(Ticket ticket, string? customerEmail = null, string? customerName = null)
        {
            try
            {
                // Read from Smtp:* with fallback to EmailSettings:* if already configured
                var host = _configuration["Smtp:Host"] 
                           ?? _configuration["EmailSettings:SmtpHost"] 
                           ?? "smtp.gmail.com";
                
                var portStr = _configuration["Smtp:Port"] 
                              ?? _configuration["EmailSettings:SmtpPort"] 
                              ?? "587";
                
                int.TryParse(portStr, out var port);
                if (port <= 0) port = 587;

                var user = _configuration["Smtp:User"] 
                           ?? _configuration["EmailSettings:SmtpUser"] 
                           ?? string.Empty;
                
                var pass = _configuration["Smtp:Pass"] 
                           ?? _configuration["EmailSettings:SmtpPass"] 
                           ?? string.Empty;

                var fromAddress = _configuration["Smtp:From"] 
                                  ?? _configuration["EmailSettings:FromEmail"] 
                                  ?? (string.IsNullOrWhiteSpace(user) ? "support@chatapp.com" : user);

                // If credentials are empty/placeholder, log info and return without error
                if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass) || user.Contains("your-", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("SMTP credentials are not configured or are placeholders. Simulated ticket confirmation email for Ticket #{TicketId}.", ticket.Id);
                    return;
                }

                var message = new MimeMessage();
                message.From.Add(new MailboxAddress("ChatApp Support", fromAddress));
                
                // Determine recipient: customer's actual email or fallback to configured support inbox
                var recipientEmail = !string.IsNullOrWhiteSpace(customerEmail) 
                    ? customerEmail.Trim() 
                    : (!string.IsNullOrWhiteSpace(user) ? user : "customer@example.com");

                var recipientName = !string.IsNullOrWhiteSpace(customerName) 
                    ? customerName.Trim() 
                    : $"Customer #{ticket.CustomerId}";

                message.To.Add(new MailboxAddress(recipientName, recipientEmail));

                // If customer has their own email, CC support inbox so admins are notified
                if (!string.IsNullOrWhiteSpace(user) && !recipientEmail.Equals(user, StringComparison.OrdinalIgnoreCase))
                {
                    message.Cc.Add(new MailboxAddress("ChatApp Support Desk", user));
                }

                message.Subject = $"[Ticket #{ticket.Id}] Support Request Received: {ticket.Category}";

                // Format CreatedAt in Indian Standard Time (IST = UTC + 5:30)
                var istTime = ticket.CreatedAt.AddHours(5.5);
                var istFormatted = istTime.ToString("dd-MMM-yyyy hh:mm:ss tt") + " IST";
                var priorityDisplay = ticket.Priority.ToUpperInvariant();

                var bodyText = $@"Hello {recipientName},

Thank you for contacting ChatApp Support. We have received your request and a support ticket has been created.

Ticket Details:
- Ticket ID: #{ticket.Id}
- Requester: {recipientName}
- Category: {ticket.Category}
- Priority: {priorityDisplay}
- Created At: {istFormatted}

Issue Summary:
{ticket.Issue}

Our support team will follow up with you within 24 hours.

Best regards,
ChatApp Customer Support Team";

                message.Body = new TextPart("plain") { Text = bodyText };

                using var client = new SmtpClient();
                client.Timeout = 15000;

                // Resolve DNS explicitly for IPv6 (AAAA) first to bypass IPv4 timeouts on local network/ISP
                IPAddress[] v6Addresses = Array.Empty<IPAddress>();
                try
                {
                    v6Addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetworkV6);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not resolve AAAA IPv6 records for {Host}", host);
                }

                IPAddress[] v4Addresses = Array.Empty<IPAddress>();
                try
                {
                    v4Addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not resolve A IPv4 records for {Host}", host);
                }

                var fallbackIpStrings = _configuration.GetSection("Network:SmtpFallbackIps").Get<string[]>() ?? Array.Empty<string>();
                var configuredFallbackIps = fallbackIpStrings
                    .Select(ipStr => IPAddress.TryParse(ipStr?.Trim(), out var parsed) ? parsed : null)
                    .Where(ip => ip != null)
                    .Select(ip => ip!)
                    .ToArray();

                if (v6Addresses.Length == 0 && configuredFallbackIps.Length > 0 &&
                    (host.EndsWith("gmail.com", StringComparison.OrdinalIgnoreCase) || host.Contains("google", StringComparison.OrdinalIgnoreCase)))
                {
                    v6Addresses = configuredFallbackIps;
                    _logger.LogInformation("Using configured SMTP fallback IP addresses: {Addresses}", string.Join(", ", (object[])v6Addresses));
                }

                var sorted = v6Addresses.Concat(v4Addresses).ToArray();
                _logger.LogInformation("Resolved {Count} addresses for {Host}. Prioritizing IPv6: {Addresses}", 
                    sorted.Length, host, string.Join(", ", sorted.Select(a => $"{a} ({a.AddressFamily})")));

                Socket? socket = null;
                foreach (var ip in sorted)
                {
                    try
                    {
                        _logger.LogInformation("Connecting socket to {IP}:{Port}...", ip, port);
                        var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                        await s.ConnectAsync(ip, port, timeoutCts.Token);
                        _logger.LogInformation("Socket successfully connected to {IP}:{Port}", ip, port);
                        socket = s;
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to connect socket to {IP}:{Port}", ip, port);
                    }
                }

                if (socket != null)
                {
                    _logger.LogInformation("Connecting MailKit over connected socket to {Host}:{Port}...", host, port);
                    await client.ConnectAsync(socket, host, port, SecureSocketOptions.StartTls);
                }
                else
                {
                    _logger.LogWarning("No direct socket connection succeeded. Falling back to MailKit default ConnectAsync...");
                    await client.ConnectAsync(host, port, SecureSocketOptions.StartTls);
                }

                var cleanPass = pass.Replace(" ", "").Trim();
                await client.AuthenticateAsync(user, cleanPass);
                await client.SendAsync(message);
                await client.DisconnectAsync(true);

                _logger.LogInformation("Ticket confirmation email sent successfully to {Recipient} for Ticket #{TicketId}", recipientEmail, ticket.Id);
            }
            catch (Exception ex)
            {
                // Never fail the chat or ticket creation process if email delivery fails
                _logger.LogError(ex, "Failed to send ticket confirmation email for Ticket #{TicketId}. Ticket remains created in database.", ticket.Id);
            }
        }
    }
}
