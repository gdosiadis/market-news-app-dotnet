using System.Reflection;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using MarketNewsApp.Models;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace MarketNewsApp.Services;

// Builds self-contained .pptx reports styled after Optima Bank's internal "Marketing Material"
// deck template — mirrors the reference "Weekly Markets Review" / "Weekly Supportive material"
// decks: white content slides with a small corner logo banner, a big orange section title +
// black subtitle top-right, a two-column chart(left)/commentary(right) body for data slides,
// diamond (❖) bullets, and a centered two-line disclaimer footer + page number.
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
    private const string FooterGray = "444444";

    // Slide geometry (16:9, 12192000 x 6858000 EMU) — shared by all content slide builders.
    private const long SlideW = 12192000;
    private const long SlideH = 6858000;
    private const long Margin = 457200;      // 0.5"
    private const long ColumnW = 5486400;     // 6" — left/right column width
    private const long RightColX = SlideW - Margin - ColumnW;
    private const long BodyTop = 1450000;
    private const long BodyBottom = 6350000;

    private int _pageNumber;

    public void Generate(
        string path,
        Dictionary<string, SourceSummary> perSource,
        string synthesisHtml,
        Dictionary<string, string> chartImages,
        string reportDate,
        string sinceDate)
    {
        GenerateMarketsReview(path, perSource, synthesisHtml, chartImages, reportDate, sinceDate);
    }

    // ── Deck 1: "Markets review" — cover, source status, then one chart+commentary slide per
    // market-data category (indices/yields/forex/macro/commodities), mirroring the reference
    // "Weekly Supportive material" deck's chart(left)/text(right) page layout. ────────────────
    public void GenerateMarketsReview(
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
        var blankLayoutPart = GetBlankLayout(presentationPart);
        var slideIdList = ResetSlides(presentationPart);

        uint slideId = 256;
        _pageNumber = 0;

        void AddSlide(SlidePart part)
        {
            var rId = presentationPart.GetIdOfPart(part);
            slideIdList.Append(new SlideId { Id = slideId++, RelationshipId = rId });
        }

        // ── Slide 1: Branded cover ───────────────────────────────────────────
        AddSlide(CreateTitleSlide(presentationPart, blankLayoutPart, "Εβδομαδιαία Ανασκόπηση Αγορών", reportDate, sinceDate));

        // ── Slide 2: Source status overview ──────────────────────────────────
        _pageNumber++;
        AddSlide(CreateStatusSlide(presentationPart, blankLayoutPart, perSource, _pageNumber));

        // ── Slide 3+: one two-column chart+commentary slide per market-data category ────────
        var synthesisBullets = HtmlToBullets(synthesisHtml).Take(20).ToList();
        var categories = new (string Key, string Subtitle, string Caption)[]
        {
            ("indices", "Δείκτες", "Εβδομαδιαία & YTD απόδοση βασικών χρηματιστηριακών δεικτών"),
            ("yields", "Αποδόσεις ομολόγων", "Αποδόσεις κρατικών & εταιρικών ομολόγων"),
            ("forex", "Συνάλλαγμα", "Κινήσεις βασικών ισοτιμιών συναλλάγματος"),
            ("macro", "Μακροοικονομικά", "Βασικοί μακροοικονομικοί δείκτες ΗΠΑ"),
            ("commodities", "Εμπορεύματα", "Τιμές βασικών εμπορευμάτων"),
        };
        var chunks = SplitBullets(synthesisBullets, Math.Max(1, (int)Math.Ceiling(synthesisBullets.Count / (double)Math.Max(1, chartImages.Count)))).ToList();
        int chunkIndex = 0;
        foreach (var (key, subtitle, caption) in categories)
        {
            if (!chartImages.TryGetValue(key, out var b64)) continue;
            var bullets = chunkIndex < chunks.Count ? chunks[chunkIndex] : new List<string>();
            chunkIndex++;
            _pageNumber++;
            AddSlide(CreateChartTextSlide(presentationPart, blankLayoutPart, "Markets review", subtitle, caption, b64, bullets, _pageNumber));
        }

        presentationPart.Presentation.Save();
    }

    // ── Deck 2: "Supportive material" — a few key bullets per source, one slide per
    // source. Mirrors the reference "Weekly Supportive material" deck: the detailed
    // per-source backup slides, kept separate from the high-level overview deck. ─────────
    public void GenerateSupportiveMaterial(
        string path,
        Dictionary<string, SourceSummary> perSource,
        string reportDate,
        string sinceDate)
    {
        File.WriteAllBytes(path, LoadTemplateBytes());

        using var doc = PresentationDocument.Open(path, true);
        var presentationPart = doc.PresentationPart!;
        var blankLayoutPart = GetBlankLayout(presentationPart);
        var slideIdList = ResetSlides(presentationPart);

        uint slideId = 256;
        _pageNumber = 0;

        void AddSlide(SlidePart part)
        {
            var rId = presentationPart.GetIdOfPart(part);
            slideIdList.Append(new SlideId { Id = slideId++, RelationshipId = rId });
        }

        // ── Slide 1: Branded cover ───────────────────────────────────────────
        AddSlide(CreateTitleSlide(presentationPart, blankLayoutPart, "Υποστηρικτικό Υλικό", reportDate, sinceDate));

        foreach (var (name, summary) in perSource)
        {
            if (summary.Status is not (SourceStatus.Success or SourceStatus.Partial)) continue;
            var bullets = HtmlToBullets(summary.Html).Take(5).ToList();
            if (bullets.Count == 0) continue;
            _pageNumber++;
            AddSlide(CreateBulletSlide(presentationPart, blankLayoutPart, "Caught our attention", name, bullets, _pageNumber));
        }

        presentationPart.Presentation.Save();
    }

    private static SlideLayoutPart GetBlankLayout(PresentationPart presentationPart) =>
        presentationPart.SlideMasterParts
            .SelectMany(m => m.SlideLayoutParts)
            .First(l => l.SlideLayout.Type?.Value == SlideLayoutValues.Blank);

    // Removes the template's placeholder slide(s) — callers add their own from scratch.
    private static SlideIdList ResetSlides(PresentationPart presentationPart)
    {
        var slideIdList = presentationPart.Presentation.SlideIdList!;
        foreach (var sid in slideIdList.Elements<SlideId>().ToList())
        {
            var slidePart = (SlidePart)presentationPart.GetPartById(sid.RelationshipId!);
            presentationPart.DeletePart(slidePart);
            sid.Remove();
        }
        return slideIdList;
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

    private static P.Picture AddLogo(SlidePart slidePart, uint id, long x, long y, long cx, long cy) =>
        AddPictureBytes(slidePart, id, "Logo", LoadLogoBytes(), ImagePartType.Png, x, y, cx, cy);

    private static P.Picture AddPictureBytes(SlidePart slidePart, uint id, string name, byte[] bytes, PartTypeInfo type, long x, long y, long cx, long cy)
    {
        var imagePart = slidePart.AddImagePart(type);
        using (var stream = new MemoryStream(bytes))
            imagePart.FeedData(stream);
        var rId = slidePart.GetIdOfPart(imagePart);

        return new P.Picture(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
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

        // Drop heading blocks entirely (the section title/subtitle in the slide header already
        // covers this — including them here would duplicate "🔍 Συνθετική Επισκόπηση…" as a bullet).
        html = Regex.Replace(html, @"<h[1-6][^>]*>.*?</h[1-6]>", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);

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
        A.TextAlignmentTypeValues? align = null, int spaceAfterPt = 0, bool anchorMiddle = false)
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
                new A.BodyProperties
                {
                    Wrap = A.TextWrappingValues.Square,
                    Anchor = anchorMiddle ? A.TextAnchoringTypeValues.Center : A.TextAnchoringTypeValues.Top,
                },
                new A.ListStyle()));

        var textBody = shape.TextBody!;
        foreach (var (text, sizePt, bold, colorHex) in lines)
        {
            var paraProps = new A.ParagraphProperties { Alignment = align.Value };
            if (spaceAfterPt > 0)
                paraProps.SpaceAfter = new A.SpaceAfter(new A.SpacingPoints { Val = spaceAfterPt * 100 });
            var paragraph = new A.Paragraph(paraProps);
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
        // Two centered disclaimer lines, mirroring the reference deck's footer, plus a plain
        // page number bottom-right.
        tree.Append(TextBoxShape(95, "Footer", 3596640, 6553200, 5000000, 260000,
            new[] { ("Εσωτερική χρήση — Δεν αποτελεί επενδυτική συμβουλή", 10, false, Purple) },
            A.TextAlignmentTypeValues.Center));
        tree.Append(TextBoxShape(96, "FooterSub", 3596640, 6710000, 5000000, 200000,
            new[] { ("Market News AI", 9, false, FooterGray) },
            A.TextAlignmentTypeValues.Center));
        tree.Append(TextBoxShape(97, "PageNumber", 11582400, 6553200, 500000, 300000,
            new[] { (pageNumber.ToString(), 12, false, TextDark) },
            A.TextAlignmentTypeValues.Right));
    }

    private static SlidePart CreateTitleSlide(PresentationPart presentationPart, SlideLayoutPart layoutPart, string title, string reportDate, string sinceDate)
    {
        var slidePart = NewSlidePart(presentationPart, layoutPart);
        var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;

        // Full-slide purple background.
        tree.Append(RectangleShape(2, "Background", 0, 0, SlideW, SlideH, Purple));

        // Orange diagonal ribbon accent (mirrors the reference cover's angled band).
        tree.Append(RectangleShape(3, "Ribbon1", 8500000, -1200000, 6500000, 900000, Orange, rotationDeg: -20, alpha: 90000));
        tree.Append(RectangleShape(4, "Ribbon2", 9200000, 5200000, 6500000, 900000, Orange, rotationDeg: -20, alpha: 55000));

        // Logo, top-left.
        tree.Append(AddLogo(slidePart, 5, 685800, 685800, 2971800, 878400));

        // Title (2 lines max, matches the reference cover's stacked title style).
        tree.Append(TextBoxShape(6, "Title", 685800, 2514600, 9144000, 1400000,
            new[] { (title, 40, true, "FFFFFF") }));

        // "Marketing Material" style caption, matching the reference cover's compliance line.
        tree.Append(TextBoxShape(7, "Subtitle", 685800, 4650000, 8229600, 700000,
            new[]
            {
                ("Εσωτερική χρήση — Επενδυτικά Προϊόντα", 16, false, "FFFFFF"),
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

        tree.Append(RectangleShape(1, "Background", 0, 0, SlideW, SlideH, "FFFFFF"));
        AddHeader(tree, slidePart, "Markets review", "Κατάσταση ανά πηγή");

        var lines = perSource.Select(kv => ($"❖  {Badge(kv.Value.Status)}   {kv.Key}", 16, false, TextDark));
        tree.Append(TextBoxShape(3, "Body", Margin, BodyTop, SlideW - 2 * Margin, BodyBottom - BodyTop, lines, spaceAfterPt: 12));

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

        tree.Append(RectangleShape(1, "Background", 0, 0, SlideW, SlideH, "FFFFFF"));
        AddHeader(tree, slidePart, sectionTitle, subtitle);

        var lines = bullets.Select(b => ("❖  " + b, 16, false, TextDark));
        tree.Append(TextBoxShape(3, "Body", Margin, BodyTop, SlideW - 2 * Margin, BodyBottom - BodyTop, lines, spaceAfterPt: 14));

        AddFooter(tree, pageNumber);
        return slidePart;
    }

    // Two-column chart(left)/commentary(right) slide — mirrors the reference "Assets in
    // review" / "Caught our attention" pages: an orange caption above a data chart on the
    // left, and a short bulleted commentary on the right.
    private static SlidePart CreateChartTextSlide(PresentationPart presentationPart, SlideLayoutPart layoutPart,
        string sectionTitle, string subtitle, string caption, string base64Png, List<string> bullets, int pageNumber)
    {
        var slidePart = NewSlidePart(presentationPart, layoutPart);
        var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;

        tree.Append(RectangleShape(1, "Background", 0, 0, SlideW, SlideH, "FFFFFF"));
        AddHeader(tree, slidePart, sectionTitle, subtitle);

        // Left column: orange caption + chart image.
        tree.Append(TextBoxShape(2, "Caption", Margin, BodyTop, ColumnW, 500000,
            new[] { (caption, 13, true, Orange) }));

        var imgBytes = Convert.FromBase64String(base64Png);
        tree.Append(AddPictureBytes(slidePart, 3, "Chart", imgBytes, ImagePartType.Png,
            Margin, BodyTop + 500000, ColumnW, BodyBottom - (BodyTop + 500000)));

        // Right column: diamond-bulleted commentary.
        var lines = bullets.Count > 0
            ? bullets.Select(b => ("❖  " + b, 14, false, TextDark))
            : new[] { ("Δεν εντοπίστηκαν επιπλέον σχόλια για αυτή την κατηγορία.", 14, false, TextDark) };
        tree.Append(TextBoxShape(4, "Commentary", RightColX, BodyTop, ColumnW, BodyBottom - BodyTop, lines, spaceAfterPt: 14));

        AddFooter(tree, pageNumber);
        return slidePart;
    }
}
