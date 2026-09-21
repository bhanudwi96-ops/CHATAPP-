using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ChatApp.Infrastructure.Options;

namespace ChatApp.Infrastructure.Push
{
    public class FcmPushProvider : IPushProvider
    {
        private readonly ILogger<FcmPushProvider> _logger;

        public FcmPushProvider(IOptions<PushSettings> options, ILogger<FcmPushProvider> logger)
        {
            _logger = logger;
            var settings = options.Value;

            // Only initialise FirebaseApp if credentials are configured
            if (FirebaseApp.DefaultInstance == null)
            {
                if (!string.IsNullOrWhiteSpace(settings.ServiceAccountPath) &&
                    System.IO.File.Exists(settings.ServiceAccountPath))
                {
                    FirebaseApp.Create(new AppOptions
                    {
                        Credential = GoogleCredential.FromFile(settings.ServiceAccountPath),
                        ProjectId = settings.ProjectId
                    });
                    _logger.LogInformation("FirebaseApp initialized from service account file.");
                }
                else
                {
                    _logger.LogWarning(
                        "FCM ServiceAccountPath is not configured or file does not exist. " +
                        "Push notifications will be skipped until credentials are provided.");
                }
            }
        }

        public async Task SendAsync(
            string deviceToken,
            string title,
            string body,
            IDictionary<string, string>? data = null)
        {
            if (FirebaseApp.DefaultInstance == null)
            {
                _logger.LogWarning("FirebaseApp is not initialised – skipping push notification.");
                return;
            }

            if (string.IsNullOrWhiteSpace(deviceToken))
            {
                _logger.LogWarning("SendAsync called with empty deviceToken – skipping.");
                return;
            }

            try
            {
                var message = new Message
                {
                    Token = deviceToken,
                    Notification = new Notification { Title = title, Body = body },
                    Data = (data ?? new Dictionary<string, string>()) as IReadOnlyDictionary<string, string>
                           ?? new Dictionary<string, string>(data ?? new Dictionary<string, string>())
                };

                var messageId = await FirebaseMessaging.DefaultInstance.SendAsync(message);
                _logger.LogInformation("Push sent. MessageId={MessageId}", messageId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send FCM push to token {Token}", deviceToken);
            }
        }
    }
}
