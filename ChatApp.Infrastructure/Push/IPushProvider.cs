namespace ChatApp.Infrastructure.Push
{
    using System.Collections.Generic;
    using System.Threading.Tasks;

    public interface IPushProvider
    {
        Task SendAsync(string deviceToken, string title, string body, IDictionary<string, string>? data = null);
    }

    public class PushNotification
    {
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string ConversationId { get; set; } = string.Empty;
    }
}
