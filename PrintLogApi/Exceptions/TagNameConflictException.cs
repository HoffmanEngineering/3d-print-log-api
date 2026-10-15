namespace PrintLogApi.Exceptions;

/// <summary>The user already has a tag with this name, ignoring case.</summary>
public class TagNameConflictException(string name, Exception? innerException = null)
    : Exception($"You already have a tag named \"{name}\".", innerException)
{
}
