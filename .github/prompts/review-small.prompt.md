---
description: "Review a small, focused code change (a few files or one method) with high-signal feedback only."
agent: "agent"
---
Review the currently staged/unstaged changes (or the file(s) I mention) in this repo.

- Only surface real issues: bugs, logic errors, security problems, or genuine
  regressions — do not comment on style, formatting, or naming preferences.
- Pay special attention to: unhandled exceptions in Playwright/async code, nullable
  reference type violations, and any change to `Scraper.cs` screenshot/selector logic
  (which must be verified against a real `--test` run per `tests.instructions.md`, not
  just read).
- If the change looks correct, say so briefly — don't invent issues to fill space.
- Keep the review short and focused on the diff at hand, not a full-file audit.
