namespace ChatApp.Domain.Entities
{
    /// <summary>
    /// Type of message content
    /// </summary>
    public enum MessageType
    {
        Text = 0,
        Image = 1,
        File = 2,
        System = 3  // System messages like "User joined", "User left"
    }
}
