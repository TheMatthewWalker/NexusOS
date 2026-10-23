using System.IO.Compression;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

// Fixtures are generated here rather than committed as binaries, so every
// test shows exactly what the file contains.
public static class Fixtures
{
    public static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        return buffer.ToArray();
    }

    public static byte[] Pdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            var y = 750.0;
            foreach (var line in text.Split('\n'))
            {
                page.AddText(line, 12, new PdfPoint(50, y), font);
                y -= 20;
            }
        }
        return builder.Build();
    }

    // A "scan": pages with drawing but no text layer.
    public static byte[] PdfWithoutText()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        page.DrawRectangle(new PdfPoint(50, 50), 300, 400, 2, fill: true);
        builder.AddPage(PageSize.A4);
        return builder.Build();
    }

    private const string Ss = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Rels = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static byte[] Xlsx(int extraRows = 0) => Zip(
        ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
        ("xl/workbook.xml", $"""
            <workbook xmlns="{Ss}" xmlns:r="{R}"><sheets>
              <sheet name="Prices" sheetId="1" r:id="rId1"/><sheet name="Notes" sheetId="2" r:id="rId2"/>
            </sheets></workbook>
            """),
        ("xl/_rels/workbook.xml.rels", $"""
            <Relationships xmlns="{Rels}">
              <Relationship Id="rId1" Type="{R}/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="{R}/worksheet" Target="/xl/worksheets/sheet2.xml"/>
            </Relationships>
            """),
        ("xl/sharedStrings.xml", $"""
            <sst xmlns="{Ss}"><si><t>Item</t></si><si><t>Price</t></si><si><r><t>Flo</t></r><r><t>ur</t></r><rPh><t>furawa</t></rPh></si></sst>
            """),
        ("xl/worksheets/sheet1.xml", $"""
            <worksheet xmlns="{Ss}"><sheetData>
              <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c></row>
              <row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2"><f>1+1.1</f><v>2.1</v></c><c r="C2" t="b"><v>1</v></c></row>
              {string.Concat(Enumerable.Range(3, extraRows).Select(i => $"<row r=\"{i}\"><c t=\"inlineStr\"><is><t>row{i}</t></is></c></row>"))}
            </sheetData></worksheet>
            """),
        ("xl/worksheets/sheet2.xml", $"""
            <worksheet xmlns="{Ss}"><sheetData><row r="1"><c t="inlineStr"><is><t>Supplier from March</t></is></c></row></sheetData></worksheet>
            """));

    private const string Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private const string Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";

    public static byte[] Ods() => Zip(("mimetype", "application/vnd.oasis.opendocument.spreadsheet"), ("content.xml", $"""
        <office:document-content xmlns:office="{Office}" xmlns:text="{Text}" xmlns:table="{Table}">
          <office:body><office:spreadsheet>
            <table:table table:name="Budget">
              <table:table-row><table:table-cell><text:p>Rent</text:p></table:table-cell>
                <table:table-cell table:number-columns-repeated="1000"/>
                <table:table-cell><text:p>950</text:p></table:table-cell></table:table-row>
              <table:table-row table:number-rows-repeated="100000"><table:table-cell/></table:table-row>
            </table:table>
            <table:table table:name="Ideas"><table:table-row><table:table-cell><text:p>Two</text:p><text:p>lines</text:p></table:table-cell></table:table-row></table:table>
          </office:spreadsheet></office:body>
        </office:document-content>
        """));

    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static string Slide(string root, params string[] paragraphs) => $"""
        <p:{root} xmlns:p="{P}" xmlns:a="{A}"><p:cSld><p:spTree><p:sp><p:txBody>
          {string.Concat(paragraphs.Select(x => $"<a:p><a:r><a:t>{x}</a:t></a:r></a:p>"))}
          <a:p><a:fld type="slidenum"><a:t>99</a:t></a:fld></a:p>
        </p:txBody></p:sp></p:spTree></p:cSld></p:{root}>
        """;

    public static byte[] Pptx() => Zip(
        ("ppt/presentation.xml", $"""
            <p:presentation xmlns:p="{P}" xmlns:r="{R}"><p:sldIdLst>
              <p:sldId id="256" r:id="rId7"/><p:sldId id="257" r:id="rId3"/>
            </p:sldIdLst></p:presentation>
            """),
        ("ppt/_rels/presentation.xml.rels", $"""
            <Relationships xmlns="{Rels}">
              <Relationship Id="rId3" Type="{R}/slide" Target="slides/slide1.xml"/>
              <Relationship Id="rId7" Type="{R}/slide" Target="slides/slide2.xml"/>
            </Relationships>
            """),
        ("ppt/slides/slide1.xml", Slide("sld", "Second slide", "Quarterly numbers")),
        ("ppt/slides/slide2.xml", Slide("sld", "Opening slide")),
        ("ppt/slides/_rels/slide2.xml.rels", $"""
            <Relationships xmlns="{Rels}"><Relationship Id="rId2" Type="{R}/notesSlide" Target="../notesSlides/notesSlide1.xml"/></Relationships>
            """),
        ("ppt/notesSlides/notesSlide1.xml", Slide("notes", "Remember to thank the team")));

    private const string Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private const string Presentation = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";

    public static byte[] Odp() => Zip(("content.xml", $"""
        <office:document-content xmlns:office="{Office}" xmlns:text="{Text}" xmlns:draw="{Draw}" xmlns:presentation="{Presentation}">
          <office:body><office:presentation>
            <draw:page draw:name="page1"><draw:frame><draw:text-box><text:p>Welcome <text:span>everyone</text:span></text:p></draw:text-box></draw:frame>
              <presentation:notes><draw:page-thumbnail/><draw:frame><draw:text-box><text:p>Speak slowly</text:p></draw:text-box></draw:frame></presentation:notes>
            </draw:page>
            <draw:page draw:name="page2"><draw:frame><draw:text-box><text:h>Roadmap</text:h><text:p>Ship search</text:p></draw:text-box></draw:frame></draw:page>
          </office:presentation></office:body>
        </office:document-content>
        """));

    public const string Rtf = """
        {\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil Calibri;}}{\colortbl;\red255\green0\blue0;}
        {\*\generator Riched20 10.0;}{\info{\title Hidden title}}
        \pard\f0\fs22 Hello {\b world} caf\'e9 costs 5\u8364?.\par
        Second\tab line \{braces\}\line
        {\pict\pngblip 89504e470d0a}Last \ldblquote quoted\rdblquote\par}
        """;

    public static byte[] Epub() => Zip(
        ("mimetype", "application/epub+zip"),
        ("META-INF/container.xml", """
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """),
        ("OEBPS/content.opf", """
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>The Garden Book</dc:title></metadata>
              <manifest>
                <item id="c2" href="text/chapter%202.xhtml" media-type="application/xhtml+xml"/>
                <item id="c1" href="text/chapter1.xhtml" media-type="application/xhtml+xml"/>
                <item id="css" href="style.css" media-type="text/css"/>
              </manifest>
              <spine><itemref idref="c1"/><itemref idref="c2"/></spine>
            </package>
            """),
        ("OEBPS/text/chapter1.xhtml", """<html xmlns="http://www.w3.org/1999/xhtml"><head><title>One</title><script>var hidden = 1;</script></head><body><h1>Planting</h1><p>Sow tomatoes in spring.</p></body></html>"""),
        ("OEBPS/text/chapter 2.xhtml", """<html xmlns="http://www.w3.org/1999/xhtml"><body><p>Harvest in autumn.</p></body></html>"""),
        ("OEBPS/style.css", "p { color: red }"));

    public const string Html = """
        <!DOCTYPE html><html><head><title>Supplier list</title>
        <style>body { font-family: secretstyle; }</style><script>var token = "secretscript";</script></head>
        <body><!-- secretcomment --><nav>Home</nav><h1>Prices &amp; terms</h1>
        <p>Flour costs <b>2.10</b>&nbsp;per kg.</p><table><tr><td>A</td><td>B</td></tr></table>
        <p data-x="a > b">Done</p><template><p>secrettemplate</p></template></body></html>
        """;

    public const string Eml = """
        From: =?UTF-8?B?Sm9zw6kgR2FyY8OtYQ==?= <jose@example.org>
        To: me@example.org
        Subject: =?UTF-8?Q?Invoice_for_M=C3=A4rz?=
         (second part)
        Date: Tue, 3 Mar 2026 10:00:00 +0000
        MIME-Version: 1.0
        Content-Type: multipart/mixed; boundary="outer"

        This is a multi-part message.
        --outer
        Content-Type: multipart/alternative; boundary="inner"

        --inner
        Content-Type: text/plain; charset=utf-8
        Content-Transfer-Encoding: quoted-printable

        The plumber's invoice is attached. Total =E2=82=AC120, due soon=
        ly.
        --inner
        Content-Type: text/html; charset=utf-8

        <p>HTML copy that should not be used</p>
        --inner--
        --outer
        Content-Type: application/pdf; name="invoice.pdf"
        Content-Disposition: attachment; filename="invoice.pdf"
        Content-Transfer-Encoding: base64

        QVRUQUNITUVOVC1TRUNSRVQ=
        --outer
        Content-Type: image/png
        Content-Disposition: attachment; filename*=utf-8''r%C3%A9sum%C3%A9.png

        iVBORw0KGgo=
        --outer--
        """;
}

