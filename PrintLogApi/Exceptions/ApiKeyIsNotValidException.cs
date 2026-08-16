using System;
using System.Runtime.Serialization;

namespace PrintLogApi.Services
{
    /// <summary>
    /// Used to indicate that an API key is not a valid key
    /// </summary>
    [Serializable]
    internal class ApiKeyIsNotValidException : Exception
    {
        public ApiKeyIsNotValidException()
        {
        }

        public ApiKeyIsNotValidException(string message) : base(message)
        {
        }

        public ApiKeyIsNotValidException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected ApiKeyIsNotValidException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
