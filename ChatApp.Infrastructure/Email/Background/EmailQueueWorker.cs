using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChatApp.Application.Interfaces;
using ChatApp.Domain.Entities;
using ChatApp.Infrastructure.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChatApp.Infrastructure.Email.Background
{
    public class EmailQueueWorker : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly IEmailProvider _emailProvider;
        private readonly IEmailTemplateRenderer _templateRenderer;
        private readonly EmailSettings _settings;
        private readonly ILogger<EmailQueueWorker> _logger;

        public EmailQueueWorker(
            IServiceProvider serviceProvider,
            IEmailProvider emailProvider,
            IEmailTemplateRenderer templateRenderer,
            IOptions<EmailSettings> options,
            ILogger<EmailQueueWorker> logger)
        {
            _serviceProvider = serviceProvider;
            _emailProvider = emailProvider;
            _templateRenderer = templateRenderer;
            _settings = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("EmailQueueWorker background service active (Durable SQL Outbox mode).");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var outboxRepo = scope.ServiceProvider.GetRequiredService<IEmailOutboxRepository>();

                    var pendingItems = await outboxRepo.GetPendingBatchAsync(10, stoppingToken);

                    if (pendingItems.Count > 0)
                    {
                        foreach (var item in pendingItems)
                        {
                            await ProcessOutboxItemAsync(item, outboxRepo, stoppingToken);
                        }
                    }

                    // Poll interval: 4 seconds
                    await Task.Delay(TimeSpan.FromSeconds(4), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in EmailQueueWorker loop.");
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
            }

            _logger.LogInformation("EmailQueueWorker stopping.");
        }

        private async Task ProcessOutboxItemAsync(EmailOutbox item, IEmailOutboxRepository repo, CancellationToken cancellationToken)
        {
            try
            {
                item.Status = EmailOutboxStatus.Processing;
                await repo.UpdateStatusAsync(item, cancellationToken);

                // Substitute support email placeholder if needed
                var recipient = item.ToEmail == "SUPPORT_INBOX_PLACEHOLDER" 
                    ? _settings.SupportInboxEmail 
                    : item.ToEmail;

                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(item.PayloadJson) ?? new();
                var htmlBody = _templateRenderer.Render(item.TemplateType, data);

                await _emailProvider.SendAsync(
                    recipient, 
                    item.ToName, 
                    item.Subject, 
                    htmlBody, 
                    null, 
                    cancellationToken);

                item.Status = EmailOutboxStatus.Sent;
                item.ProcessedAt = DateTime.UtcNow;
                item.LastError = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed attempt {Attempt} for outbox item {Id}", item.RetryCount + 1, item.Id);
                item.RetryCount++;
                item.LastError = ex.Message;

                if (item.RetryCount >= item.MaxRetries)
                {
                    item.Status = EmailOutboxStatus.Failed;
                }
                else
                {
                    // Exponential backoff: 30s, 2m, 8m
                    var delaySeconds = Math.Pow(4, item.RetryCount) * 10;
                    item.NextAttemptAt = DateTime.UtcNow.AddSeconds(delaySeconds);
                    item.Status = EmailOutboxStatus.Pending;
                }
            }

            await repo.UpdateStatusAsync(item, cancellationToken);
        }
    }
}
