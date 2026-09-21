using System;

namespace ChatApp.Application.Exceptions
{
    /// <summary>
    /// Thrown when a requested resource is not found (maps to 404)
    /// </summary>
    public class NotFoundException : Exception
    {
        public NotFoundException(string message) : base(message) { }
        public NotFoundException(string resourceName, object key)
            : base($"{resourceName} with key '{key}' was not found") { }
    }

    /// <summary>
    /// Thrown when the user doesn't have permission (maps to 403)
    /// </summary>
    public class ForbiddenException : Exception
    {
        public ForbiddenException(string message) : base(message) { }
    }

    /// <summary>
    /// Thrown for business rule / input validation errors (maps to 400)
    /// </summary>
    public class BusinessRuleException : Exception
    {
        public BusinessRuleException(string message) : base(message) { }
    }

    /// <summary>
    /// Thrown when a duplicate resource is detected (maps to 409)
    /// </summary>
    public class ConflictException : Exception
    {
        public ConflictException(string message) : base(message) { }
    }
}