public sealed class SearchFormatTests
{
    public static ExtractedText Extract(string name, byte[] bytes, ExtractionLimits? limits = null, TimeSpan? timeout = null)
    {
        var extractor = TextExtractorRegistry.Default().For(name) ?? throw new InvalidOperationException("No extractor for " + name);
        using var stream = new MemoryStream(bytes);
        return TextExtractorRegistry.Run(extractor, name, stream, limits ?? ExtractionLimits.For(4 * 1024 * 1024), timeout ?? TimeSpan.FromSeconds(30));
    }

    private static string Text(string name, byte[] bytes)
    {
        var result = Extract(name, bytes);
        Assert.True(result.Text is not null, result.Skipped);
        return result.Text!;
    }

    [Fact]
    public void PdfTextLayerIsExtractedPageByPage()
    {
        var text = Text("report.pdf", Fixtures.Pdf("Supplier prices for March\nFlour 2.10", "Second page about invoices"));
        Assert.Contains("Supplier prices for March", text);
        Assert.Contains("Flour 2.10", text);
        Assert.True(text.IndexOf("March", StringComparison.Ordinal) < text.IndexOf("Second page", StringComparison.Ordinal));
    }

    [Fact]
    public void PdfWithoutTextLayerIsReportedNotOcrd() =>
        Assert.Equal("not indexed (pdf: no text layer)", Extract("scan.pdf", Fixtures.PdfWithoutText()).Skipped);

