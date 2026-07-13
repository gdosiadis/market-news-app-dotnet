---
description: "Refactor a single method for clarity/maintainability without changing its external behavior."
agent: "agent"
argument-hint: "Method or file to refactor..."
---
Refactor the method I specify (or the one currently selected/open) to improve
readability and maintainability, **without changing its observable behavior**.

- Preserve the existing method signature (name, parameters, return type) unless I
  explicitly ask to change it.
- Extract helper methods where it clarifies intent, following this repo's existing
  naming/structure conventions (see `MarketNewsApp/Services/Scraper.cs` for the house
  style of small, single-purpose private async helpers with a `// why` comment above
  non-obvious logic).
- Do not introduce new dependencies or change async/sync behavior.
- After refactoring, rebuild (`dotnet build`) to confirm zero errors, and if the method
  is part of the scraping/screenshot pipeline, re-run `dotnet run -- --test` and verify
  the relevant output is unchanged (per `tests.instructions.md`).
