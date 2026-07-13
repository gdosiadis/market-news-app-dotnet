# Market News AI (.NET) — Project Guidelines

## Architecture

.NET 8 console app (`MarketNewsApp/`) that runs a daily pipeline:
1. **Scrape** (`Services/Scraper.cs`) — Playwright scrapes 7 financial sites in parallel
   (`Program.cs` → `ScrapeAllAsync`), extracting article text **and** capturing
   chart/table **screenshots directly from the live pages** (never AI-generated/rendered).
2. **Clean** — dedupe/normalize scraped text per source.
3. **Cache** (`Services/ScrapeCache.cs`, `Services/SummaryCache.cs`) — same-day results
   (scrape + per-source AI summary + final synthesis) are cached by content hash so a
   second run the same day reuses unchanged work instead of re-scraping/re-summarizing
   from scratch. A failed source invalidates only that day's cache, not the whole run.
4. **Summarize** (`Services/AiSummarizer.cs`) — per-source Greek AI summaries, then a
   synthesized overview. Provider is GitHub Copilot SDK by default, with Azure OpenAI /
   Groq as explicit alternatives via `AI_PROVIDER` env var.
5. **Email** (`Services/EmailSender.cs`) — HTML report (Scriban template in
   `Templates/email_template.html`) sent via Gmail SMTP (MailKit). Charts/tables are
   embedded as base64 screenshots — no PowerPoint/PPTX output.

See `MarketNewsApp/README.md` for the full source list, setup, and cache behavior.

## Build and Test

```bash
cd MarketNewsApp
dotnet build
dotnet run -- --test   # dry run: saves report.html, sends no email
dotnet run -- --now    # runs once and sends the email immediately
```

If `dotnet build`/`dotnet run` fails with `MSB3027`/`MSB3021` (locked `apphost.exe`/`.exe`),
a previous `dotnet run` process is still holding the file — find it with
`Get-Process MarketNewsApp` and `Stop-Process -Id <pid> -Force` before rebuilding.

## Conventions

- **Screenshots are ground truth, not AI-generated.** Any change touching chart/table
  capture in `Scraper.cs` must be verified by actually running `--test` and visually
  inspecting the resulting `report.html` images (extract embedded base64 images and view
  them) — do not assume a selector/heuristic change is correct without a real screenshot.
- **Transient vs. real errors in `AiSummarizer.cs`**: `TransientCopilotErrors` lists
  known flaky failures (proxy/connection hiccups, occasional "organization has been
  disabled") that are retried automatically instead of failing the source immediately.
  Only add new entries here when a failure has been observed to be non-persistent
  (i.e., the same source later succeeds without any config/account change).
- **Don't leave test artifacts committed**: `report.html`, `cache/*.json`,
  `debug_shots/`, and similar generated files are git-ignored — clean them up after
  local testing rather than committing them.
- Comments in this codebase explain *why* (root cause of a bug, rationale for a
  heuristic) rather than *what* the code does — keep that style when adding logic that
  isn't self-evident.
