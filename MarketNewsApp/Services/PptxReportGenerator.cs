using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using MarketNewsApp.Models;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace MarketNewsApp.Services;

// Builds a self-contained .pptx report (title, per-source status, synthesis, charts)
// as an alternative to the long HTML email — same underlying data, slide format.
public class PptxReportGenerator
{
    // Widescreen 16:9 slide size in EMUs (English Metric Units, 914400 per inch).
    private const int SlideWidth = 12192000;  // 13.333in
    private const int SlideHeight = 6858000;  // 7.5in

    public void Generate(
        string path,
        Dictionary<string, SourceSummary> perSource,
        string synthesisHtml,
        Dictionary<string, string> chartImages,
        string reportDate,
        string sinceDate)
    {
        using var doc = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentationPart = doc.AddPresentationPart();
        presentationPart.Presentation = new Presentation();

        var slideMasterPart = CreateSlideMasterPart(presentationPart);
        var slideLayoutPart = CreateSlideLayoutPart(slideMasterPart);

        var slideMasterIdList = new SlideMasterIdList(new SlideMasterId
        {
            Id = 2147483648,
            RelationshipId = presentationPart.GetIdOfPart(slideMasterPart),
        });
        presentationPart.Presentation.Append(slideMasterIdList);

        var slideIdList = new SlideIdList();
        uint slideId = 256;

        void AddSlide(SlidePart part)
        {
            var rId = presentationPart.GetIdOfPart(part);
            slideIdList.Append(new SlideId { Id = slideId++, RelationshipId = rId });
        }

        // ── Slide 1: Title ──────────────────────────────────────────────────
        AddSlide(CreateTitleSlide(presentationPart, slideLayoutPart, reportDate, sinceDate));

        // ── Slide 2: Source status overview ──────────────────────────────────
        AddSlide(CreateStatusSlide(presentationPart, slideLayoutPart, perSource));

        // ── Slide 3+: Synthesis (split into chunks that fit a slide, capped) ──
        var synthesisBullets = HtmlToBullets(synthesisHtml).Take(16).ToList();
        foreach (var chunk in SplitBullets(synthesisBullets, 8))
            AddSlide(CreateBulletSlide(presentationPart, slideLayoutPart, "🗓️ Συνθετική Επισκόπηση", chunk));

        // ── Slide N: Charts (one per chart image) ─────────────────────────────
        var chartTitles = new Dictionary<string, string>
        {
            ["indices"] = "📈 Δείκτες",
            ["yields"] = "💰 Αποδόσεις Ομολόγων",
            ["forex"] = "💱 Συνάλλαγμα",
            ["macro"] = "🌍 Μακροοικονομικά",
            ["commodities"] = "🛢️ Εμπορεύματα",
        };
        foreach (var (key, b64) in chartImages)
        {
            var title = chartTitles.TryGetValue(key, out var t) ? t : key;
            AddSlide(CreateImageSlide(presentationPart, slideLayoutPart, title, b64));
        }

        // ── Per-source highlights — a few key bullets per source, not the full text
        // (the synthesis slide(s) above already cover the condensed overview; this section
        // is meant as a quick per-source glance, not a duplicate of the "huge email"). ──────
        foreach (var (name, summary) in perSource)
        {
            if (summary.Status is not (SourceStatus.Success or SourceStatus.Partial)) continue;
            var bullets = HtmlToBullets(summary.Html).Take(4).ToList();
            if (bullets.Count == 0) continue;
            AddSlide(CreateBulletSlide(presentationPart, slideLayoutPart, $"📄 {name}", bullets));
        }

        presentationPart.Presentation.Append(slideIdList);
        presentationPart.Presentation.Append(new SlideSize { Cx = SlideWidth, Cy = SlideHeight });
        presentationPart.Presentation.Append(new NotesSize { Cx = SlideHeight, Cy = SlideWidth });
        presentationPart.Presentation.Save();
    }

