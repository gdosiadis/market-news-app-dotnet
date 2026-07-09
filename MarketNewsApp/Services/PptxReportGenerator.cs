using System.Reflection;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using MarketNewsApp.Models;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace MarketNewsApp.Services;

// Builds a self-contained .pptx report styled after Optima Bank's internal "Marketing
// Material" deck template (purple/orange branding, logo banner, section headers, footer) —
// a branded alternative to the long HTML email, using the same underlying scraped data.
//
// Implementation note: rather than hand-building every OPC part (presentation.xml,
// slideMaster, theme, docProps, presProps/viewProps/tableStyles, etc.) from scratch, this
// starts from a real PowerPoint-generated template (embedded resource) and only adds/removes
// slide parts. Raw from-scratch OpenXML construction can pass OpenXmlValidator with 0 errors
// yet still be rejected by real PowerPoint as "corrupted" if any boilerplate part is missing
// or laid out non-standardly — reusing a template file avoids that fragility entirely.
public class PptxReportGenerator
{
    // Brand palette sampled from the reference Optima Bank template.
    private const string Purple = "38003D";
    private const string Orange = "FF8B00";
    private const string TextDark = "1A1A1A";
    private const string FooterGray = "6E6E6E";

    private int _pageNumber;

    public void Generate(
        string path,
        Dictionary<string, SourceSummary> perSource,
        string synthesisHtml,
        Dictionary<string, string> chartImages,
        string reportDate,
        string sinceDate)
    {
        File.WriteAllBytes(path, LoadTemplateBytes());

        using var doc = PresentationDocument.Open(path, true);
        var presentationPart = doc.PresentationPart!;

        var blankLayoutPart = presentationPart.SlideMasterParts
            .SelectMany(m => m.SlideLayoutParts)
            .First(l => l.SlideLayout.Type?.Value == SlideLayoutValues.Blank);

        // Remove the template's placeholder slide(s) — we add our own from scratch.
        var slideIdList = presentationPart.Presentation.SlideIdList!;
        foreach (var sid in slideIdList.Elements<SlideId>().ToList())
        {
            var slidePart = (SlidePart)presentationPart.GetPartById(sid.RelationshipId!);
            presentationPart.DeletePart(slidePart);
            sid.Remove();
        }

        uint slideId = 256;
        _pageNumber = 0;

        void AddSlide(SlidePart part)
        {
            var rId = presentationPart.GetIdOfPart(part);
            slideIdList.Append(new SlideId { Id = slideId++, RelationshipId = rId });
        }

        // ── Slide 1: Branded cover ───────────────────────────────────────────
        AddSlide(CreateTitleSlide(presentationPart, blankLayoutPart, reportDate, sinceDate));

        // ── Slide 2: Source status overview ──────────────────────────────────
        _pageNumber++;
        AddSlide(CreateStatusSlide(presentationPart, blankLayoutPart, perSource, _pageNumber));

        // ── Slide 3+: Synthesis (split into chunks that fit a slide, capped) ──
        var synthesisBullets = HtmlToBullets(synthesisHtml).Take(16).ToList();
        foreach (var chunk in SplitBullets(synthesisBullets, 8))
        {
            _pageNumber++;
            AddSlide(CreateBulletSlide(presentationPart, blankLayoutPart, "Markets review", "Συνθετική επισκόπηση", chunk, _pageNumber));
        }

        // ── Slide N: Charts (one per chart image) ─────────────────────────────
        var chartTitles = new Dictionary<string, string>
        {
            ["indices"] = "Δείκτες",
            ["yields"] = "Αποδόσεις ομολόγων",
            ["forex"] = "Συνάλλαγμα",
            ["macro"] = "Μακροοικονομικά",
            ["commodities"] = "Εμπορεύματα",
        };
        foreach (var (key, b64) in chartImages)
        {
            var subtitle = chartTitles.TryGetValue(key, out var t) ? t : key;
            _pageNumber++;
            AddSlide(CreateImageSlide(presentationPart, blankLayoutPart, "Assets in review", subtitle, b64, _pageNumber));
        }

        // ── Per-source highlights — a few key bullets per source, not the full text
        // (the synthesis slide(s) above already cover the condensed overview; this section
        // is meant as a quick per-source glance, not a duplicate of the "huge email"). ──────
        foreach (var (name, summary) in perSource)
        {
            if (summary.Status is not (SourceStatus.Success or SourceStatus.Partial)) continue;
            var bullets = HtmlToBullets(summary.Html).Take(4).ToList();
            if (bullets.Count == 0) continue;
            _pageNumber++;
            AddSlide(CreateBulletSlide(presentationPart, blankLayoutPart, "Caught our attention", name, bullets, _pageNumber));
        }

        presentationPart.Presentation.Save();
    }

