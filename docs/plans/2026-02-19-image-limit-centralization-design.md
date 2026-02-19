# Image Limit Centralization Design

**Date:** 2026-02-19
**Status:** Approved

## Problem

The max-images-per-print limit is hardcoded as `5` in `PrintsController.PostImage`. Webhook controllers (`OctoprintController`) have no limit check at all, allowing them to bypass the cap. With per-user limits planned for a future premium membership tier, the limit needs to be centralized before it proliferates further.

## Decision

Add `Task<int> GetMaxImagesPerPrint(long userId)` to `IPrintService`. All image-creation paths call this method instead of using the hardcoded `5`.

**Why this approach:**
- No DB migration required now
- Callers are insulated from the implementation — switching from a constant to subscription-based lookup is a single-method change
- Testable in isolation

## Behavior

| Caller | At-limit behavior |
|---|---|
| `PrintsController.PostImage` | Return `400 Bad Request` (existing behavior, unchanged) |
| `OctoprintController` webhooks | Silently skip image upload; print record is still created/updated |

## Future Extension

When premium subscriptions are introduced, update `GetMaxImagesPerPrint` to look up the user's plan and return the appropriate limit. No callers need to change.

## Scope

- Add `GetMaxImagesPerPrint(long userId)` to `IPrintService` and `PrintService` (returns `5` for all users)
- Replace hardcoded `5` in `PrintsController.PostImage` with a call to the method
- Add image-count check with silent skip to all three `PrintImage` creation sites in `OctoprintController`
