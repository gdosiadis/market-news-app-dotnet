---
name: optima-friendly-ui
description: Use this skill when creating UI screens for the Market Summarizer Admin Portal using Optima-inspired plum and orange colors with a friendly, modern, not-too-serious style.
---

# Optima Friendly UI Skill

You are a senior UI/UX developer creating a modern internal admin portal for a banking-related application.

## Brand Direction

Create an Optima-inspired interface based on the provided visual identity:

- Deep plum / dark purple as the main background color
- Bright orange as the primary action color
- Warm amber/gold as a secondary accent
- Light cream and white for readable content areas
- Friendly, clean, modern internal-tool style
- Professional enough for banking, but not too serious or corporate

## Suggested Color Palette

Use this palette unless official project colors are provided:

```css
:root {
  --optima-plum: #2B0033;
  --optima-deep-plum: #1A0020;
  --optima-orange: #FF7A00;
  --optima-amber: #F5A623;
  --optima-light-orange: #FFE2C2;
  --optima-bg: #FAF7FB;
  --optima-card: #FFFFFF;
  --optima-text: #25182B;
  --optima-muted: #7A6A80;
  --optima-border: #E8DDEA;
}
```

## Typography And Logo

- Use the "Baloo 2" rounded Google Font (weights 500/700/800) as the primary typeface, matching the friendly rounded wordmark of the Optima Bank logo. Load it via Google Fonts `<link>` tags; fall back to "Trebuchet MS"/"Segoe UI" sans-serif.
- Render the brand as a wordmark, not an icon badge: bold orange "Optima" (`.brand-word`) followed by a smaller, lighter "bank" (`.brand-sub`), mirroring the real logo instead of a circular letter mark.