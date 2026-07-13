---
description: "Diagnose and fix a runtime error or exception from this app (scrape failure, AI provider error, email send failure, etc.)."
agent: "agent"
argument-hint: "Paste the error message/stack trace..."
---
Diagnose and fix the error I paste below.

- First determine whether it's a **real bug** in our code vs. an **external/transient**
  failure (network hiccup, third-party site layout change, AI provider outage) — check
  `AiSummarizer.cs`'s `TransientCopilotErrors` handling as the existing example of this
  distinction before assuming something needs a code fix.
- If it's a genuine bug: make the smallest correct fix, rebuild (`dotnet build`), and
  verify with `dotnet run -- --test` per `tests.instructions.md` before considering it
  resolved.
- If it's a site-specific scraping issue (e.g. a source's selectors stopped matching, or
  a screenshot captures the wrong element): inspect the live DOM (the `--debug-dom <url>`
  CLI option in `Program.cs` exists exactly for this) before guessing at a selector fix.
- If it's a one-off transient failure with no reproducible root cause: don't add
  speculative retry/fallback logic — explain that conclusion instead of writing
  unnecessary code.

Error:
