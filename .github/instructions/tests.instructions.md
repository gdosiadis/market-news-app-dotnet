---
description: "Use when adding or changing tests, or when verifying a fix to the scraping/AI/email pipeline actually works end-to-end. Covers the manual --test workflow used in this repo (no automated test project exists yet)."
---
# Testing Guidelines

This repo currently has **no automated test project** — verification is done by running
the real pipeline in dry-run mode and inspecting the output:

```bash
cd MarketNewsApp
dotnet run -- --test
```

This scrapes all 7 sites, runs AI summarization, and saves `report.html` **without**
sending an email.

## Verifying screenshot/scraping changes

1. Run `dotnet run -- --test` (clear `cache/*.json` first if you need a fresh scrape
   instead of a same-day cache hit).
2. Extract the embedded base64 images from `report.html` and view each one — confirm
   charts/tables are clean (no bundled paragraph text, no blank/placeholder captures, no
   tiny decorative icons mistaken for a chart).
3. Spot-check every source, not just the one you changed — a selector/heuristic tweak
   can regress a different site.

## Verifying AI/email changes

- Check the per-step console output (`Step 4/5`, `Step 5/5`) for `✅ Success` vs
  `❌ Error` per source, and confirm the same-day cache (`♻️ reused cached summary`)
  behaves as expected when re-running the same day.
- Never send a real email while testing — always use `--test`, not `--now`.

## Cleanup

Before committing, remove generated artifacts: `report.html`, `cache/*.json`,
`debug_shots/`, and any ad-hoc `screenshot_check*/` folders used during manual
inspection — these are git-ignored and should not be committed.