    // ── HTML → plain-text bullet list ───────────────────────────────────────
    private static List<string> HtmlToBullets(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return new();

        // Split on common block-level tags, then strip remaining markup.
        var parts = Regex.Split(html, @"</(li|p|h[1-6]|div)\s*>", RegexOptions.IgnoreCase);
        var bullets = new List<string>();
        foreach (var part in parts)
        {
            var text = Regex.Replace(part, "<[^>]+>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"\s{2,}", " ").Trim();
            if (text.Length > 0)
                bullets.Add(text.Length > 220 ? text[..220] + "…" : text);
        }
        return bullets;
    }

    private static IEnumerable<List<string>> SplitBullets(List<string> bullets, int perSlide)
    {
        if (bullets.Count == 0) yield break;
        for (int i = 0; i < bullets.Count; i += perSlide)
            yield return bullets.Skip(i).Take(perSlide).ToList();
    }

    // ── Slide master / layout (minimal, blank theme) ────────────────────────
    private static SlideMasterPart CreateSlideMasterPart(PresentationPart presentationPart)
    {
        var slideMasterPart = presentationPart.AddNewPart<SlideMasterPart>();
        var themePart = slideMasterPart.AddNewPart<ThemePart>();
        themePart.Theme = CreateTheme();

        slideMasterPart.SlideMaster = new SlideMaster(
            new CommonSlideData(new ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties(new A.TransformGroup()))),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink,
            });