    [Fact]
    public void PdfPageLimitTruncates()
    {
        var limits = ExtractionLimits.For(4 * 1024 * 1024) with { MaxPages = 2 };
        var result = Extract("long.pdf", Fixtures.Pdf("one", "two", "three"), limits);
        Assert.True(result.Truncated);
        Assert.DoesNotContain("three", result.Text);
    }

    [Fact]
    public void XlsxSheetsRowsAndSharedStringsAreExtracted()
    {
        var text = Text("budget.xlsx", Fixtures.Xlsx());
        Assert.Equal("Sheet: Prices\nItem\tPrice\nFlour\t2.1\tTRUE\nSheet: Notes\nSupplier from March\n", text);
        Assert.DoesNotContain("furawa", text); // Phonetic reading aids are not cell text.
        Assert.DoesNotContain("1+1.1", text);  // Formulas are not text.
    }

    [Fact]
    public void XlsxCellLimitTruncates()
    {
        var limits = ExtractionLimits.For(4 * 1024 * 1024) with { MaxCells = 10 };
        var result = Extract("big.xlsx", Fixtures.Xlsx(extraRows: 500), limits);
        Assert.True(result.Truncated);
        Assert.DoesNotContain("row100", result.Text);
    }

    [Fact]
    public void OdsTablesAreExtractedWithoutExpandingRepeats()
    {
        var text = Text("budget.ods", Fixtures.Ods());
        Assert.Equal("Sheet: Budget\nRent\t950\nSheet: Ideas\nTwo lines\n", text);
    }

    [Fact]
    public void PptxSlidesFollowPresentationOrderWithNotes()
    {
        var text = Text("talk.pptx", Fixtures.Pptx());
        Assert.Equal("Slide 1\nOpening slide\nNotes: Remember to thank the team\nSlide 2\nSecond slide\nQuarterly numbers\n", text);
        Assert.DoesNotContain("99", text); // Slide-number fields are skipped.
    }

