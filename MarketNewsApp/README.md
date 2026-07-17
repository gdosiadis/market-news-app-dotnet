# 📊 Market News AI (.NET) — Εβδομαδιαία Ενημέρωση Αγορών

Αυτόματη εφαρμογή σε .NET 8 που κάθε μέρα:
1. **Αντλεί** ειδήσεις από 7 κορυφαίους χρηματοοικονομικούς οίκους με Playwright
2. **Συνοψίζει** στα Ελληνικά μέσω Groq AI ή Azure OpenAI (Azure AI Foundry)
3. **Καταγράφει** screenshots γραφημάτων/πινάκων απευθείας από τις σελίδες-πηγές (χωρίς AI rendering)
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
#   - AI_PROVIDER (προαιρετικό) → επιλέγει τον AI provider:
#       copilot (default) → GitHub Copilot SDK (χρειάζεται `copilot` CLI login, χωρίς API key)
#       groq              → GROQ_API_KEY  → https://console.groq.com
#       azure             → Azure OpenAI / Foundry:
#           AZURE_OPENAI_ENDPOINT
#           AZURE_OPENAI_API_KEY
#           AZURE_OPENAI_DEPLOYMENT
#           (προαιρετικά) AZURE_OPENAI_API_VERSION
#   - COPILOT_MODEL (προαιρετικό) → μοντέλο για τον Copilot provider
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
│   ├── Scraper.cs           # Playwright async scraper (+ chart/table screenshot capture)
│   ├── AiSummarizer.cs     # Groq AI (ελληνική σύνοψη ανά πηγή + συνθετική επισκόπηση)
│   ├── ScrapeCache.cs      # Ημερήσια cache του scraped/cleaned περιεχομένου
│   ├── SummaryCache.cs     # Ημερήσια cache των AI summaries + synthesis
│   └── EmailSender.cs      # Gmail SMTP αποστολή (MailKit)
├── Models/
│   └── Models.cs            # Data models
├── Templates/
│   └── email_template.html # HTML email template (Scriban)
├── cache/                   # (regenerated) daily scrape + summary cache — git-ignored
├── MarketNewsApp.csproj
└── .env.example
```

## Ημερήσιο Cache (αποφυγή επανάληψης)

Όταν τρέξει η εφαρμογή μία φορά μέσα στην ημέρα, αποθηκεύει:
- `cache/{ημερομηνία}.json` — το scraped/cleaned περιεχόμενο ανά πηγή
- `cache/{ημερομηνία}-summary.json` — τα AI summaries ανά πηγή + τη συνθετική επισκόπηση, μαζί με ένα hash του περιεχομένου κάθε πηγής

Σε επόμενη εκτέλεση **την ίδια ημέρα** (π.χ. `--test` ή `--now` ξανά):
- Αν το scrape cache υπάρχει, δεν ξαναγίνεται scraping.
- Για κάθε πηγή, αν το περιεχόμενό της δεν έχει αλλάξει (ίδιο hash), το AI summary **επαναχρησιμοποιείται** αντί να ξαναπαραχθεί — καλείται το AI μόνο για πηγές με νέο/διαφορετικό περιεχόμενο.
- Αν καμία πηγή δεν άλλαξε, επαναχρησιμοποιείται και η τελική συνθετική επισκόπηση, χωρίς νέα κλήση AI.

Αν κάποια πηγή είχε αποτύχει (π.χ. timeout), το cache της ημέρας αγνοείται αυτόματα ώστε να μη «παγώσει» ένα κακό αποτέλεσμα για όλη τη μέρα.

## Screenshots γραφημάτων/πινάκων

Τα γραφήματα και οι πίνακες στο email είναι **αποκλειστικά screenshots** που τραβιούνται απευθείας από τις ζωντανές σελίδες (όχι AI-generated) — έτσι είναι πάντα ακριβές αντίγραφο του τι δημοσίευσε η πηγή. Ο scraper ψάχνει αυτόματα για `table`, `svg`, `canvas`, `figure` και στοιχεία με class/id που περιέχει "chart"/"graph".

Για πηγές όπου το configured URL είναι λίστα άρθρων (π.χ. Citi) αντί για το ίδιο το άρθρο, χρησιμοποιείται `SiteConfig.FollowFirstLinkSelector` ώστε ο scraper να ακολουθεί αυτόματα το πρώτο link πριν τραβήξει screenshots/κείμενο.

Πριν από κάθε screenshot, ο scraper προσπαθεί αυτόματα να **απορρίψει** (reject/decline) τυχόν cookie-consent banner, ώστε να μην εμφανίζεται μέσα στο στιγμιότυπο. Ελέγχει πρώτα για κουμπιά τύπου "Reject All"/"Decline"/"Only Necessary" κ.λπ. (μόνο πραγματικά `<button>`/`role="button"` στοιχεία, όχι links, ώστε να μην κάνει λάθος πλοήγηση σε άσχετο link) και μόνο αν δεν βρεθεί τέτοιο κουμπί καταφεύγει σε "Accept"-style κουμπί (χρειάζεται π.χ. για νομικά/institutional-investor gates όπως του JPMorgan, που δεν προσφέρουν επιλογή απόρριψης). Ο έλεγχος γίνεται δύο φορές — μία στην αρχή και μία ακριβώς πριν το screenshot — ώστε να πιάνει και banners που εμφανίζονται με καθυστέρηση.

## Gmail App Password

1. Ενεργοποιήστε 2-Step Verification στο Google Account
2. Google Account → Security → App Passwords
3. Επιλέξτε "Mail" → "Windows Computer"
4. Αντιγράψτε τον 16-ψήφιο κωδικό στο `.env`

## AI Providers

Το `AiSummarizer` υποστηρίζει τρεις providers, επιλέγονται μέσω `AI_PROVIDER` στο `.env`:

| Provider | `AI_PROVIDER` | Απαιτούμενα env vars | Σημειώσεις |
|---|---|---|---|
| GitHub Copilot SDK (**default**) | *(κενό)* ή `copilot` | *(κανένα API key — χρειάζεται `copilot` CLI login)*, προαιρετικά `COPILOT_MODEL` | Χρησιμοποιεί το ήδη συνδεδεμένο Copilot session· λειτουργεί ακόμη και όταν το `api.groq.com`/OpenAI endpoints είναι μπλοκαρισμένα από εταιρικό firewall |
| Groq | `groq` | `GROQ_API_KEY` | Απευθείας HTTPS στο `api.groq.com` |
| Azure OpenAI / Foundry | `azure` (ή κενό με πλήρως συμπληρωμένα Azure vars) | `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, `AZURE_OPENAI_DEPLOYMENT`, προαιρετικά `AZURE_OPENAI_API_VERSION` | — |

## Tech Stack

- .NET 8
- Microsoft.Playwright (browser automation)
- GitHub.Copilot.SDK (default AI summarization provider)
- Groq API / Azure OpenAI (alternative AI summarization providers via HTTP)
- MailKit (SMTP email)
- Scriban (HTML templating)
- DotNetEnv (.env file loading)