    // ── Embedded assets: a real PowerPoint-generated 16:9 template, and the Optima logo ─────
    private static byte[]? _templateBytes;
    private static byte[] LoadTemplateBytes() => LoadEmbeddedBytes(ref _templateBytes, "template.pptx");

    private static byte[]? _logoBytes;
    private static byte[] LoadLogoBytes() => LoadEmbeddedBytes(ref _logoBytes, "optima_logo_banner.png");

    private static byte[] LoadEmbeddedBytes(ref byte[]? cache, string suffix)
    {
        if (cache != null) return cache;
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames().First(n => n.EndsWith(suffix));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var mem = new MemoryStream();
        stream.CopyTo(mem);
        cache = mem.ToArray();
        return cache;
    }

    private static P.Picture AddLogo(SlidePart slidePart, uint id, long x, long y, long cx, long cy)
    {
        var imagePart = slidePart.AddImagePart(ImagePartType.Png);
        using (var stream = new MemoryStream(LoadLogoBytes()))
            imagePart.FeedData(stream);
        var rId = slidePart.GetIdOfPart(imagePart);

        return new P.Picture(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "Logo" },
                new P.NonVisualPictureDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(
                new A.Blip { Embed = rId },
                new A.Stretch(new A.FillRectangle())),
            new P.ShapeProperties(
                new A.Transform2D(
                    new A.Offset { X = x, Y = y },
                    new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
    }

    // ── HTML → plain-text bullet list ───────────────────────────────────────
    private static List<string> HtmlToBullets(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return new();

        // Split on common block-level tags, then strip remaining markup.
        var parts = Regex.Split(html, @"</(?:li|p|h[1-6]|div)\s*>", RegexOptions.IgnoreCase);
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

    private static P.Shape RectangleShape(uint id, string name, long x, long y, long cx, long cy,
        string fillHex, int rotationDeg = 0, int alpha = 100000)
    {
        var transform = new A.Transform2D(
            new A.Offset { X = x, Y = y },
            new A.Extents { Cx = cx, Cy = cy });
        if (rotationDeg != 0)
            transform.Rotation = rotationDeg * 60000;

        var colorModel = new A.RgbColorModelHex { Val = fillHex };
        if (alpha < 100000)
            colorModel.Append(new A.Alpha { Val = alpha });
        var fill = new A.SolidFill(colorModel);

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                transform,
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                fill,
                new A.Outline(new A.NoFill())),
            new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph()));
    }

