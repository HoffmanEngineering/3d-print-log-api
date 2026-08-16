using System;
using System.Runtime.Serialization;

namespace PrintLogApi.Exceptions
{
    [Serializable]
    public class PrinterDoesNotExistException : Exception
    {
        public PrinterDoesNotExistException()
        {
        }

        public PrinterDoesNotExistException(string message) : base(message)
        {
        }

        public PrinterDoesNotExistException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected PrinterDoesNotExistException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }
    }
}
