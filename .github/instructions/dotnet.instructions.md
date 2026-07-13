---
description: "Use when writing, modifying, or reviewing C#/.NET code in this repo — covers async Playwright/HTTP patterns, nullable reference types, and project structure conventions."
applyTo: "MarketNewsApp/**/*.cs"
---
# .NET / C# Guidelines

- Target framework is **.NET 8** with nullable reference types enabled — respect `?`
  annotations, don't silently introduce nulls without updating signatures.
- This is a **top-level statements** program (`Program.cs`); keep new orchestration logic
  there and business logic in `Services/`.
- All I/O (scraping, AI calls, email, file cache) is `async`/`await` end-to-end — never
  block with `.Result`/`.Wait()` on a `Task`.
- Playwright calls (`Services/Scraper.cs`) must be defensive: wrap per-element/per-site
  operations in `try/catch` so one selector or one site failing doesn't abort the whole
  scrape — this mirrors the existing pattern of returning partial results with a status
  per source rather than throwing.
- When adding a new scraped site, follow the existing `SiteConfig` pattern (selectors,
  optional `FollowFirstLinkSelector` for list-style pages) instead of one-off scraping
  logic in `Program.cs`.
- Prefer extending the existing cache-hash mechanism (`ScrapeCache.cs`/`SummaryCache.cs`)
  over adding a second, parallel caching scheme.
