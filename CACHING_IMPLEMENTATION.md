# Caching Implementation for Print and Printer Summaries

## Overview
Implemented a version-based caching strategy for the `GetPrintSummary` and `GetPrinterSummary` endpoints using in-memory caching with automatic memory management and per-user cache invalidation.

## Files Created

### 1. `PrintLogApi\Services\ICacheVersionService.cs`
Interface defining the cache version service contract with two methods:
- `GetUserCacheVersion(long userId)` - Retrieves the current cache version for a user
- `InvalidateUserCache(long userId)` - Invalidates all cached data for a user by generating a new version

### 2. `PrintLogApi\Services\CacheVersionService.cs`
Implementation of the cache version service using `IMemoryCache`:
- Stores a GUID-based version string per user
- Version entries have a sliding expiration of 24 hours and absolute expiration of 7 days
- Each version entry uses minimal memory (size = 1 unit)
- When a user's cache is invalidated, a new GUID is generated and stored

## Files Modified

### 1. `PrintLogApi\Startup.cs`
**Changes:**
- Added memory cache configuration with:
  - Size limit: 1024 units (~1MB)
  - Compaction percentage: 25% (removes 25% of entries when limit reached)
  - Expiration scan frequency: 2 minutes
- Registered `ICacheVersionService` as a singleton

### 2. `PrintLogApi\Controllers\PrintsController.cs`
**Changes:**
- Added `IMemoryCache` and `ICacheVersionService` dependencies
- Added cache key prefix constant: `PRINT_SUMMARY_CACHE_PREFIX`

**Modified Methods:**
- `GetPrintSummary()` - Implemented caching logic:
  - Generates version-based cache key using user ID and query parameters
  - Checks cache before querying database
  - Stores results in cache with:
    - Sliding expiration: 5 minutes
    - Absolute expiration: 15 minutes
    - Dynamic size estimation based on result count (~2KB per print)
    - Normal priority

**Cache Invalidation Added to:**
- `PostPrint()` - Invalidates cache after creating a new print
- `PutPrint()` - Invalidates cache after updating a print
- `UpdatePrintStatus()` - Invalidates cache after changing print status
- `DeletePrint()` - Invalidates cache after deleting a print

**Helper Methods Added:**
- `GenerateCacheKey()` - Creates unique cache keys including:
  - User ID
  - Cache version
  - Page number and size
  - Search text
  - Printer ID filters
  - Sort column and direction
  - Status filter
- `EstimateCacheSize()` - Estimates memory footprint (~2KB per print summary item)

### 3. `PrintLogApi\Controllers\PrintersController.cs`
**Changes:**
- Added `IMemoryCache` and `ICacheVersionService` dependencies
- Added cache key prefix constant: `PRINTER_SUMMARY_CACHE_PREFIX`

**Modified Methods:**
- `GetPrinterSummary()` - Implemented caching logic:
  - Generates version-based cache key using user ID and query parameters
  - Checks cache before querying database
  - Stores results in cache with:
    - Sliding expiration: 5 minutes
    - Absolute expiration: 15 minutes
    - Dynamic size estimation based on result count (~3KB per printer including filament)
    - Normal priority

**Cache Invalidation Added to:**
- `PostPrinter()` - Invalidates cache after creating a new printer
- `PutPrinter()` - Invalidates cache after updating a printer
- `UnloadPrinterFilament()` - Invalidates cache after unloading printer filament
- `DeletePrinter()` - Invalidates cache after deleting a printer

**Helper Methods Added:**
- `GeneratePrinterCacheKey()` - Creates unique cache keys including:
  - User ID
  - Cache version
  - Page number and size
  - Search text
  - Include inactive flag
- `EstimatePrinterCacheSize()` - Estimates memory footprint (~3KB per printer summary item)

## How It Works

### Caching Flow
1. When `GetPrintSummary` or `GetPrinterSummary` is called, it:
   - Retrieves the current cache version for the user
   - Generates a cache key based on version + all query parameters
   - Checks if cached result exists and returns it if found
   - Otherwise, queries the database and caches the result

### Invalidation Flow
1. When a user creates, updates, or deletes a print or printer:
   - The cache version service generates a new version GUID for that user
   - All existing cache entries for that user become effectively invalid (different version in key)
   - Next query will miss the cache and fetch fresh data
   - Old cache entries will be automatically removed by memory pressure or expiration

### Cross-Controller Invalidation
The caching strategy uses a **shared user version** across controllers:
- Changing a print invalidates both print and printer caches
- Changing a printer invalidates both printer and print caches
- This ensures consistency when related data changes (e.g., printer loaded filament affects prints)

## Benefits

### ? Performance
- Reduces database load for repeated queries
- Fast cache lookups using memory cache
- Automatic expiration prevents stale data

### ? Memory Safety
- Bounded memory usage with size limits
- Automatic compaction when limit reached
- Sliding expiration removes unused entries
- Small memory footprint for version tracking

### ? Correctness
- Instant invalidation on data changes
- All user queries invalidated together (different filters, pages, etc.)
- Version-based keys eliminate need to track individual cache entries

### ? Maintainability
- Simple implementation
- Easy to add invalidation to new endpoints
- No complex cache key tracking
- Works with standard `IMemoryCache`

## Memory Management

The implementation includes multiple layers of memory protection:

1. **Size Limits**: Global cache size limit of 1024 units (~1MB)
2. **Entry Sizing**: Each entry sized based on item count (~2KB per print)
3. **Expiration Policies**:
   - Sliding: 5 minutes (removes if not accessed)
   - Absolute: 15 minutes (maximum lifetime)
4. **Compaction**: Automatic removal of 25% of entries when limit reached
5. **Priority**: Normal priority allows eviction under memory pressure

## Configuration

To adjust cache behavior, modify in `Startup.cs`:

```csharp
services.AddMemoryCache(options =>
{
    options.SizeLimit = 1024;                              // Total cache size
    options.CompactionPercentage = 0.25;                   // % to remove when full
    options.ExpirationScanFrequency = TimeSpan.FromMinutes(2);
});
```

To adjust per-entry behavior, modify in `PrintsController.cs`:

```csharp
var cacheOptions = new MemoryCacheEntryOptions()
    .SetSize(EstimateCacheSize(result))                    // Entry size
    .SetSlidingExpiration(TimeSpan.FromMinutes(5))         // Inactivity timeout
    .SetAbsoluteExpiration(TimeSpan.FromMinutes(15))       // Maximum lifetime
    .SetPriority(CacheItemPriority.Normal);                // Eviction priority
```

## Testing Recommendations

1. **Cache Hit Testing**: Verify repeated queries return cached results
2. **Invalidation Testing**: Verify cache is invalidated after mutations
3. **Memory Testing**: Monitor memory usage under load
4. **Expiration Testing**: Verify entries expire after inactivity
5. **Concurrency Testing**: Test multiple users simultaneously

## Future Enhancements

Potential improvements if needed:
- Add cache hit/miss metrics to Application Insights
- Implement distributed cache (Redis) for multi-instance deployments
- Add cache warming for frequently accessed data
- Implement selective invalidation for specific query patterns
- Add cache statistics endpoint for monitoring
