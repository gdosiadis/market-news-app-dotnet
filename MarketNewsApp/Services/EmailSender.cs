using System.Net;
using System.Net.Mail;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Scriban;

namespace MarketNewsApp.Services;

public class EmailSender
{
    public void Send(string reportDate, string? marketsReviewPath = null, string? supportiveMaterialPath = null)
    {
        static string? EnvOrNull(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        var gmailUser = EnvOrNull("GMAIL_USER");
        var gmailPass = EnvOrNull("GMAIL_APP_PASSWORD");
        var emailTo = Environment.GetEnvironmentVariable("EMAIL_TO")
            ?? throw new InvalidOperationException("EMAIL_TO not set");

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

        var recipients = emailTo.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Build MIME message
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Market News AI", fromAddress));
        foreach (var addr in recipients)
            message.To.Add(MailboxAddress.Parse(addr));
        message.Subject = $"Market News AI — {DateTime.Now:dd/MM/yyyy}";

        // Short plain-text body — the actual report content lives in the two attached
        // PowerPoint decks (same charts/text/branding as before), not dumped in the email.
        var builder = new BodyBuilder
        {
            TextBody = $"Εβδομαδιαία Ενημέρωση Αγορών — {reportDate}\n\n" +
                "Δείτε τα συνημμένα PowerPoint αρχεία:\n" +
                "  • Markets Review — συνθετική επισκόπηση, κατάσταση πηγών, γραφήματα\n" +
                "  • Supportive Material — αναλυτικά highlights ανά πηγή",
        };

        foreach (var path in new[] { marketsReviewPath, supportiveMaterialPath })
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
            builder.Attachments.Add(path, new ContentType("application", "vnd.openxmlformats-officedocument.presentationml.presentation"));
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