    [Fact]
    public void OdpPagesAndNotesAreExtracted()
    {
        var text = Text("talk.odp", Fixtures.Odp());
        Assert.Equal("Slide 1\nWelcome everyone\nNotes: Speak slowly\nSlide 2\nRoadmap\nShip search\n", text);
    }

    [Fact]
    public void RtfControlWordsAndHiddenGroupsAreStripped()
    {
        var text = Text("letter.rtf", Encoding.Latin1.GetBytes(Fixtures.Rtf));
        Assert.Equal("Hello world café costs 5€.\nSecond\tline {braces}\nLast “quoted”\n", text);
        Assert.DoesNotContain("Calibri", text);
        Assert.DoesNotContain("Riched20", text);
        Assert.DoesNotContain("Hidden title", text);
        Assert.DoesNotContain("89504e", text);
    }

    [Fact]
    public void RtfThatIsNotRtfIsUnreadable() =>
        Assert.Equal("not indexed (rtf: unreadable)", Extract("x.rtf", Encoding.ASCII.GetBytes("plain text")).Skipped);

    [Fact]
    public void RtfNestingIsBounded() =>
        Assert.Equal("not indexed (rtf: unreadable)", Extract("x.rtf", Encoding.ASCII.GetBytes("{\\rtf1" + new string('{', 10_000))).Skipped);

    [Fact]
    public void EpubChaptersFollowTheSpine()
    {
        var text = Text("garden.epub", Fixtures.Epub());
        Assert.Equal("The Garden Book\nOne\nPlanting\nSow tomatoes in spring.\nHarvest in autumn.\n", text);
        Assert.DoesNotContain("hidden", text);
        Assert.DoesNotContain("color", text);
    }

    [Fact]
    public void HtmlKeepsVisibleTextOnly()
    {
        var text = Text("page.html", Encoding.UTF8.GetBytes(Fixtures.Html));
        Assert.Equal("Supplier list\nHome\nPrices & terms\nFlour costs 2.10 per kg.\nA\tB\nDone\n", text);
        foreach (var hidden in new[] { "secretstyle", "secretscript", "secretcomment", "secrettemplate" })
            Assert.DoesNotContain(hidden, text);
    }

    [Fact]
    public void HtmlCharsetFromMetaIsHonoured()
    {
        var bytes = Encoding.Latin1.GetBytes("<html><head><meta charset=\"iso-8859-1\"></head><body>café</body></html>");
        Assert.Equal("café\n", Text("old.htm", bytes));
    }

    [Fact]
    public void HostileHtmlIsLinear()
    {
        var hostile = string.Concat(Enumerable.Repeat("<a title=\"", 50_000)) + string.Concat(Enumerable.Repeat("<script>", 50_000));
        var result = Extract("bad.html", Encoding.UTF8.GetBytes(hostile), timeout: TimeSpan.FromSeconds(5));
        Assert.NotNull(result.Text);
    }

    [Fact]
    public void EmlHeadersBodyAndAttachmentNames()
    {
        var text = Text("invoice.eml", Encoding.UTF8.GetBytes(Fixtures.Eml.ReplaceLineEndings("\r\n")));
        Assert.StartsWith("Subject: Invoice for März (second part)\nFrom: José García <jose@example.org>\nTo: me@example.org\nDate: Tue, 3 Mar 2026 10:00:00 +0000\n", text);
        Assert.Contains("The plumber's invoice is attached. Total €120, due soonly.", text);
        Assert.EndsWith("Attachments: invoice.pdf, résumé.png\n", text);
        Assert.DoesNotContain("HTML copy", text);          // The plain alternative wins.
        Assert.DoesNotContain("ATTACHMENT-SECRET", text);  // Attachments are listed, never read.
    }

    [Fact]
    public void EmlWithOnlyHtmlUsesItsVisibleText()
    {
        var eml = "Subject: Hi\nContent-Type: text/html\n\n<p>Hello <script>x</script>there</p>";
        Assert.Equal("Subject: Hi\nHello there\n", Text("a.eml", Encoding.UTF8.GetBytes(eml)));
    }

