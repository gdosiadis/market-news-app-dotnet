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
    private readonly ReportTemplateConfiguration? _reportTemplate;

    public EmailSender(EmailConfiguration configuration, ReportTemplateConfiguration? reportTemplate = null)
    {
        _configuration = configuration;
        _reportTemplate = reportTemplate;
    }

    public void Send(string aiSummary, Dictionary<string, SourceSummary> perSource, IReadOnlyList<string> managedRecipients)
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

        var recipients = managedRecipients.Count > 0
            ? managedRecipients
            : _configuration.Recipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var reportDate = DateTime.Now.ToString("dddd, dd MMMM yyyy");
        var sinceDate = DateTime.Now.AddDays(-10).ToString("dd/MM/yyyy");

        var sourceNames = perSource.Keys.ToList();
        var htmlBody = RenderHtml(aiSummary, reportDate, sinceDate, sourceNames);

        // Build MIME message
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_configuration.FromDisplayName, fromAddress));
        foreach (var addr in recipients)
            message.To.Add(MailboxAddress.Parse(addr));
        var subjectTemplate = _reportTemplate?.SubjectTemplate ?? _configuration.SubjectTemplate;
        message.Subject = subjectTemplate.Replace("{{date}}", DateTime.Now.ToString("dd/MM/yyyy"), StringComparison.Ordinal);

        var builder = new BodyBuilder();
        builder.TextBody = $"Εβδομαδιαία Ενημέρωση Αγορών — {reportDate}\n\n" +
            "Ανοίξτε σε email client που υποστηρίζει HTML για να δείτε γραφήματα/πίνακες (screenshots από τις πηγές).\n\n" +
            $"Πηγές: {string.Join(", ", sourceNames)}";
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

        SendMessage(message, smtpHost, smtpPort, smtpUser, smtpPass, smtpSecureSetting);
        ReportArchive.Save(htmlBody, perSource);
        Console.WriteLine($"  Email sent successfully to {string.Join(", ", recipients)}");
    }

    public void SendOperationalAlert(IReadOnlyList<string> recipients, string subject, string htmlBody)
    {
        if (recipients.Count == 0)
            return;

        static string? EnvOrNull(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        var smtpHost = EnvOrNull("SMTP_HOST") ?? "smtp.gmail.com";
        var isGmail = smtpHost == "smtp.gmail.com";
        var gmailUser = EnvOrNull("GMAIL_USER");
        var smtpPort = int.TryParse(EnvOrNull("SMTP_PORT"), out var port) ? port : 465;
        var smtpUser = EnvOrNull("SMTP_USER") ?? (isGmail ? gmailUser : null);
        var smtpPass = EnvOrNull("SMTP_PASS") ?? (isGmail ? EnvOrNull("GMAIL_APP_PASSWORD") : null);
        var smtpSecureSetting = EnvOrNull("SMTP_SECURE")?.Trim().ToLowerInvariant();
        var fromAddress = EnvOrNull("EMAIL_FROM") ?? smtpUser ?? "market-news@localhost";

        if (isGmail && (gmailUser is null || smtpPass is null))
            throw new InvalidOperationException("GMAIL_USER / GMAIL_APP_PASSWORD not set (or configure SMTP_HOST for a different provider)");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_configuration.FromDisplayName, fromAddress));
        foreach (var recipient in recipients)
            message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody, TextBody = HtmlToText(htmlBody) }.ToMessageBody();

        SendMessage(message, smtpHost, smtpPort, smtpUser, smtpPass, smtpSecureSetting);
    }

    private static string HtmlToText(string html) => System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");

    private static void SendMessage(MimeMessage message, string smtpHost, int smtpPort, string? smtpUser, string? smtpPass, string? smtpSecureSetting)
    {
        // Retrying the connection is safe; retrying Send after an uncertain response can duplicate a report.
        Console.WriteLine($"  Sending to {string.Join(", ", message.To)} via {smtpHost}:{smtpPort}...");
        var secureOptions = smtpSecureSetting switch
        {
            "none" => SecureSocketOptions.None,
            "starttls" => SecureSocketOptions.StartTls,
            "ssl" => SecureSocketOptions.SslOnConnect,
            _ => smtpHost == "smtp.gmail.com" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.None,
        };

        using var client = SmtpResilience.ConnectionRetry.Execute(() =>
        {
            var smtpClient = new MailKit.Net.Smtp.SmtpClient { Timeout = 30000 };
            try
            {
                try
                {
                    smtpClient.Connect(smtpHost, smtpPort, secureOptions);
                }
                catch when (smtpHost == "smtp.gmail.com" && smtpSecureSetting is null)
                {
                    smtpClient.Connect(smtpHost, 587, SecureSocketOptions.StartTls);
                }

                if (!string.IsNullOrEmpty(smtpUser) && !string.IsNullOrEmpty(smtpPass))
                    smtpClient.Authenticate(smtpUser, smtpPass);
                return smtpClient;
            }
            catch
            {
                smtpClient.Dispose();
                throw;
            }
        });

        client.Send(message);
        client.Disconnect(true);
    }

    public string RenderHtml(string aiSummary, string reportDate, string sinceDate, IEnumerable<string> sourceNames)
    {
        var templatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "email_template.html");
        if (!File.Exists(templatePath))
            templatePath = Path.Combine(Directory.GetCurrentDirectory(), "Templates", "email_template.html");

        var templateText = File.ReadAllText(templatePath);
        var template = Template.Parse(templateText);

        var reportContent = aiSummary;
        if (_reportTemplate is { IsEnabled: true } && !string.IsNullOrWhiteSpace(_reportTemplate.BodyTemplate))
        {
            var reportTemplate = Template.Parse(_reportTemplate.BodyTemplate);
            if (!reportTemplate.HasErrors)
                reportContent = reportTemplate.Render(new { ai_summary = aiSummary, report_date = reportDate, since_date = sinceDate });
        }

        var selectedSourceNames = sourceNames.Distinct(StringComparer.Ordinal).ToList();
        var sourcePills = string.Join("\n", selectedSourceNames.Select(name =>
            $"<span class=\"source-pill\">{System.Net.WebUtility.HtmlEncode(name)}</span>"));

        return template.Render(new
        {
            ai_summary = reportContent,
            report_date = reportDate,
            since_date = sinceDate,
            source_pills = sourcePills,
            source_names = System.Net.WebUtility.HtmlEncode(string.Join(", ", selectedSourceNames)),
        });
    }
}