using System.Threading;
using System.Threading.Tasks;

namespace ChatApp.Infrastructure.Email
{
    public interface IEmailProvider
    {
        Task SendAsync(
            string toEmail, 
            string toName, 
            string subject, 
            string htmlBody, 
            string? plainText = null, 
            CancellationToken cancellationToken = default);
    }
}
