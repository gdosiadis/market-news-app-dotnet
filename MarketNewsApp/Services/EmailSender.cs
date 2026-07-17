using System.Net;
using System.Net.Mail;
using MailKit.Net.Smtp;
using MailKit.Security;
using MarketNewsApp.Models;
using MarketNewsApp.Data;
using MimeKit;
using Scriban;

namespace MarketNewsApp.Services;

public class EmailSender
{
    private readonly EmailConfiguration _configuration;

    public EmailSender(EmailConfiguration configuration)
    {
        _configuration = configuration;
    }

    public void Send(string aiSummary, Dictionary<string, SourceSummary> perSource)
    {
        static string? EnvOrNull(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        var gmailUser = EnvOrNull("GMAIL_USER");
        var gmailPass = EnvOrNull("GMAIL_APP_PASSWORD");

        // ── SMTP settings (optional overrides) ──────────────────────────────
        // Defaults to Gmail; set SMTP_HOST (e.g. localhost for Mailpit) to override.
        var smtpHost = EnvOrNull("SMTP_HOST") ?? "smtp.gmail.com";
        var isGmail = smtpHost == "smtp.gmail.com";
        var smtpPort = int.TryParse(EnvOrNull("SMTP_PORT"), out var p) ? p : 465;
        var smtpUser = EnvOrNull("SMTP_USER") ?? (isGmail ? gmailUser : null);
        var smtpPass = EnvOrNull("SMTP_PASS") ?? (isGmail ? gmailPass : null);
        var smtpSecureSetting = EnvOrNull("SMTP_SECURE")?.Trim().ToLowerInvariant();
        var fromAddress = EnvOrNull("EMAIL_FROM") ?? smtpUser ?? "market-news@localhost";

        if (isGmail && (gmailUser is null || gmailPass is null))
            throw new InvalidOperationException("GMAIL_USER / GMAIL_APP_PASSWORD not set (or configure SMTP_HOST for a different provider)");

        var recipients = _configuration.Recipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var reportDate = DateTime.Now.ToString("dddd, dd MMMM yyyy");
        var sinceDate = DateTime.Now.AddDays(-10).ToString("dd/MM/yyyy");

        var htmlBody = RenderHtml(aiSummary, reportDate, sinceDate);

        // Build MIME message
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_configuration.FromDisplayName, fromAddress));
        foreach (var addr in recipients)
            message.To.Add(MailboxAddress.Parse(addr));
        message.Subject = _configuration.SubjectTemplate.Replace("{{date}}", DateTime.Now.ToString("dd/MM/yyyy"), StringComparison.Ordinal);

        var builder = new BodyBuilder();
        builder.TextBody = $"Εβδομαδιαία Ενημέρωση Αγορών — {reportDate}\n\n" +
            "Ανοίξτε σε email client που υποστηρίζει HTML για να δείτε γραφήματα/πίνακες (screenshots από τις πηγές).\n\n" +
            "Πηγές: Bloomberg, BlackRock, T. Rowe Price, John Hancock, BNP Paribas AM, Edward Jones, JPMorgan AM, Citi";
        builder.HtmlBody = htmlBody;

        // Attach each source's page screenshots (charts/tables captured verbatim from the
        // live site, not AI-rendered) as inline CID images — cid naming matches what
        // AiSummarizer.ComposeHtml embedded via AiSummarizer.ScreenshotCid.
        foreach (var (sourceName, summary) in perSource)
        {
            for (var i = 0; i < summary.Screenshots.Count; i++)
            {
                var cid = AiSummarizer.ScreenshotCid(sourceName, i);
                var imgBytes = Convert.FromBase64String(summary.Screenshots[i]);
                var image = builder.LinkedResources.Add($"{cid}.png", imgBytes, new ContentType("image", "png"));
                image.ContentId = cid;
                image.ContentDisposition = new ContentDisposition(ContentDisposition.Inline);
            }
        }

        message.Body = builder.ToMessageBody();

        // Send via SMTP (Gmail by default, or SMTP_HOST override e.g. Mailpit)
        Console.WriteLine($"  Sending to {string.Join(", ", recipients)} via {smtpHost}:{smtpPort}...");
        using var client = new MailKit.Net.Smtp.SmtpClient();
        client.Timeout = 30000;

        var secureOptions = smtpSecureSetting switch
        {
            "none" => SecureSocketOptions.None,
            "starttls" => SecureSocketOptions.StartTls,
            "ssl" => SecureSocketOptions.SslOnConnect,
            _ => smtpHost == "smtp.gmail.com" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None,
        };

        try
        {
            client.Connect(smtpHost, smtpPort, secureOptions);
        }
        catch when (smtpHost == "smtp.gmail.com" && smtpSecureSetting is null)
        {
            client.Connect(smtpHost, 587, SecureSocketOptions.StartTls);
        }

        if (!string.IsNullOrEmpty(smtpUser) && !string.IsNullOrEmpty(smtpPass))
            client.Authenticate(smtpUser, smtpPass);

        client.Send(message);
        client.Disconnect(true);

        Console.WriteLine($"  Email sent successfully to {string.Join(", ", recipients)}");
    }

    public string RenderHtml(string aiSummary, string reportDate, string sinceDate)
    {
        var templatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "email_template.html");
        if (!File.Exists(templatePath))
            templatePath = Path.Combine(Directory.GetCurrentDirectory(), "Templates", "email_template.html");

        var templateText = File.ReadAllText(templatePath);
        var template = Template.Parse(templateText);

        return template.Render(new
        {
            ai_summary = aiSummary,
            report_date = reportDate,
            since_date = sinceDate,
        });
    }
}