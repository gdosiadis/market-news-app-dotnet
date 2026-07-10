using System.Reflection;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using MarketNewsApp.Models;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace MarketNewsApp.Services;

// Builds self-contained .pptx reports on top of "OptimaMasterTemplate.pptx" — a real
// PowerPoint-authored Slide Master with branded custom layouts ("Optima Cover",
// "Optima Bullets") matching Optima Bank's internal "Marketing Material" deck style.
// Each slide we generate simply references one of these layouts and fills in its
// placeholders (title/subtitle/commentary/bullets/date) — the branded background, logo
// banner, ribbon accents and footer text all come from the layout itself, so
// slide-building code stays lean and any manual redesign of the visual style only
// requires editing the template file, not this code.
public class PptxReportGenerator
{
    // Layout names as authored in OptimaMasterTemplate.pptx (see build script history) —
    // used to look up the right SlideLayoutPart for each slide type.
    private const string CoverLayoutName = "Optima Cover";
    private const string BulletsLayoutName = "Optima Bullets";

    public void Generate(
        string path,
        Dictionary<string, SourceSummary> perSource,
        string synthesisHtml,
        string reportDate,
        string sinceDate)
    {
        GenerateMarketsReview(path, perSource, synthesisHtml, reportDate, sinceDate);
    }

    // ── Deck 1: "Markets review" — cover, source status, then the synthesis commentary split
    // across one or more bullet slides. ────────────────────────────────────────────────────
    public void GenerateMarketsReview(
        string path,
        Dictionary<string, SourceSummary> perSource,
        string synthesisHtml,
        string reportDate,
        string sinceDate)
    {
        File.WriteAllBytes(path, LoadTemplateBytes());

        using var doc = PresentationDocument.Open(path, true);
        var presentationPart = doc.PresentationPart!;
        var coverLayout = GetLayoutByName(presentationPart, CoverLayoutName);
        var bulletsLayout = GetLayoutByName(presentationPart, BulletsLayoutName);
        var slideIdList = ResetSlides(presentationPart);

        uint slideId = 256;

        void AddSlide(SlidePart part)
        {
            var rId = presentationPart.GetIdOfPart(part);
            slideIdList.Append(new SlideId { Id = slideId++, RelationshipId = rId });
        }

        // ── Slide 1: Branded cover ───────────────────────────────────────────
        AddSlide(CreateTitleSlide(presentationPart, coverLayout, "Εβδομαδιαία Ανασκόπηση Αγορών", reportDate, sinceDate));

        // ── Slide 2: Source status overview ──────────────────────────────────
        AddSlide(CreateStatusSlide(presentationPart, bulletsLayout, perSource));

        // ── Slide 3+: synthesis commentary, split into bullet slides ─────────────────────────
        var synthesisBullets = HtmlToBullets(synthesisHtml);
        foreach (var chunk in SplitBullets(synthesisBullets, 8))
            AddSlide(CreateBulletSlide(presentationPart, bulletsLayout, "Markets review", "Συνθετική επισκόπηση", chunk));

        presentationPart.Presentation.Save();
    }

    // ── Deck 2: "Supportive material" — a few key bullets per source, one slide per
    // source. ──────────────────────────────────────────────────────────────────────────────
    public void GenerateSupportiveMaterial(
        string path,
        Dictionary<string, SourceSummary> perSource,
        string reportDate,
        string sinceDate)
    {
        File.WriteAllBytes(path, LoadTemplateBytes());

        using var doc = PresentationDocument.Open(path, true);
        var presentationPart = doc.PresentationPart!;
        var coverLayout = GetLayoutByName(presentationPart, CoverLayoutName);
        var bulletsLayout = GetLayoutByName(presentationPart, BulletsLayoutName);
        var slideIdList = ResetSlides(presentationPart);

        uint slideId = 256;

        void AddSlide(SlidePart part)
        {
            var rId = presentationPart.GetIdOfPart(part);
            slideIdList.Append(new SlideId { Id = slideId++, RelationshipId = rId });
        }

        // ── Slide 1: Branded cover ───────────────────────────────────────────
        AddSlide(CreateTitleSlide(presentationPart, coverLayout, "Υποστηρικτικό Υλικό", reportDate, sinceDate));

        foreach (var (name, summary) in perSource)
        {
            if (summary.Status is not (SourceStatus.Success or SourceStatus.Partial)) continue;
            var bullets = HtmlToBullets(summary.Html).Take(5).ToList();
            if (bullets.Count == 0) continue;
            AddSlide(CreateBulletSlide(presentationPart, bulletsLayout, "Caught our attention", name, bullets));
        }

        presentationPart.Presentation.Save();
    }

