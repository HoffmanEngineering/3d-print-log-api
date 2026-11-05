# Image backfill runbook

`PrintLogApi.Tools image-backfill` re-processes legacy print and project images to the canonical
format with thumbnails and social images, the shape every upload has: a re-encoded original
(JPEG, or PNG when the image has transparent pixels), a 320px WebP thumbnail (#283), a JPEG
social image of about 1200px on the long edge for link previews (#281, drawn on white when the
image is transparent), and the decoded content type, width and height on the image row.

> **Transparent images.** Until the fix that added this note, every original was re-encoded as
> JPEG, which turned a transparent background (slicer-plugin and printlog-bridge renders) solid
> black. The production run of 2026-11-05 was stopped after converting about 8,250 rows. Before
> running the backfill again, repair those rows with `--repair-transparency`: see
> [Repairing transparent images](#repairing-transparent-images).

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
   before. An image with any pixel that is not fully opaque is re-encoded as PNG
   (`ContentType` `image/png`); every other image as JPEG. An alpha channel alone does not count:
   an RGBA PNG whose pixels are all opaque becomes a JPEG.
3. Copies the untouched original to a private container, `printimages-archive` or
   `projectimages-archive`, under its existing name, with blob metadata naming its row
   (`imagekind`, `imageid`, `fileid`). The name's extension is the client's, so it does not
   always match the format (a JPEG uploaded as `photo.png` is archived as `<id>.png`).
4. Uploads the new original (`<new id>.jpg`, or `<new id>.png` when transparent), thumbnail
   (`<new id>.webp`, which keeps the transparency) and social image (`<new id>.jpg`, on white
   when the image is transparent).
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
  `/api/Projects/<projectId>/images/<imageId>`) returns `200` with `Content-Type: image/jpeg`, or
  `image/png` for a row the log shows as transparent (`image/png -> image/png`), whose background
  is still transparent.
- **In the database**, nothing is left except the reported undecodable and missing rows:

  ```sql
  SELECT COUNT(*) FROM PrintImages   WHERE ContentType IS NULL OR ThumbnailFileId IS NULL OR SocialFileId IS NULL;
  SELECT COUNT(*) FROM ProjectImages WHERE ContentType IS NULL OR ThumbnailFileId IS NULL OR SocialFileId IS NULL;
  ```

- **For one row**, its `Files.Path` ends in `.jpg` (`.png` when transparent), that blob exists in
  the live container, and the old name from the log exists in the archive container:

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

## Repairing transparent images

Rows converted before the backfill kept transparency have a JPEG original with a black
background where the upload was transparent, and a social image with the same black. Their
thumbnails are fine (WebP kept the alpha). `--repair-transparency` fixes them from the archive:

- For each archive copy it can tie to exactly one row, it decodes the copy. If any pixel is
  transparent, it writes a new PNG original (`<new id>.png`) and a new social image on white, and
  in one save points the `Files` row at the PNG, sets `ContentType` to `image/png`, and replaces
  the social `Files` row (its id changes, so `/seo`'s `?v=` changes and preview services fetch it
  again). Only after that save commits does it delete the black JPEG original and the old social
  blob. The thumbnail and the archive copy are not touched.
- A row whose archived original is opaque is **left as it is**: its JPEG is what the fixed
  backfill would write too. It is counted under **Opaque**.
- A failure before the save deletes what that row wrote and leaves the row unchanged
  (**Failed**, retried by the next run). A row already `image/png` is skipped, so re-running is
  always safe. Opaque rows are downloaded again on each run (egress only, nothing is written).

### How a row is tied to its archive copy

The database does not record the archive name: the backfill pointed the existing `Files` row at
the new blob and kept no old name. The repair lists the archive containers and ties each copy to a
row by any of these, and only when exactly one row claims a copy and that row has no other copy:

1. **Blob metadata** (`imagekind`, `imageid`), which the backfill writes from this fix on. The
   2026-11-05 run wrote none.
2. **The run's log**, passed with `--from-log <file>` (repeatable): each
   `print-image <id>: ..., original archived as <container>/<name>` line. Other lines are ignored.
3. **The name.** Legacy print uploads were stored as `<File id><client extension>`, and the
   backfill keeps the `Files` row's id, so a copy in `printimages-archive` whose name is a `Files`
   id belongs to the print image with that original. This is what finds the rows converted after
   the log stopped. Project images were stored under random names and have no such link; the
   2026-11-05 run had not reached them (projects come after all print images).

Nothing is matched by time or by guess. Before writing, the repair also checks that the row is
still the backfill's `image/jpeg` and that the archived image decodes to the row's `Width` and
`Height`; a copy of another size is reported under **Skipped**, not written.

The summary lists what needs a person:

- **Unmatched archive copies**: no log line, metadata or `Files` id ties them to a row (a print
  image deleted since it was converted, or a legacy blob with an older naming scheme). With the
  archive's `LastModified` time. To repair one by hand once you know its row, add a line in the
  log format to a file and pass it with `--from-log`:

  ```text
  print-image 12345: manual, original archived as printimages-archive/<name>
  ```

- **Conflicts**: a copy claimed by two rows, or a row with two copies. Nothing is written for them.
- **Not converted**: the row has an archive copy but no content type (the run was stopped between
  archiving and saving it). The backfill picks it up, now keeping transparency.

### Running it

Run it from a checkout of `main` that contains the fix, with the setup above. `dotnet run` runs
in the project folder, so pass log files as absolute paths. First a dry run over everything, which
downloads every archive copy and writes nothing:

```bash
dotnet run --project PrintLogApi.Tools -- image-backfill --repair-transparency \
  --from-log "$PWD/image-backfill-full.log" 2>&1 | tee repair-dry-run.log
```

Pass the trial log too (`--from-log "$PWD/image-backfill-trial.log"`) if the trial converted rows.
Check **Matched**, **Would repair**, **Opaque**, and every listed unmatched copy and conflict.
Then a trial, and look at the logged prints in the site (the model on a transparent page
background, not on black; the link preview on white):

```bash
dotnet run --project PrintLogApi.Tools -- image-backfill --repair-transparency --apply --limit 10 \
  --from-log "$PWD/image-backfill-full.log" 2>&1 | tee repair-trial.log
```

Then the rest:

```bash
dotnet run --project PrintLogApi.Tools -- image-backfill --repair-transparency --apply \
  --from-log "$PWD/image-backfill-full.log" 2>&1 | tee repair-full.log
```

The exit code is 1 when any row failed; re-run until **Failed** is empty. Then restart the normal
backfill (sections 2 and 3); it converts the remaining legacy rows with transparency kept.

Uploads processed by the API between #283 (v1.20.0) and this fix have no archive copy (they were
re-encoded on upload), so a transparent upload from that window keeps its black JPEG original and
social image; only its thumbnail is transparent. This repair cannot reach them.

## Deleting the archive containers

The archive containers are private and nothing in the API reads them. They roughly double the
storage of the legacy images until they are deleted. Delete them once:

- the transparency repair has run with **Failed** empty, and its unmatched copies and conflicts
  have been dealt with (the repair needs the archive),
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
