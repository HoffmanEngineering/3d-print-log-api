using System;
using System.Runtime.Serialization;

namespace PrintLogApi.Exceptions
{
    [Serializable]
    public class UserCannotAccessApiKeyException : Exception
    {
        public UserCannotAccessApiKeyException()
        {
        }

        public UserCannotAccessApiKeyException(string message) : base(message)
        {
        }

        public UserCannotAccessApiKeyException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected UserCannotAccessApiKeyException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