    // Looks up a SlideLayoutPart by its authored name (p:cSld/@name in slideLayoutN.xml).
    private static SlideLayoutPart GetLayoutByName(PresentationPart presentationPart, string name) =>
        presentationPart.SlideMasterParts
            .SelectMany(m => m.SlideLayoutParts)
            .First(l => l.SlideLayout.CommonSlideData?.Name?.Value == name);

    // Removes the template's placeholder slide(s) — callers add their own from scratch.
    // OptimaMasterTemplate.pptx has zero slides, so <p:sldIdLst> may not exist yet.
    private static SlideIdList ResetSlides(PresentationPart presentationPart)
    {
        var presentation = presentationPart.Presentation;
        var slideIdList = presentation.SlideIdList;
        if (slideIdList == null)
        {
            slideIdList = new SlideIdList();
            presentation.InsertAfter(slideIdList, presentation.SlideMasterIdList!);
        }
        foreach (var sid in slideIdList.Elements<SlideId>().ToList())
        {
            var slidePart = (SlidePart)presentationPart.GetPartById(sid.RelationshipId!);
            presentationPart.DeletePart(slidePart);
            sid.Remove();
        }
        return slideIdList;
    }

    // ── Embedded asset: the branded master template (see Assets/OptimaMasterTemplate.pptx) ──
    private static byte[]? _templateBytes;
    private static byte[] LoadTemplateBytes() => LoadEmbeddedBytes(ref _templateBytes, "OptimaMasterTemplate.pptx");

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

    // ── Slide scaffolding ─────────────────────────────────────────────────────
    private static SlidePart NewSlidePart(PresentationPart presentationPart, SlideLayoutPart layoutPart, bool showCover = false)
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