        return slideMasterPart;
    }

    private static SlideLayoutPart CreateSlideLayoutPart(SlideMasterPart slideMasterPart)
    {
        var slideLayoutPart = slideMasterPart.AddNewPart<SlideLayoutPart>();
        slideLayoutPart.SlideLayout = new SlideLayout(
            new CommonSlideData(new ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new GroupShapeProperties(new A.TransformGroup()))))
        { Type = SlideLayoutValues.Blank };

        var slideLayoutIdList = new SlideLayoutIdList();
        slideLayoutIdList.Append(new SlideLayoutId
        {
            Id = 2147483649,
            RelationshipId = slideMasterPart.GetIdOfPart(slideLayoutPart),
        });
        slideMasterPart.SlideMaster.Append(slideLayoutIdList);
        return slideLayoutPart;
    }

    private static A.Theme CreateTheme()
    {
        var theme = new A.Theme { Name = "MarketNewsTheme" };
        var themeElements = new A.ThemeElements(
            new A.ColorScheme(
                new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "0D1117" }),
                new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
                new A.Dark2Color(new A.RgbColorModelHex { Val = "161B22" }),
                new A.Light2Color(new A.RgbColorModelHex { Val = "E6EDF3" }),
                new A.Accent1Color(new A.RgbColorModelHex { Val = "58A6FF" }),
                new A.Accent2Color(new A.RgbColorModelHex { Val = "3FB950" }),
                new A.Accent3Color(new A.RgbColorModelHex { Val = "F85149" }),
                new A.Accent4Color(new A.RgbColorModelHex { Val = "D29922" }),
                new A.Accent5Color(new A.RgbColorModelHex { Val = "8B949E" }),
                new A.Accent6Color(new A.RgbColorModelHex { Val = "21262D" }),
                new A.Hyperlink(new A.RgbColorModelHex { Val = "58A6FF" }),
                new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "8B949E" })
            ) { Name = "MarketNews" },
            new A.FontScheme(
                new A.MajorFont(new A.LatinFont { Typeface = "Calibri" }, new A.EastAsianFont { Typeface = "" }, new A.ComplexScriptFont { Typeface = "" }),
                new A.MinorFont(new A.LatinFont { Typeface = "Calibri" }, new A.EastAsianFont { Typeface = "" }, new A.ComplexScriptFont { Typeface = "" })
            ) { Name = "MarketNews" },
            new A.FormatScheme(
                new A.FillStyleList(
                    new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 }),
                    new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 }),
                    new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Accent1 })),
                new A.LineStyleList(
                    new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Text1 })),
                    new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Text1 })),
                    new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Text1 }))),
                new A.EffectStyleList(
                    new A.EffectStyle(new A.EffectList()),
                    new A.EffectStyle(new A.EffectList()),
                    new A.EffectStyle(new A.EffectList())),
                new A.BackgroundFillStyleList(
                    new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Light1 }),
                    new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Light1 }),
                    new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.Light1 }))
            ) { Name = "MarketNews" }
        );
        theme.Append(themeElements);
        theme.Append(new A.ObjectDefaults());
        theme.Append(new A.ExtraColorSchemeList());
        return theme;
    }

    // ── Slide builders ───────────────────────────────────────────────────────
    private static SlidePart NewSlidePart(PresentationPart presentationPart, SlideLayoutPart layoutPart)
    {
        var slidePart = presentationPart.AddNewPart<SlidePart>();
        slidePart.AddPart(layoutPart);
        slidePart.Slide = new Slide(new CommonSlideData(new ShapeTree()));
        var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;
        tree.Append(new P.NonVisualGroupShapeProperties(
            new P.NonVisualDrawingProperties { Id = 1, Name = "" },
            new P.NonVisualGroupShapeDrawingProperties(),
            new ApplicationNonVisualDrawingProperties()));
        tree.Append(new GroupShapeProperties(new A.TransformGroup()));
        return slidePart;
    }

    private static P.Shape TextBoxShape(uint id, string name, long x, long y, long cx, long cy,
        IEnumerable<(string text, int sizePt, bool bold)> lines, bool centered = false)
    {
        var shape = new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
            new P.TextBody(
                new A.BodyProperties(),
                new A.ListStyle()));

        var textBody = shape.TextBody!;
        foreach (var (text, sizePt, bold) in lines)
        {
            var paragraph = new A.Paragraph(new A.ParagraphProperties
            {
                Alignment = centered ? A.TextAlignmentTypeValues.Center : A.TextAlignmentTypeValues.Left,
            });
            paragraph.Append(new A.Run(
                new A.RunProperties { Language = "el-GR", FontSize = sizePt * 100, Bold = bold, Dirty = false },
                new A.Text(text)));
            paragraph.Append(new A.EndParagraphRunProperties { Language = "el-GR" });
            textBody.Append(paragraph);
        }
        return shape;
    }

    private static SlidePart CreateTitleSlide(PresentationPart presentationPart, SlideLayoutPart layoutPart, string reportDate, string sinceDate)
    {
        var slidePart = NewSlidePart(presentationPart, layoutPart);
        var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;

        tree.Append(TextBoxShape(2, "Title", 685800, 2286000, 10820400, 1200000,
            new[] { ("📊 Market News AI — Ημερήσια Αναφορά", 40, true) }, centered: true));

        tree.Append(TextBoxShape(3, "Subtitle", 685800, 3600000, 10820400, 800000,
            new[] { ($"Περίοδος {sinceDate} – {reportDate}", 22, false) }, centered: true));

        return slidePart;
    }

    private static SlidePart CreateStatusSlide(PresentationPart presentationPart, SlideLayoutPart layoutPart, Dictionary<string, SourceSummary> perSource)
    {
        var slidePart = NewSlidePart(presentationPart, layoutPart);
        var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;

        tree.Append(TextBoxShape(2, "Title", 457200, 274638, 11277600, 700000,
            new[] { ("📋 Κατάσταση ανά πηγή", 32, true) }));

        var lines = perSource.Select(kv => (Badge(kv.Value.Status) + "  " + kv.Key, 18, false));
        tree.Append(TextBoxShape(3, "Body", 685800, 1200000, 10820400, 5200000, lines));

        return slidePart;
    }

    private static string Badge(SourceStatus status) => status switch
    {
        SourceStatus.Success => "✅ OK",
        SourceStatus.Partial => "⚠️ PARTIAL",
        SourceStatus.Blocked => "⛔ BLOCKED",
        SourceStatus.DisclaimerOnly => "⚠️ DISCLAIMER-ONLY",
        SourceStatus.Error => "❌ ERROR",
        _ => "?",
    };

    private static SlidePart CreateBulletSlide(PresentationPart presentationPart, SlideLayoutPart layoutPart, string title, List<string> bullets)
    {
        var slidePart = NewSlidePart(presentationPart, layoutPart);
        var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;

        tree.Append(TextBoxShape(2, "Title", 457200, 274638, 11277600, 700000,
            new[] { (title, 30, true) }));

        var lines = bullets.Select(b => ("•  " + b, 16, false));
        tree.Append(TextBoxShape(3, "Body", 685800, 1200000, 10820400, 5200000, lines));

        return slidePart;
    }

    private static SlidePart CreateImageSlide(PresentationPart presentationPart, SlideLayoutPart layoutPart, string title, string base64Png)
    {
        var slidePart = NewSlidePart(presentationPart, layoutPart);
        var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;

        tree.Append(TextBoxShape(2, "Title", 457200, 274638, 11277600, 700000,
            new[] { (title, 30, true) }, centered: true));

        var imagePart = slidePart.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(Convert.FromBase64String(base64Png)))
            imagePart.FeedData(stream);
        var rId = slidePart.GetIdOfPart(imagePart);

        var picture = new P.Picture(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = 4, Name = "Chart" },
                new P.NonVisualPictureDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(
                new A.Blip { Embed = rId },
                new A.Stretch(new A.FillRectangle())),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = 1371600, Y = 1150240 },
                    new A.Extents { Cx = 9448800, Cy = 5314445 }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));

        tree.Append(picture);
        return slidePart;
    }
}