    [Theory]
    [InlineData("x.docx")]
    [InlineData("x.odt")]
    [InlineData("x.xlsx")]
    [InlineData("x.ods")]
    [InlineData("x.pptx")]
    [InlineData("x.odp")]
    [InlineData("x.epub")]
    [InlineData("x.pdf")]
    [InlineData("x.rtf")]
    [InlineData("x.eml")]
    public void MalformedFilesAreReportedUnreadable(string name)
    {
        var garbage = new byte[4096];
        new Random(42).NextBytes(garbage);
        var type = Path.GetExtension(name).TrimStart('.');
        Assert.Equal($"not indexed ({type}: unreadable)", Extract(name, garbage).Skipped);
        // A valid zip without the expected parts is unreadable too.
        if (name is not ("x.pdf" or "x.rtf" or "x.eml"))
            Assert.Equal($"not indexed ({type}: unreadable)", Extract(name, Fixtures.Zip(("other.txt", "hello"))).Skipped);
    }

    [Theory]
    [InlineData("bomb.docx", "word/document.xml")]
    [InlineData("bomb.xlsx", "xl/sharedStrings.xml")]
    [InlineData("bomb.epub", "META-INF/container.xml")]
    public void ZipBombsAreRefused(string name, string entry)
    {
        var bomb = Fixtures.Zip((entry, "<a>" + new string('A', 100 * 1024 * 1024) + "</a>"));
        Assert.True(bomb.Length < 500_000);
        var type = Path.GetExtension(name).TrimStart('.');
        Assert.Equal($"not indexed ({type}: too large when unpacked)", Extract(name, bomb).Skipped);
    }

    [Fact]
    public void ZipWithLyingSizesStillStopsAtTheBudget()
    {
        var bomb = Fixtures.Zip(("word/document.xml", "<a>" + new string('A', 8 * 1024 * 1024) + "</a>"));
        // Rewrite the declared uncompressed sizes (local and central headers) to 100 bytes.
        for (var i = 0; i + 30 < bomb.Length; i++)
        {
            if (BitConverter.ToUInt32(bomb, i) == 0x04034b50) BitConverter.TryWriteBytes(bomb.AsSpan(i + 22), 100u);
            if (BitConverter.ToUInt32(bomb, i) == 0x02014b50) BitConverter.TryWriteBytes(bomb.AsSpan(i + 24), 100u);
        }
        var limits = ExtractionLimits.For(4 * 1024 * 1024) with { MaxUnpackedBytes = 1024 * 1024 };
        var result = Extract("liar.docx", bomb, limits);
        Assert.Null(result.Text);
        Assert.StartsWith("not indexed (docx: ", result.Skipped);
    }

    [Fact]
    public void ArchivesWithTooManyEntriesAreRefused()
    {
        var entries = Enumerable.Range(0, 50).Select(i => ($"f{i}.xml", "<a/>")).ToArray();
        var limits = ExtractionLimits.For(4 * 1024 * 1024) with { MaxZipEntries = 20 };
        Assert.Equal("not indexed (docx: too many archive entries)", Extract("many.docx", Fixtures.Zip(entries), limits).Skipped);
    }