    private static P.Shape TextBoxShape(uint id, string name, long x, long y, long cx, long cy,
        IEnumerable<(string text, int sizePt, bool bold, string colorHex)> lines,
        A.TextAlignmentTypeValues? align = null)
    {
        align ??= A.TextAlignmentTypeValues.Left;

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
                new A.BodyProperties { Wrap = A.TextWrappingValues.Square, Anchor = A.TextAnchoringTypeValues.Top },
                new A.ListStyle()));

        var textBody = shape.TextBody!;
        foreach (var (text, sizePt, bold, colorHex) in lines)
        {
            var paragraph = new A.Paragraph(new A.ParagraphProperties { Alignment = align.Value });
            var runProps = new A.RunProperties { Language = "el-GR", FontSize = sizePt * 100, Bold = bold, Dirty = false };
            runProps.Append(new A.SolidFill(new A.RgbColorModelHex { Val = colorHex }));
            paragraph.Append(new A.Run(runProps, new A.Text(text)));
            paragraph.Append(new A.EndParagraphRunProperties { Language = "el-GR" });
            textBody.Append(paragraph);
        }
        return shape;
    }

    // ── Shared header/footer (Optima "Marketing Material" template pattern) ────────────────
    private static void AddHeader(ShapeTree tree, SlidePart slidePart, string sectionTitle, string subtitle)
    {
        // Small logo banner, top-left (matches the reference deck's corner logo placement).
        tree.Append(AddLogo(slidePart, 90, 0, 320040, 2286000, 675000));

        // Big orange section title + smaller black subtitle, top-right.
        tree.Append(TextBoxShape(91, "SectionTitle", 5029200, 91440, 6972300, 640080,
            new[] { (sectionTitle, 32, false, Orange) }, A.TextAlignmentTypeValues.Right));
        tree.Append(TextBoxShape(92, "SectionSubtitle", 5029200, 690880, 6972300, 480060,
            new[] { (subtitle, 20, true, TextDark) }, A.TextAlignmentTypeValues.Right));
    }

    private static void AddFooter(ShapeTree tree, int pageNumber)
    {
        tree.Append(TextBoxShape(95, "Footer", 3886200, 6400800, 4419600, 380000,
            new[] { ("Market News AI — Εσωτερική χρήση", 10, false, Purple) },
            A.TextAlignmentTypeValues.Center));
        tree.Append(TextBoxShape(96, "PageNumber", 11582400, 6400800, 500000, 380000,
            new[] { (pageNumber.ToString(), 12, false, FooterGray) },
            A.TextAlignmentTypeValues.Right));
    }

    private static SlidePart CreateTitleSlide(PresentationPart presentationPart, SlideLayoutPart layoutPart, string reportDate, string sinceDate)
    {
        var slidePart = NewSlidePart(presentationPart, layoutPart);
        var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;

        // Full-slide purple background.
        tree.Append(RectangleShape(2, "Background", 0, 0, 12192000, 6858000, Purple));

        // Orange diagonal ribbon accent (mirrors the reference cover's angled band).
        tree.Append(RectangleShape(3, "Ribbon1", 8500000, -1200000, 6500000, 900000, Orange, rotationDeg: -20, alpha: 90000));
        tree.Append(RectangleShape(4, "Ribbon2", 9200000, 5200000, 6500000, 900000, Orange, rotationDeg: -20, alpha: 55000));

        // Logo, top-left.
        tree.Append(AddLogo(slidePart, 5, 685800, 685800, 2971800, 878400));

        // Title.
        tree.Append(TextBoxShape(6, "Title", 685800, 2514600, 9144000, 1150000,
            new[] { ("Ημερήσια Αναφορά Αγορών", 40, true, "FFFFFF") }));

        // Subtitle line (matches the reference "Marketing Material" caption pattern).
        tree.Append(TextBoxShape(7, "Subtitle", 685800, 4800600, 8229600, 700000,
            new[]
            {
                ("Market News AI — Αυτόματη σύνθεση ειδήσεων αγοράς", 16, false, "FFFFFF"),
            }));

        // Date, bottom-left.
        tree.Append(TextBoxShape(8, "Date", 685800, 6172200, 4000000, 400000,
            new[] { ($"{sinceDate} – {reportDate}", 14, false, "E6D6EA") }));

        return slidePart;
    }

    private static SlidePart CreateStatusSlide(PresentationPart presentationPart, SlideLayoutPart layoutPart, Dictionary<string, SourceSummary> perSource, int pageNumber)
    {
        var slidePart = NewSlidePart(presentationPart, layoutPart);
        var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;

        tree.Append(RectangleShape(1, "Background", 0, 0, 12192000, 6858000, "FFFFFF"));
        AddHeader(tree, slidePart, "Markets review", "Κατάσταση ανά πηγή");

        var lines = perSource.Select(kv => ($"❖  {Badge(kv.Value.Status)}   {kv.Key}", 16, false, TextDark));
        tree.Append(TextBoxShape(3, "Body", 685800, 1500000, 10820400, 4700000, lines));

        AddFooter(tree, pageNumber);
        return slidePart;
    }

    private static string Badge(SourceStatus status) => status switch
    {
        SourceStatus.Success => "OK",
        SourceStatus.Partial => "PARTIAL",
        SourceStatus.Blocked => "BLOCKED",
        SourceStatus.DisclaimerOnly => "DISCLAIMER-ONLY",
        SourceStatus.Error => "ERROR",
        _ => "?",
    };

    private static SlidePart CreateBulletSlide(PresentationPart presentationPart, SlideLayoutPart layoutPart, string sectionTitle, string subtitle, List<string> bullets, int pageNumber)
    {
        var slidePart = NewSlidePart(presentationPart, layoutPart);
        var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;

        tree.Append(RectangleShape(1, "Background", 0, 0, 12192000, 6858000, "FFFFFF"));
        AddHeader(tree, slidePart, sectionTitle, subtitle);

        var lines = bullets.Select(b => ("❖  " + b, 15, false, TextDark));
        tree.Append(TextBoxShape(3, "Body", 685800, 1500000, 10820400, 4700000, lines));

        AddFooter(tree, pageNumber);
        return slidePart;
    }

    private static SlidePart CreateImageSlide(PresentationPart presentationPart, SlideLayoutPart layoutPart, string sectionTitle, string subtitle, string base64Png, int pageNumber)
    {
        var slidePart = NewSlidePart(presentationPart, layoutPart);
        var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;

        tree.Append(RectangleShape(1, "Background", 0, 0, 12192000, 6858000, "FFFFFF"));
        AddHeader(tree, slidePart, sectionTitle, subtitle);

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
                    new A.Offset { X = 1371600, Y = 1500000 },
                    new A.Extents { Cx = 9448800, Cy = 5000000 }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));

        tree.Append(picture);
        AddFooter(tree, pageNumber);
        return slidePart;
    }
}
