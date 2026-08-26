namespace PrintLogApi.Exceptions;

/// <summary>
/// Thrown when uploaded bytes are not a decodable image in an allowed format and
/// within configured limits. Always maps to a 400.
/// </summary>
public class InvalidImageException(string message) : Exception(message);