    [Fact]
    public void PdfCompressionBombIsRefused()
    {
        // A valid PDF whose content stream inflates far past the budget.
        var payload = new byte[32 * 1024 * 1024];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) zlib.Write(payload);
        var stream = compressed.ToArray();
        var pdf = new MemoryStream();
        void W(string s) => pdf.Write(Encoding.ASCII.GetBytes(s));
        var offsets = new List<long>();
        W("%PDF-1.4\n");
        offsets.Add(pdf.Position); W("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        offsets.Add(pdf.Position); W("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        offsets.Add(pdf.Position); W("3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >> endobj\n");
        offsets.Add(pdf.Position); W($"4 0 obj << /Length {stream.Length} /Filter /FlateDecode >> stream\n");
        pdf.Write(stream); W("\nendstream endobj\n");
        var xref = pdf.Position;
        W("xref\n0 5\n0000000000 65535 f \n" + string.Concat(offsets.Select(o => $"{o:D10} 00000 n \n")));
        W($"trailer << /Size 5 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var limits = ExtractionLimits.For(4 * 1024 * 1024) with { MaxUnpackedBytes = 1024 * 1024 };
        var result = Extract("bomb.pdf", pdf.ToArray(), limits);
        Assert.Null(result.Text);
        Assert.Equal("not indexed (pdf: too large when unpacked)", result.Skipped);
    }

    private sealed class SlowExtractor(bool cooperative) : ITextExtractor
    {
        public string Kind => SearchKinds.Text;
        public IReadOnlyCollection<string> Extensions { get; } = [".slow"];
        public ExtractedText Extract(Stream file, ExtractionLimits limits, CancellationToken ct)
        {
            if (cooperative) { while (true) ct.ThrowIfCancellationRequested(); }
            Thread.Sleep(TimeSpan.FromSeconds(10));
            return new ExtractedText("late", null);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExtractionIsTimeLimited(bool cooperative)
    {
        using var stream = new MemoryStream([1]);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = TextExtractorRegistry.Run(new SlowExtractor(cooperative), "a.slow", stream,
            ExtractionLimits.For(1024), TimeSpan.FromMilliseconds(200));
        Assert.Equal("not indexed (slow: timed out)", result.Skipped);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void LegacyBinaryOfficeFormatsAreReportedNotRead()
    {
        foreach (var name in new[] { "a.doc", "a.xls", "a.ppt" })
        {
            Assert.Null(TextExtractorRegistry.Default().For(name));
            Assert.Equal($"not indexed ({Path.GetExtension(name).TrimStart('.')})", TextExtractorRegistry.UnsupportedReason(name));
        }
    }

    [Fact]
    public void StatusListsSupportedFormats()
    {
        using var f = new SearchFixture();
        var formats = f.Indexer.Status().Formats;
        Assert.Contains(".pdf", formats.Indexed["document"]);
        Assert.Contains(".xlsx", formats.Indexed["data"]);
        Assert.Contains(".eml", formats.Indexed["mail"]);
        Assert.Contains(".doc", formats.ReportedNotIndexed);
        Assert.Equal(SearchKinds.All, formats.Indexed.Keys);
    }

    [Fact]
    public async Task NewFormatsAreIndexedAndSearchable()
    {
        using var f = new SearchFixture();
        f.Embedder.Model = null;
        var docs = f.Choose("Docs");
        File.WriteAllBytes(Path.Combine(docs, "report.pdf"), Fixtures.Pdf("Quarterly supplier review"));
        File.WriteAllBytes(Path.Combine(docs, "scan.pdf"), Fixtures.PdfWithoutText());
        File.WriteAllBytes(Path.Combine(docs, "budget.xlsx"), Fixtures.Xlsx());
        File.WriteAllBytes(Path.Combine(docs, "talk.pptx"), Fixtures.Pptx());
        File.WriteAllBytes(Path.Combine(docs, "garden.epub"), Fixtures.Epub());
        File.WriteAllText(Path.Combine(docs, "invoice.eml"), Fixtures.Eml);
        File.WriteAllText(Path.Combine(docs, "letter.rtf"), Fixtures.Rtf, Encoding.Latin1);
        var report = await f.ScanAsync();
        Assert.Equal(6, report.Indexed);
        Assert.Equal(1, report.Skipped["not indexed (pdf: no text layer)"]);

        Assert.Equal("report.pdf", Assert.Single((await f.SearchAsync("quarterly supplier review")).Results.Take(1)).Title);
        Assert.Equal("invoice.eml", Assert.Single((await f.SearchAsync("plumber", kinds: "mail")).Results).Title);
        Assert.Equal("budget.xlsx", Assert.Single((await f.SearchAsync("flour", kinds: "data")).Results).Title);
        Assert.Equal("talk.pptx", Assert.Single((await f.SearchAsync("thank")).Results).Title);
        Assert.Equal("garden.epub", Assert.Single((await f.SearchAsync("tomatoes")).Results).Title);
        Assert.Equal("letter.rtf", Assert.Single((await f.SearchAsync("braces")).Results).Title);
        // Attachment content is never decoded or stored, in either form.
        Assert.All(f.IndexFiles(), bytes =>
        {
            var raw = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("ATTACHMENT-SECRET", raw);
            Assert.DoesNotContain("QVRUQUNITUVOVC1TRUNSRVQ", raw);
        });
    }
}
