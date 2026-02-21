# Low Hanging Fruit - PrintLogApi

Quick wins organized by category and priority. Each item includes file path, issue, and suggested fix.

---

## Priority 1 - High (Bugs / Security)

### ~~Authorization Logic: Latent OR/AND Issue~~ ✅ Fixed

Two places in `PrintsController.cs` use `||` where `&&` is arguably more correct:

- `PrintsController.cs` ~line 258 - `UpdatePrint()`
- `PrintsController.cs` ~line 309 - `UpdatePrintStatus()`

```csharp
if (userId != existingPrint.CreatedById || userId != existingPrint.Printer.UserId)
```

**Currently not a bug** because `CreatedById` (from `TimestampEntity`) and `Printer.UserId` are always the same user in the current app — users only log prints on their own printers. The `||` and `&&` produce identical results when the two values are equal.

**Would become a bug** if the app ever supports shared printers or any scenario where `CreatedById != Printer.UserId`. In that case, neither the creator nor the printer owner could update the print — both get blocked by the `||` check.

**Consider:** Changing to `&&` now to make the intent explicit and guard against future scenarios.

---

### ~~IFormFile Null Checks Missing~~ ✅ Fixed

If a request is sent without a file, accessing `image.FileName` or `image.Length` throws a `NullReferenceException`.

- `PrintsController.cs` ~line 585 - `PostImage()`
- `UsersController.cs` ~line 250 - `PostProfileImage()`
- `UsersController.cs` ~line 297 - `PostCoverImage()`

**Fix:** Add null check at start of each method:
```csharp
if (image == null) return BadRequest("Image file is required.");
```

---

### ~~Missing File Type Validation on Uploads~~ ✅ Fixed

Any file type can be uploaded - no check for image MIME type or extension.

- `PrintsController.cs` ~line 585 - `PostImage()`
- `UsersController.cs` ~line 250 - `PostProfileImage()`
- `UsersController.cs` ~line 297 - `PostCoverImage()`

**Fix:** Validate extension or content type before passing to storage service.

---

### ~~Missing File Size Validation on Uploads~~ ✅ Fixed

No upper bound on uploaded file size - potential DoS vector.

- `PrintsController.cs` ~line 585 - `PostImage()`
- `UsersController.cs` ~line 250 - `PostProfileImage()`
- `UsersController.cs` ~line 297 - `PostCoverImage()`

**Fix:** Add a size check before upload:
```csharp
if (image.Length > 10 * 1024 * 1024) return BadRequest("File must be under 10MB.");
```

---

## Priority 2 - Medium (Data Integrity / Correctness)

### ~~Missing `[Required]` on Key DTOs~~ N/A

Checked against entity models — none of these fields are `[Required]` in the database (all are nullable columns). Adding `[Required]` to DTOs would be stricter than the DB allows and would break valid use cases (e.g. unbranded filament, comments stored without body). No change made.

---

### ~~`BadRequest()` Calls Without Messages~~ ✅ Fixed

These return HTTP 400 with no body, giving clients no feedback on what went wrong.

- `PrintsController.cs` ~line 242
- `FilamentsController.cs` ~line 125
- `PrintersController.cs` ~line 197
- `PrinterMaintenanceController.cs` ~line 125

**Fix:**
```csharp
return BadRequest("ID in route does not match body.");
```

---

### ~~`Forbid()` Used Instead of `Unauthorized()`~~ ✅ Fixed

`Forbid()` (403) is semantically "you don't have permission." `Unauthorized()` (401) is "you're not logged in." Several endpoints use the wrong one.

- `FilamentsController.cs` ~lines 234, 256, 279 - returns `Forbid()` when user is not authenticated

**Fix:** Change to `return Unauthorized();`

---

### ~~Wrong Status Code on PUT Endpoint~~ ✅ Fixed

- `PrintersController.cs` ~line 261 - `UpdatePrinter()` returns `CreatedAtAction` (201) but it's an update operation.

**Fix:** Change to `return Ok(...)` (200).

---

### ~~`ReorderImagesDto.DisplayOrder` Missing Range Validation~~ ✅ Fixed

- `Models/DTOs/Print/ReorderImagesDto.cs` ~line 16

**Fix:**
```csharp
[Range(0, int.MaxValue)]
public int DisplayOrder { get; set; }
```

---

## Priority 3 - Low (Code Quality / Cleanup)

### ~~TODO/FIXME Comments~~ ✅ Fixed

- `PrintService.cs` ~line 677 - Typo: `"TODO: User the currentUserId..."` → should be `"Use"`
- `CommentsController.cs` ~line 41 - TODO about authorization for a commented-out `GetComment()` endpoint

---

### ~~Dead Code - Commented-Out Blocks~~ ✅ Fixed

- `CommentsController.cs` ~lines 41-54 - Entire `GetComment()` method is commented out. Remove it or implement it.
- `Startup.cs` ~lines 196-210 - Commented-out `JwtBearerEvents` handlers (`OnAuthenticationFailed`, `OnTokenValidated`). Remove if not planned.

---

### ~~Inconsistent Parameter Casing~~ ✅ Fixed

- `PrintsController.cs` ~lines 473, 677 - Parameter named `printid` (all lowercase) while the rest of the codebase uses `printId` (camelCase).

---

### ~~Inconsistent Error Response Patterns~~ ✅ Fixed

- `CommentsController.cs` - line 84 used `StatusCode(403, "message")` but line 139 used `return Forbid()` for the same scenario. Standardized to `Forbid()`.
- Various controllers mix `Unauthorized()` (no body) with `Unauthorized("message")`.

Standardize to one pattern throughout. Recommended: `return StatusCode(403, "Reason here.");`

---

## Summary

| Priority | Category | Count |
|----------|----------|-------|
| High | Auth logic bugs (OR vs AND) | 2 |
| High | IFormFile null checks | 3 |
| High | File type/size validation | 2 locations × 2 checks |
| Medium | Missing `[Required]` on DTOs | ~8 properties |
| Medium | `BadRequest()` without messages | 4 |
| Medium | Wrong Forbid vs Unauthorized | 3 |
| Medium | Wrong status codes | 2 |
| Low | TODO typo fix | 1 |
| Low | Dead code removal | 2 blocks |
| Low | Parameter casing | 2 |
| Low | Inconsistent error patterns | Multiple |
