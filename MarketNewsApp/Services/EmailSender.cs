using System.Net;
using System.Net.Mail;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Scriban;

namespace MarketNewsApp.Services;

public class EmailSender
{
    private static readonly Dictionary<string, string> ChartCids = new()
    {
        ["indices"] = "chart_indices",
        ["yields"] = "chart_yields",
        ["forex"] = "chart_forex",
        ["macro"] = "chart_macro",
        ["commodities"] = "chart_commodities",
    };

    public void Send(string aiSummary, Dictionary<string, string> charts)
    {
        var gmailUser = Environment.GetEnvironmentVariable("GMAIL_USER")
            ?? throw new InvalidOperationException("GMAIL_USER not set");
        var gmailPass = Environment.GetEnvironmentVariable("GMAIL_APP_PASSWORD")
            ?? throw new InvalidOperationException("GMAIL_APP_PASSWORD not set");
        var emailTo = Environment.GetEnvironmentVariable("EMAIL_TO")
            ?? throw new InvalidOperationException("EMAIL_TO not set");

        var recipients = emailTo.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var reportDate = DateTime.Now.ToString("dddd, dd MMMM yyyy");
        var sinceDate = DateTime.Now.AddDays(-10).ToString("dd/MM/yyyy");

        var htmlBody = RenderHtml(aiSummary, charts, reportDate, sinceDate);

        // Build MIME message
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Market News AI", gmailUser));
        foreach (var addr in recipients)
            message.To.Add(MailboxAddress.Parse(addr));
        message.Subject = $"Market News AI — {DateTime.Now:dd/MM/yyyy}";

        var builder = new BodyBuilder();
        builder.TextBody = $"Εβδομαδιαία Ενημέρωση Αγορών — {reportDate}\n\n" +
            "Ανοίξτε σε email client που υποστηρίζει HTML για να δείτε γραφήματα.\n\n" +
            "Πηγές: Bloomberg, BlackRock, T. Rowe Price, John Hancock, BNP Paribas AM, Edward Jones, JPMorgan AM, Citi";
        builder.HtmlBody = htmlBody;

        // Attach charts as inline CID images
        foreach (var (chartKey, b64Data) in charts)
        {
            if (!ChartCids.TryGetValue(chartKey, out var cid)) continue;
            var imgBytes = Convert.FromBase64String(b64Data);
            var image = builder.LinkedResources.Add($"{cid}.png", imgBytes, new ContentType("image", "png"));
            image.ContentId = cid;
            image.ContentDisposition = new ContentDisposition(ContentDisposition.Inline);
        }

        message.Body = builder.ToMessageBody();

        // Send via Gmail SMTP
        Console.WriteLine($"  Sending to {string.Join(", ", recipients)} via Gmail...");
        using var client = new MailKit.Net.Smtp.SmtpClient();
        client.Timeout = 30000;
        try
        {
            client.Connect("smtp.gmail.com", 465, SecureSocketOptions.SslOnConnect);
        }
        catch
        {
            client.Connect("smtp.gmail.com", 587, SecureSocketOptions.StartTls);
        }
        client.Authenticate(gmailUser, gmailPass);
        client.Send(message);
        client.Disconnect(true);

        Console.WriteLine($"  Email sent successfully to {string.Join(", ", recipients)}");
    }

    public string RenderHtml(string aiSummary, Dictionary<string, string> charts, string reportDate, string sinceDate)
    {
        var templatePath = Path.Combine(AppContext.BaseDirectory, "Templates", "email_template.html");
        if (!File.Exists(templatePath))
            templatePath = Path.Combine(Directory.GetCurrentDirectory(), "Templates", "email_template.html");

        var templateText = File.ReadAllText(templatePath);
        var template = Template.Parse(templateText);

        var chartCids = charts.Keys
            .Where(k => ChartCids.ContainsKey(k))
            .ToDictionary(k => k, k => ChartCids[k]);

        return template.Render(new
        {
            ai_summary = aiSummary,
            charts = chartCids,
            report_date = reportDate,
            since_date = sinceDate,
        });
    }
}
