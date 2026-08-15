#nullable enable

using System;

namespace PrintLogApi.Exceptions
{
    public class SubscriptionException : Exception
    {
        public SubscriptionException(string message) : base(message) { }
        public SubscriptionException(string message, Exception innerException) : base(message, innerException) { }
        public SubscriptionException() { }
    }
}
