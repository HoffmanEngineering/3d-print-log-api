# Public images and SEO metadata

## Public images and SEO metadata (#281)

Print and project images are served by the API (`GET /api/Prints/{printId}/image/{imageId}`,
`/api/Projects/{id}/images/{imageId}`), each with a `/thumbnail` variant (320px WebP, falling back
to the original) and, for the default image of a public print or project, `/api/Prints/{id}/social-image`
and `/api/Projects/{id}/social-image` (JPEG, about 1200px on the long edge, for link previews).
`GET /api/{Prints,Projects,Users}/{id}/seo` (`SeoController`) returns the page metadata. **Two
readers depend on `/seo`: the Cloudflare Worker that writes meta tags, JSON-LD and a summary into
the app shell for crawlers, and the web app's `MetaTagService`.** Change its shape additively only.

- **Cache rules, and why they differ.** An image anyone can read (print or project not private)
  is `Cache-Control: public, max-age=3600`, so Cloudflare can serve it. The hour is the bound on
  how long a print made private stays in shared caches; do not raise it without accepting a
  longer window. A private print's or project's image is `private`. Every image has a strong
  `ETag` from its blob path, not the File id (the backfill points an existing File row at a new
  blob), and a matching `If-None-Match` is a 304 before storage is touched. `/seo` is
  `public, max-age=300`. Nothing here is in HybridCache: the keys are anonymous and enumerable,
  and a crawler would churn the shared 8 MiB budget the per-user summaries rely on. The public
  print id and sitemap lists are, as one fixed key a crawler cannot multiply (see `docs/agents/caching.md`).
- **Non-disclosing 404s.** `/seo` and `social-image` answer 404 for anything not Public: private,
  unlisted, missing, or (social image) no image, and the same for the owner. Unlisted is left
  out on purpose, the same line the sitemap and `/public` lists draw. `SocialImageLookup` is the one
  query for "which image previews this page", used by both the endpoint and `/seo`.
- **`?v=` cache busting.** `/seo`'s image URL is absolute (`Seo:ApiBaseUrl`, else the request's
  origin; the local and test settings blank it) and ends in `?v=` plus the served File's id.
  Preview services cache by URL, so choosing another default image, or the backfill adding a
  social file, must change the URL; the endpoint ignores the parameter.
- **Streaming.** `IBlobStorageService.OpenReadAsync` replaces the buffered download for these
  endpoints. A plain GET is one forward-only download (no existence check: a 404 from it is the
  check); only a `Range` request opens the seekable stream, which costs a properties read first.
  The in-memory test double mirrors that split (forward-only unless asked to seek); the Azure
  calls themselves were checked against Azurite by hand.
- **`/seo` content.** Only what the public page shows: no email, cost, purchase or material notes,
  connector ids. Titles end in ` | 3D Print Log`; descriptions are the entity's own text with
  whitespace collapsed, else a sentence from its fields, cut to 155 characters at a word.
  Canonical URLs come from `Email:WebBaseUrl`. Rate-limited by the anonymous "api" budget.
- **Social derivative.** Made on every print and project upload and printer snapshot
  (`ImageProcessingOptions.PrintUploads`), never for printer or material images. Its size is on
  the row (`SocialWidth`/`SocialHeight`) for `og:image:width/height`. Older rows get one from
  the image backfill (`docs/image-backfill.md`). A transparent image's social JPEG is drawn on
  white: link previews need a solid background.
- **Originals are JPEG or PNG, never assume JPEG.** `ImageProcessingService` re-encodes an image
  with any pixel that is not fully opaque (it scans the alpha, not the color type) as PNG and
  everything else as JPEG; `ProcessedImage.ContentType` says which, it goes on the row, and blob
  extensions follow it (`ImageContentTypes.ExtensionFor`). Serving reads the row's type. Until
  this was fixed every original was JPEG and transparent renders (slicer plugins, the bridge)
  came out on black; uploads from that window cannot be recovered, and backfilled rows are
  repaired from the archive by `image-backfill --repair-transparency`.
- **Thumbnail URLs in DTOs** (`thumbnailUrl`, `defaultPrintImageThumbnailUrl`,
  `defaultImageThumbnailUrl`, `defaultProjectImageThumbnailUrl`) are paths relative to the API
  base URL (`ImageUrls`), which the web app prefixes as it does for image ids.
