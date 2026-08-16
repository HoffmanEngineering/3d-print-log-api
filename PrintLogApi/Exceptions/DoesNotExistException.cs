namespace PrintLogApi.Exceptions;

public class DoesNotExistException : Exception
{
    public DoesNotExistException(string message) : base(message)
    {
    }

    public DoesNotExistException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public DoesNotExistException()
    {
    }
}
