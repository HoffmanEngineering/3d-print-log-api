namespace PrintLogApi.Models.DTOs.Sync;

/// <summary>The caller's data version (#234). Opaque: compare it for equality, nothing else.</summary>
/// <param name="Version">Changes whenever anything the snapshot reports may have changed.</param>
public sealed record SyncVersionResponse(string Version);
