#nullable enable

using System;

namespace PrintLogApi.Exceptions
{
    public class UserCannotAccessFilamentException : Exception
    {
        public UserCannotAccessFilamentException(string message) : base(message)
        {
        }

        public UserCannotAccessFilamentException(string message, Exception innerException) : base(message, innerException)
        {
        }

        public UserCannotAccessFilamentException()
        {
        }
    }
}
