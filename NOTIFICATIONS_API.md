# Notifications API Documentation

This document describes the Notifications API for front-end integration.

## Overview

The Notifications system allows users to receive notifications for various events:
- **Comment** - When someone comments on the user's print
- **PrintCompleted** - When a print completes successfully (via Moonraker/OctoPrint webhook)
- **PrintFailed** - When a print fails (via Moonraker/OctoPrint webhook)
- **Achievement** - Reserved for future achievement system
- **SystemAnnouncement** - Reserved for system-wide announcements

All endpoints require authentication via JWT Bearer token or API Key.

---

## Data Types

### NotificationType Enum

| Value | Name | Description |
|-------|------|-------------|
| 1 | Comment | Someone commented on your print |
| 2 | PrintCompleted | Your print completed successfully |
| 3 | PrintFailed | Your print failed |
| 4 | Achievement | Achievement unlocked (future) |
| 5 | SystemAnnouncement | System announcement (future) |

### NotificationSummaryDto

Used in list views.

```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "type": 1,
  "title": "New comment on your print",
  "message": "John Doe commented on \"Benchy\"",
  "isRead": false,
  "createdDate": "2026-01-25T18:07:49Z",
  "actionUrl": "/prints/456#comment-789",
  "printId": 456,
  "printTitle": "Benchy",
  "triggeredByUser": {
    "id": 42,
    "profilePicture": "https://example.com/avatar.jpg",
    "coverPicture": null,
    "displayName": "John Doe"
  }
}
```

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| id | Guid | No | Unique notification ID |
| type | NotificationType | No | Type of notification |
| title | string | No | Short title (max 200 chars) |
| message | string | Yes | Detailed message (max 1000 chars) |
| isRead | boolean | No | Whether the notification has been read |
| createdDate | DateTime | No | When the notification was created (UTC) |
| actionUrl | string | Yes | Deep link URL for navigation (e.g., `/prints/123`) |
| printId | long | Yes | Related print ID, if applicable |
| printTitle | string | Yes | Title of the related print |
| triggeredByUser | UserSummaryDto | Yes | User who triggered the notification |

### NotificationDetailDto

Used for single notification view.

```json
{
  "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "type": 1,
  "title": "New comment on your print",
  "message": "John Doe commented on \"Benchy\"",
  "isRead": true,
  "createdDate": "2026-01-25T18:07:49Z",
  "readDate": "2026-01-25T19:00:00Z",
  "actionUrl": "/prints/456#comment-789",
  "printId": 456,
  "printTitle": "Benchy",
  "commentId": 789,
  "triggeredByUser": {
    "id": 42,
    "profilePicture": "https://example.com/avatar.jpg",
    "coverPicture": null,
    "displayName": "John Doe"
  },
  "metadata": null
}
```

| Field | Type | Nullable | Description |
|-------|------|----------|-------------|
| id | Guid | No | Unique notification ID |
| type | NotificationType | No | Type of notification |
| title | string | No | Short title (max 200 chars) |
| message | string | Yes | Detailed message (max 1000 chars) |
| isRead | boolean | No | Whether the notification has been read |
| createdDate | DateTime | No | When the notification was created (UTC) |
| readDate | DateTime | Yes | When the notification was read (UTC) |
| actionUrl | string | Yes | Deep link URL for navigation |
| printId | long | Yes | Related print ID |
| printTitle | string | Yes | Title of the related print |
| commentId | long | Yes | Related comment ID, if applicable |
| triggeredByUser | UserSummaryDto | Yes | User who triggered the notification |
| metadata | string | Yes | JSON string for extensibility data |

### NotificationUnreadCountDto

```json
{
  "unreadCount": 5
}
```

### MarkNotificationsReadDto

Request body for marking multiple notifications as read.

```json
{
  "notificationIds": [
    "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
    "b2c3d4e5-f6a7-8901-bcde-f12345678901"
  ]
}
```

---

## API Endpoints

### GET /api/notifications

Get a paged list of notifications for the current user.

**Query Parameters:**

| Parameter | Type | Required | Default | Description |
|-----------|------|----------|---------|-------------|
| pageNumber | int | No | 1 | Page number (1-based) |
| pageSize | int | No | 10 | Items per page |
| unreadOnly | bool | No | null | If true, only return unread notifications |

**Response:** `200 OK`

```json
{
  "paging": {
    "totalCount": 42,
    "currentPage": 1,
    "pageSize": 10,
    "totalPages": 5,
    "hasPrevious": false,
    "hasNext": true
  },
  "items": [
    {
      "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "type": 1,
      "title": "New comment on your print",
      "message": "John Doe commented on \"Benchy\"",
      "isRead": false,
      "createdDate": "2026-01-25T18:07:49Z",
      "actionUrl": "/prints/456#comment-789",
      "printId": 456,
      "printTitle": "Benchy",
      "triggeredByUser": {
        "id": 42,
        "profilePicture": "https://example.com/avatar.jpg",
        "coverPicture": null,
        "displayName": "John Doe"
      }
    }
  ]
}
```

**Error Responses:**
- `401 Unauthorized` - User is not authenticated

---

### GET /api/notifications/unread-count

Get the count of unread notifications for badge display.

**Response:** `200 OK`

```json
{
  "unreadCount": 5
}
```

**Error Responses:**
- `401 Unauthorized` - User is not authenticated

---

### GET /api/notifications/{id}

Get a single notification by ID.

**Path Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| id | Guid | Notification ID |

**Response:** `200 OK`