        // Explicitly force footer + slide-number to render (inherited from the layout's
        // placeholders) — PowerPoint only shows these special placeholders when a slide's
        // <p:hf> flags say so; without this they stay hidden even though the layout defines
        // them. The cover slide has neither placeholder, so both stay off there.
        slidePart.Slide.Append(new HeaderFooter
        {
            Header = false,
            Footer = !showCover,
            DateTime = showCover,
            SlideNumber = !showCover,
        });
        return slidePart;
    }

    // Adds a text placeholder shape that inherits its position/formatting from the slide
    // layout's matching placeholder (matched by type+idx) — no explicit geometry needed,
    // mirroring how PowerPoint itself emits slide XML.
    private static void AddTextPlaceholder(ShapeTree tree, uint id, string name, PlaceholderValues? type, uint? idx, IEnumerable<string> paragraphs)
    {
        var ph = new P.PlaceholderShape();
        if (type.HasValue) ph.Type = type.Value;
        if (idx.HasValue) ph.Index = idx.Value;

        var shape = new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties(ph)),
            new P.ShapeProperties(),
            new P.TextBody(new A.BodyProperties(), new A.ListStyle()));

        var textBody = shape.TextBody!;
        foreach (var text in paragraphs)
        {
            var paragraph = new A.Paragraph();
            paragraph.Append(new A.Run(new A.RunProperties { Language = "el-GR", Dirty = false }, new A.Text(text)));
            paragraph.Append(new A.EndParagraphRunProperties { Language = "el-GR" });
            textBody.Append(paragraph);
        }
        tree.Append(shape);
    }

    // Footer (idx=11) and slide-number (idx=12) placeholders are "special" placeholders that
    // PowerPoint only renders when the slide itself carries a matching shape — the <p:hf>
    // flags alone (set in NewSlidePart) aren't enough. We add them explicitly here, mirroring
    // the exact text/field used in the layout so every content slide shows a matching footer.
    private static void AddFooterAndPageNumber(ShapeTree tree, uint idBase)
    {
        AddTextPlaceholder(tree, idBase, "Footer", PlaceholderValues.Footer, 11, new[]
        {
            "Εσωτερική χρήση — Δεν αποτελεί επενδυτική συμβουλή",
            "Market News AI",
        });

        var ph = new P.PlaceholderShape { Type = PlaceholderValues.SlideNumber, Index = 12 };
        var shape = new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = idBase + 1, Name = "Slide Number" },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new ApplicationNonVisualDrawingProperties(ph)),
            new P.ShapeProperties(),
            new P.TextBody(new A.BodyProperties(), new A.ListStyle(),
                new A.Paragraph(new A.Field(new A.RunProperties { Language = "el-GR" }, new A.Text("‹#›"))
                {
                    Type = "slidenum",
                    Id = "{0932C017-3961-4E85-9FAB-58B99B9EA0F5}",
                })));
        tree.Append(shape);
    }

    // ── Slide builders ───────────────────────────────────────────────────────

    // Cover slide: title (ctrTitle), subtitle (subTitle idx=1), date (dt idx=10). All other
    // visual elements (background, ribbons, logo) live on the "Optima Cover" layout itself.
    private static SlidePart CreateTitleSlide(PresentationPart presentationPart, SlideLayoutPart layoutPart, string title, string reportDate, string sinceDate)
    {
        var slidePart = NewSlidePart(presentationPart, layoutPart, showCover: true);
        var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;

        AddTextPlaceholder(tree, 2, "Title", PlaceholderValues.CenteredTitle, null, new[] { title });
        AddTextPlaceholder(tree, 3, "Subtitle", PlaceholderValues.SubTitle, 1, new[] { "Εσωτερική χρήση — Επενδυτικά Προϊόντα" });
        AddTextPlaceholder(tree, 4, "Date", PlaceholderValues.DateAndTime, 10, new[] { $"{sinceDate} – {reportDate}" });

        return slidePart;
    }

    // Full-width bulleted slide: title (title), subtitle (body idx=13), body (idx=1).
    // Footer/slide-number text/positioning are inherited from the "Optima Bullets" layout —
    // we only need to make the slide-number field visible (its text comes from the field).
    private static SlidePart CreateStatusSlide(PresentationPart presentationPart, SlideLayoutPart layoutPart, Dictionary<string, SourceSummary> perSource)
    {
        var slidePart = NewSlidePart(presentationPart, layoutPart);
        var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;

        AddTextPlaceholder(tree, 2, "Title", PlaceholderValues.Title, null, new[] { "Markets review" });
        AddTextPlaceholder(tree, 3, "Subtitle", PlaceholderValues.Body, 13, new[] { "Κατάσταση ανά πηγή" });

        var lines = perSource.Select(kv => $"❖  {Badge(kv.Value.Status)}   {kv.Key}");
        AddTextPlaceholder(tree, 4, "Bullets", null, 1, lines);

        AddFooterAndPageNumber(tree, 6);
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

    private static SlidePart CreateBulletSlide(PresentationPart presentationPart, SlideLayoutPart layoutPart, string sectionTitle, string subtitle, List<string> bullets)
    {
        var slidePart = NewSlidePart(presentationPart, layoutPart);
        var tree = slidePart.Slide.CommonSlideData!.ShapeTree!;

        AddTextPlaceholder(tree, 2, "Title", PlaceholderValues.Title, null, new[] { sectionTitle });
        AddTextPlaceholder(tree, 3, "Subtitle", PlaceholderValues.Body, 13, new[] { subtitle });
        AddTextPlaceholder(tree, 4, "Bullets", null, 1, bullets.Select(b => "❖  " + b));

        AddFooterAndPageNumber(tree, 6);
        return slidePart;
    }
}