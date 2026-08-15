#nullable enable

using System;
using System.Runtime.Serialization;

namespace PrintLogApi.Services
{
    [Serializable]
    internal class PrinterIsInUseException : Exception
    {
        public PrinterIsInUseException()
        {
        }

        public PrinterIsInUseException(string message) : base(message)
        {
        }

        public PrinterIsInUseException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected PrinterIsInUseException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
