namespace PrintLogApi.Exceptions;

/// <summary>
/// A phase-one validation failure: the request as a whole is invalid, so nothing was
/// written. The controller turns this into a ProblemDetails 400.
/// </summary>
public class BulkRequestInvalidException(string message) : Exception(message);
