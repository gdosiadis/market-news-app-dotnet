# 📊 Market News AI (.NET) — Εβδομαδιαία Ενημέρωση Αγορών

Αυτόματη εφαρμογή σε .NET 8 που κάθε μέρα:
1. **Αντλεί** ειδήσεις από 7 κορυφαίους χρηματοοικονομικούς οίκους με Playwright
2. **Συνοψίζει** στα Ελληνικά μέσω Groq AI ή Azure OpenAI (Azure AI Foundry)
3. **Δημιουργεί** γραφήματα (δείκτες, ομόλογα, συνάλλαγμα, μακρο) με ScottPlot
4. **Αποστέλλει** HTML email μέσω Gmail (MailKit)

## Πηγές
| Οίκος | URL |
|---|---|
| Bloomberg | bloomberg.com/markets |
| BlackRock | blackrock.com/...weekly-commentary |
| T. Rowe Price | troweprice.com/...global-markets-weekly-update |
| BNP Paribas AM | viewpoint.bnpparibas-am.com |
| Edward Jones | edwardjones.com/...stock-market-weekly-update |
| JPMorgan AM | am.jpmorgan.com/...weekly-market-recap |
| Citi | marketinsights.citi.com/...Weekly-Market-Update |

## Εγκατάσταση

```bash
# 1. Clone / download
cd market-news-app-dotnet/MarketNewsApp

# 2. Restore packages
dotnet restore

# 3. Install Playwright browsers
pwsh bin/Debug/net8.0/playwright.ps1 install chromium
# OR after first build:
dotnet build
pwsh bin/Debug/net8.0/playwright.ps1 install chromium

# 4. Ρύθμιση credentials
cp .env.example .env
# Επεξεργαστείτε το .env με:
#   - Επιλογή A (Groq):
#       GROQ_API_KEY  → https://console.groq.com
#   - Επιλογή B (Azure OpenAI / Foundry):
#       AZURE_OPENAI_ENDPOINT
#       AZURE_OPENAI_API_KEY
#       AZURE_OPENAI_DEPLOYMENT
#       (προαιρετικά) AZURE_OPENAI_API_VERSION
#   - GMAIL_USER    → το Gmail σας
#   - GMAIL_APP_PASSWORD → Google Account > Security > App Passwords
#   - EMAIL_TO      → παραλήπτες (κόμμα για πολλούς)
#   - SEND_TIME     → π.χ. 07:00
```

## Χρήση

```bash
# Δοκιμαστική εκτέλεση — αποθηκεύει report.html (δεν στέλνει email)
dotnet run -- --test

# Εκτέλεση μία φορά και αποστολή email τώρα
dotnet run -- --now

# Scheduler — στέλνει κάθε μέρα στην ώρα SEND_TIME
dotnet run
```

## Δομή Αρχείων

```
MarketNewsApp/
├── Program.cs               # Orchestrator + Scheduler (top-level statements)
├── Services/
│   ├── Scraper.cs           # Playwright async scraper
│   ├── AiSummarizer.cs     # Groq AI (ελληνική σύνοψη + εξαγωγή δεδομένων)
│   ├── ChartGenerator.cs   # ScottPlot γραφήματα
│   └── EmailSender.cs      # Gmail SMTP αποστολή (MailKit)
├── Models/
│   └── MarketData.cs       # Data models
├── Templates/
│   └── email_template.html # HTML email template (Scriban)
├── MarketNewsApp.csproj
└── .env.example
```

## Gmail App Password

1. Ενεργοποιήστε 2-Step Verification στο Google Account
2. Google Account → Security → App Passwords
3. Επιλέξτε "Mail" → "Windows Computer"
4. Αντιγράψτε τον 16-ψήφιο κωδικό στο `.env`

## Tech Stack

- .NET 8
- Microsoft.Playwright (browser automation)
- Groq API (AI summarization via HTTP)
- ScottPlot 5 (chart generation)
- MailKit (SMTP email)
- Scriban (HTML templating)
- DotNetEnv (.env file loading)
