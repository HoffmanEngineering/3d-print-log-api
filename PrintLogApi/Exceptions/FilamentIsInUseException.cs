using System;
using System.Runtime.Serialization;

namespace PrintLogApi.Services
{
    [Serializable]
    internal class FilamentIsInUseException : Exception
    {
        public FilamentIsInUseException()
        {
        }

        public FilamentIsInUseException(string message) : base(message)
        {
        }

        public FilamentIsInUseException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected FilamentIsInUseException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
