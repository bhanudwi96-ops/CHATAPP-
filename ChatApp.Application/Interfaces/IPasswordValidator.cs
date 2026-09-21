namespace ChatApp.Application.Interfaces
{
    /// <summary>
    /// Reusable password validator ensuring identical validation rules between Registration and Password Reset
    /// </summary>
    public interface IPasswordValidator
    {
        (bool IsValid, string? ErrorMessage) Validate(string password);
    }
}
