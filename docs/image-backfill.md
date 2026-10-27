# Image backfill runbook

`PrintLogApi.Tools image-backfill` re-processes legacy print and project images to the canonical
format with thumbnails and social images, the shape every upload has: a re-encoded JPEG original,
a 320px WebP thumbnail (#283), a JPEG social image of about 1200px on the long edge for link
previews (#281), and the decoded content type, width and height on the image row.

Run it once, from a workstation, after the release that contains #281's social image (and its
migration) is deployed. The API keeps serving while it runs. A run before that release does not
add social images; running it again after the release adds them to every row.

## What it does

It selects every `PrintImages` and `ProjectImages` row with no `ContentType`, no
`ThumbnailFileId` or no `SocialFileId` (`ImageBackfillSelection`). For each row, in this order:

1. Downloads the original blob. The file row's `Path` is either `printimages/<name>` or, for older
   project images, a bare `<name>`; only the name is used.
2. Decodes and re-encodes it with the same processor uploads use. GIF and BMP, which uploads
   accepted before #283, are converted too (the first frame of an animated GIF). An image that
   cannot be decoded is **left as it is** and listed in the report; the API keeps serving it as
   before.
3. Copies the untouched original to a private container, `printimages-archive` or
   `projectimages-archive`, under its existing name.
4. Uploads the new original (`<new id>.jpg`), thumbnail (`<new id>.webp`) and social image
   (`<new id>.jpg`).
5. In one save: points the existing `Files` row at the new original (`Path`, `Size`; its id is
   unchanged), adds the derivatives' `Files` rows, and sets `ContentType`, `ThumbnailFileId`,
   `SocialFileId`, `Width`, `Height`, `SocialWidth` and `SocialHeight` on the image row.
6. Only after that save commits, deletes the old original.

If anything fails before the save commits, the blobs that row wrote are deleted (the archive copy
stays, and the next run overwrites it), the row is unchanged, and it is listed under **Failed**.
A re-run picks it up again. Rows already done are not selected, so re-running is always safe.

A row that already has a content type only gets the derivatives it lacks: its original is not
re-encoded or archived. That covers uploads made after #283 but before the social image existed
(they get only the social image), and processed uploads whose thumbnail failed.

## Setup

The tool reads two environment variables, under the names the API uses. Copy the values from the
App Service configuration (Azure portal > App Service > Settings > Environment variables). Read
them in with `read -rs` so they stay out of your shell history, and never paste them into an
issue or log:

```bash
cd /d/Development/github/3d-print-log-api
read -rs -p "SQL connection string: " ConnectionString__PrintLogDb; echo
read -rs -p "Storage connection string: " AZURE_STORAGE_CONNECTION_STRING; echo
export ConnectionString__PrintLogDb AZURE_STORAGE_CONNECTION_STRING
```

The SQL login must be able to update `Files`, `PrintImages` and `ProjectImages`. Your IP must be
allowed through the Azure SQL firewall.

Each run prints the database name and storage account it is about to use. Check them before
anything else.

## 1. Dry run

A dry run is the default. It downloads and processes every selected image but writes nothing,
then prints what it would do: how many it would process, how many are GIF or BMP, which ids it
would skip as undecodable or missing, and the bytes before and after.

```bash
dotnet run --project PrintLogApi.Tools -- image-backfill 2>&1 | tee image-backfill-dry-run.log
```

It reads every legacy image, so on a large library it takes a while and costs egress. Add
`--limit 200` for a quicker sample.

## 2. Trial: ten images

```bash
dotnet run --project PrintLogApi.Tools -- image-backfill --apply --limit 10 2>&1 | tee image-backfill-trial.log
```

The log lists each row it changed (`print-image 123: image/png -> image/jpeg + thumbnail + social, ...`).
Check a few of them as below before the full run. One print or project can be targeted with
`--print-id <id>` or `--project-id <guid>`.

## 3. Full run

```bash
dotnet run --project PrintLogApi.Tools -- image-backfill --apply 2>&1 | tee image-backfill-full.log
```

It reads `--batch-size` rows per query (default 100) and handles one row at a time. Ctrl+C stops
it; the row in progress is cleaned up like a failed one, and the next run carries on from the rest.
The exit code is 1 when any row failed. Re-run until **Failed** is empty or only lists rows that
fail every time, and look at those individually.

## Verifying

After the trial and after the full run:

- **In the site**, open a few of the logged prints and projects: each image shows, the right way
  up, and opening it shows the full image.
- **Through the API**, `GET /api/Prints/<printId>/image/<imageId>` (or
  `/api/Projects/<projectId>/images/<imageId>`) returns `200` with `Content-Type: image/jpeg`.
- **In the database**, nothing is left except the reported undecodable and missing rows:

  ```sql
  SELECT COUNT(*) FROM PrintImages   WHERE ContentType IS NULL OR ThumbnailFileId IS NULL OR SocialFileId IS NULL;
  SELECT COUNT(*) FROM ProjectImages WHERE ContentType IS NULL OR ThumbnailFileId IS NULL OR SocialFileId IS NULL;
  ```

- **For one row**, its `Files.Path` ends in `.jpg`, that blob exists in the live container, and the
  old name from the log exists in the archive container:

  ```bash
  az storage blob list --container-name printimages-archive --connection-string "$AZURE_STORAGE_CONNECTION_STRING" --num-results 5 --query "[].name" -o tsv
  ```

### Putting one image back

If a converted image is wrong, its original is in the archive under its old name, which the log
line for that row gives (`original archived as printimages-archive/<name>`). Copy it back to
`printimages` (or `projectimages`) under that name, set the `Files` row's `Path` back
(`printimages/<name>`; a bare `<name>` works for project images too) and its `Size`,
and set the image row's `ContentType`, `Width`, `Height`, `ThumbnailFileId`, `SocialFileId`,
`SocialWidth` and `SocialHeight` to `NULL`. Delete the thumbnail's and social image's `Files` rows
and blobs. The API then serves it the legacy way again, and the next run
would select it again, so fix the cause before re-running.

## Deleting the archive containers

The archive containers are private and nothing in the API reads them. They roughly double the
storage of the legacy images until they are deleted. Delete them once:

- the full run has finished and **Failed** is empty (or every remaining row has been dealt with),
- the checks above pass, and
- the site has run on the converted images for about two weeks without a report of a broken or
  rotated image.

```bash
az storage container delete --name printimages-archive --connection-string "$AZURE_STORAGE_CONNECTION_STRING"
az storage container delete --name projectimages-archive --connection-string "$AZURE_STORAGE_CONNECTION_STRING"
```

This cannot be undone. Afterwards, `unset ConnectionString__PrintLogDb AZURE_STORAGE_CONNECTION_STRING`
(or close the shell).