```json
{
  "id": 123,
  "type": 1,
  "title": "New comment on your print",
  "message": "John Doe commented on \"Benchy\"",
  "isRead": true,
  "createdDate": "2026-01-25T18:07:49Z",
  "readDate": "2026-01-25T19:00:00Z",
  "actionUrl": "/prints/456#comment-789",
  "printId": 456,
  "printTitle": "Benchy",
  "commentId": 789,
  "triggeredByUser": {
    "id": 42,
    "profilePicture": "https://example.com/avatar.jpg",
    "coverPicture": null,
    "displayName": "John Doe"
  },
  "metadata": null
}
```

**Error Responses:**
- `401 Unauthorized` - User is not authenticated
- `404 Not Found` - Notification not found or doesn't belong to user

---

### PUT /api/notifications/{id}/read

Mark a single notification as read.

**Path Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| id | Guid | Notification ID |

**Response:** `204 No Content`

**Error Responses:**
- `401 Unauthorized` - User is not authenticated
- `404 Not Found` - Notification not found or doesn't belong to user

---

### PUT /api/notifications/read-all

Mark all notifications as read for the current user.

**Response:** `204 No Content`

**Error Responses:**
- `401 Unauthorized` - User is not authenticated

---

### PUT /api/notifications/read

Mark multiple notifications as read.

**Request Body:**

```json
{
  "notificationIds": [1, 2, 3, 4, 5]
}
```

**Response:** `204 No Content`

**Error Responses:**
- `400 Bad Request` - NotificationIds is required and must not be empty
- `401 Unauthorized` - User is not authenticated

---

### DELETE /api/notifications/{id}

Delete a single notification.

**Path Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| id | Guid | Notification ID |

**Response:** `204 No Content`

**Error Responses:**
- `401 Unauthorized` - User is not authenticated
- `404 Not Found` - Notification not found or doesn't belong to user

---

### DELETE /api/notifications

Delete all notifications for the current user.

**Response:** `204 No Content`

**Error Responses:**
- `401 Unauthorized` - User is not authenticated

---

## Notification Generation

Notifications are automatically created by the backend in the following scenarios:

### Comment Notifications

When a user comments on a print, notifications are sent to:
1. **The print owner** (if the commenter is not the owner)
2. **All previous commenters** on that print (excluding the current commenter and print owner)

This creates a "thread-style" notification system where everyone participating in a discussion is notified of new comments.

- **Type:** `Comment` (1)
- **ActionUrl:** `/prints/{printId}#comment-{commentId}`

**For print owners:**
- **Title:** "New comment on your print"
- **Message:** "{commenterName} commented on \"{printTitle}\""

**For previous commenters:**
- **Title:** "New reply on a print you commented on"
- **Message:** "{commenterName} also commented on \"{printTitle}\""

> Note: Users do not receive notifications for their own comments. Previous commenters who are also the print owner only receive one notification (as the owner).

### Print Completed Notifications

When a print completes via Moonraker or OctoPrint webhook.

- **Type:** `PrintCompleted` (2)
- **Title:** "Print completed"
- **Message:** "Your print \"{printTitle}\" has completed successfully"
- **ActionUrl:** `/prints/{printId}`

### Print Failed Notifications

When a print fails via Moonraker or OctoPrint webhook.

- **Type:** `PrintFailed` (3)
- **Title:** "Print failed"
- **Message:** "Your print \"{printTitle}\" has failed"
- **ActionUrl:** `/prints/{printId}`

### API Key Created Notifications

When a new API key is created for the user's account.

- **Type:** `SystemAnnouncement` (5)
- **Title:** "New API key created"
- **Message:** "A new API key \"{keyDescription}\" was created for your account"
- **ActionUrl:** `/api-keys`

### API Key Deleted Notifications

When an API key is deleted from the user's account.

- **Type:** `SystemAnnouncement` (5)
- **Title:** "API key deleted"
- **Message:** "The API key \"{keyDescription}\" was deleted from your account"
- **ActionUrl:** `/api-keys`

---

## Frontend Implementation Notes

### Polling vs Real-time

Currently, the API supports polling-based notification retrieval. For real-time updates, implement polling on the `/api/notifications/unread-count` endpoint at a reasonable interval (e.g., 30-60 seconds).

### Badge Display

Use the `/api/notifications/unread-count` endpoint to display notification badges. This is a lightweight endpoint optimized for frequent polling.

### Navigation

The `actionUrl` field contains a relative URL that can be used for client-side navigation. Examples:
- `/prints/123` - Navigate to a print
- `/prints/123#comment-456` - Navigate to a print and scroll to a specific comment

### Optimistic UI Updates

When marking notifications as read, consider implementing optimistic updates:
1. Update the UI immediately
2. Send the API request in the background
3. Revert if the request fails

### Notification List UX Recommendations

1. Show unread notifications with visual distinction (bold, background color, etc.)
2. Allow users to mark individual notifications as read by clicking/tapping
3. Provide a "Mark all as read" action
4. Support pull-to-refresh on mobile
5. Implement infinite scroll or pagination for the notification list

---

## Future Extensibility

The `metadata` field (JSON string) is reserved for future notification types that may require additional data:

### Achievement Notifications (Future)

```json
{
  "metadata": "{\"achievementId\": \"first-print\", \"name\": \"First Print\", \"icon\": \"trophy\"}"
}
```

### System Announcements (Future)

```json
{
  "metadata": "{\"announcementId\": \"maintenance-2026-02\", \"priority\": \"high\"}"
}
```
