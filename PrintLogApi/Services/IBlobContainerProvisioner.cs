namespace PrintLogApi.Services;

/// <summary>
/// Ensures a blob container exists, at most once per container per process.
/// Registered as a singleton because <see cref="IBlobStorageService"/> is transient.
/// </summary>
public interface IBlobContainerProvisioner
{
    Task EnsureAsync(string containerName, Func<Task> create, CancellationToken ct = default);
}
