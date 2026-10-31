namespace PrintLogApi.Exceptions;

/// <summary>
/// Image processing's memory budget stayed spent for longer than the gate waits
/// (<see cref="Services.ImageMemoryGate"/>). Answered 503 with <c>Retry-After</c> by
/// <see cref="Middleware.ImageProcessingBusyFilter"/>; the upload itself was fine and can be retried.
/// </summary>
public class ImageProcessingBusyException() : Exception("Image processing is busy. Try the upload again shortly.");
