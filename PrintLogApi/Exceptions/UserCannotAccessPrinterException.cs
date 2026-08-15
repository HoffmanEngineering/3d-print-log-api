#nullable enable

using System;

namespace PrintLogApi.Exceptions
{
    public class UserCannotAccessPrinterException: Exception
    {
        public UserCannotAccessPrinterException(string message) : base(message)
        {
        }

        public UserCannotAccessPrinterException(string message, Exception innerException) : base(message, innerException)
        {
        }

        public UserCannotAccessPrinterException()
        {
        }
    }
}
