using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

// Format extractors for private search. All of them are pure .NET or PdfPig
// (Apache-2.0), read only the file handed to them, never touch the network,
// and work within ExtractionLimits; TextExtractorRegistry.Run adds the time
// limit and turns any failure into "not indexed (type: unreadable)".

public static class TextEncodings
{
    static TextEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding Get(string? name, Encoding fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        try { return Encoding.GetEncoding(name.Trim().Trim('"')); }
        catch (ArgumentException) { return fallback; }
    }

    public static Encoding Get(int codepage, Encoding fallback)
    {
        try { return Encoding.GetEncoding(codepage); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return fallback; }
    }
}

// Reads text out of one XML vocabulary: text inside "container" elements,
// a line break after each "paragraph", and tab/break/space elements.
public sealed class XmlTextRules
{
    public required Func<string, bool> Ours { get; init; }
    public HashSet<string> Containers { get; init; } = [];
    public HashSet<string> Paragraphs { get; init; } = [];
    public HashSet<string> Tabs { get; init; } = ["tab"];
    public HashSet<string> Breaks { get; init; } = [];
    public HashSet<string> Spaces { get; init; } = [];
    public HashSet<string> Skip { get; init; } = [];

    public static bool Ooxml(string ns, string vocabulary) =>
        ns == $"http://schemas.openxmlformats.org/{vocabulary}/2006/main" || ns == $"http://purl.oclc.org/ooxml/{vocabulary}/main";

    public const string OdfText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

    public static readonly XmlTextRules Word = new()
    {
        Ours = ns => Ooxml(ns, "wordprocessingml"), Containers = ["t"], Paragraphs = ["p"], Breaks = ["br", "cr"]
    };
    public static readonly XmlTextRules Drawing = new()
    {
        Ours = ns => Ooxml(ns, "drawingml"), Containers = ["t"], Paragraphs = ["p"], Breaks = ["br"], Skip = ["fld"]
    };
    public static readonly XmlTextRules Odf = new()
    {
        Ours = ns => ns == OdfText, Containers = ["p", "h"], Paragraphs = ["p", "h"], Breaks = ["line-break"], Spaces = ["s"]
    };

    public void Walk(XmlReader xml, TextSink sink)
    {
        var depth = 0;
        var advance = true;
        while (!sink.Full && (!advance || xml.Read()))
        {
            if (xml.EOF) break;
            advance = Step(xml, sink, ref depth);
        }
    }

    // Handles the current node. Returns false when it already moved the
    // reader to the next node (after skipping an element).
    public bool Step(XmlReader xml, TextSink sink, ref int depth)
    {
        var ours = Ours(xml.NamespaceURI);
        switch (xml.NodeType)
        {
            case XmlNodeType.Element when ours:
                var name = xml.LocalName;
                if (Skip.Contains(name) && !xml.IsEmptyElement) { xml.Skip(); return false; }
                if (Containers.Contains(name) && !xml.IsEmptyElement) depth++;
                else if (Tabs.Contains(name)) sink.Append('\t');
                else if (Breaks.Contains(name)) sink.Append('\n');
                else if (Spaces.Contains(name)) sink.Append(' ');
                if (Paragraphs.Contains(name) && xml.IsEmptyElement) sink.Line();
                break;
            case XmlNodeType.EndElement when ours:
                if (Containers.Contains(xml.LocalName)) depth = Math.Max(0, depth - 1);
                if (Paragraphs.Contains(xml.LocalName)) sink.Line();
                break;
            case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace when depth > 0:
                sink.Append(xml.Value);
                break;
        }
        return true;
    }
}

public static class OoxmlParts
{
    private const string PackageRels = "http://schemas.openxmlformats.org/package/2006/relationships";
    public const string RelationshipAttr = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string StrictRelationshipAttr = "http://purl.oclc.org/ooxml/officeDocument/relationships";

    public static string? RelId(XmlReader xml) =>
        xml.GetAttribute("id", RelationshipAttr) ?? xml.GetAttribute("id", StrictRelationshipAttr);

    // Relationship id → (type, part path resolved against the source part's folder).
    public static Dictionary<string, (string Type, string Path)> Relationships(SafeZip zip, string part)
    {
        var folder = part.Contains('/') ? part[..part.LastIndexOf('/')] : "";
        var relsPath = (folder.Length > 0 ? folder + "/" : "") + "_rels/" + part[(part.LastIndexOf('/') + 1)..] + ".rels";
        var rels = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        using var xml = zip.Xml(relsPath);
        if (xml is null) return rels;
        while (xml.Read())
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "Relationship" && xml.NamespaceURI == PackageRels &&
                xml.GetAttribute("Id") is { } id && xml.GetAttribute("Target") is { } target &&
                xml.GetAttribute("TargetMode") != "External")
                rels[id] = (xml.GetAttribute("Type") ?? "", SafeZip.Resolve(folder, target));
        return rels;
    }
}

// .docx (word/document.xml) and .odt (content.xml): paragraphs of text.
public sealed class OfficeXmlExtractor : ITextExtractor
{
    private readonly string _entry;
    private readonly XmlTextRules _rules;
    public string Kind => SearchKinds.Document;
    public IReadOnlyCollection<string> Extensions { get; }

    private OfficeXmlExtractor(string extension, string entry, XmlTextRules rules)
    {
        Extensions = [extension];
        _entry = entry;
        _rules = rules;
    }

    public static OfficeXmlExtractor Docx() => new(".docx", "word/document.xml", XmlTextRules.Word);
    public static OfficeXmlExtractor Odt() => new(".odt", "content.xml", XmlTextRules.Odf);

    public ExtractedText Extract(Stream file, ExtractionLimits limits, CancellationToken ct)
    {
        using var zip = new SafeZip(file, limits, ct);
        using var xml = zip.Xml(_entry) ?? throw new UnreadableDocumentException("unreadable");
        var sink = new TextSink(limits, ct);
        _rules.Walk(xml, sink);
        return sink.Result();
    }
}

// .xlsx: every sheet, named, one row per line with cells separated by tabs.
// Shared strings are resolved; formulas are not evaluated (the cached value is used).
public sealed class XlsxExtractor : ITextExtractor
{
    public string Kind => SearchKinds.Data;
    public IReadOnlyCollection<string> Extensions { get; } = [".xlsx", ".xlsm"];
    private static bool Ours(string ns) => XmlTextRules.Ooxml(ns, "spreadsheetml");

    public ExtractedText Extract(Stream file, ExtractionLimits limits, CancellationToken ct)
    {
        using var zip = new SafeZip(file, limits, ct);
        var shared = SharedStrings(zip, limits);
        var sheets = new List<(string Name, string RelId)>();
        using (var workbook = zip.Xml("xl/workbook.xml") ?? throw new UnreadableDocumentException("unreadable"))
            while (workbook.Read())
                if (workbook.NodeType == XmlNodeType.Element && workbook.LocalName == "sheet" && Ours(workbook.NamespaceURI) &&
                    OoxmlParts.RelId(workbook) is { } relId)
                    sheets.Add((workbook.GetAttribute("name") ?? "Sheet", relId));
        var rels = OoxmlParts.Relationships(zip, "xl/workbook.xml");
        var sink = new TextSink(limits, ct);
        var cells = 0;
        var capped = false;
        foreach (var (name, relId) in sheets.Take(limits.MaxParts))
        {
            if (!rels.TryGetValue(relId, out var rel)) continue;
            using var xml = zip.Xml(rel.Path);
            if (xml is null) continue;
            sink.Line().Append("Sheet: " + name + "\n");
            string? type = null;
            var inValue = false;
            var inInline = false;
            var rowHasCell = false;
            var cell = new StringBuilder();
            while (!sink.Full && !capped && xml.Read())
            {
                var ours = Ours(xml.NamespaceURI);
                if (xml.NodeType == XmlNodeType.Element && ours)
                {
                    switch (xml.LocalName)
                    {
                        case "row": rowHasCell = false; break;
                        case "c": type = xml.GetAttribute("t"); cell.Clear(); break;
                        case "v": inValue = !xml.IsEmptyElement; break;
                        case "is": inInline = !xml.IsEmptyElement; break;
                        case "t" when inInline: inValue = !xml.IsEmptyElement; break;
                    }
                }
                else if (xml.NodeType == XmlNodeType.EndElement && ours)
                {
                    switch (xml.LocalName)
                    {
                        case "v" or "t": inValue = false; break;
                        case "is": inInline = false; break;
                        case "c":
                            var value = cell.ToString();
                            if (type == "s") value = int.TryParse(value, out var index) && index >= 0 && index < shared.Count ? shared[index] : "";
                            else if (type == "b") value = value == "1" ? "TRUE" : "FALSE";
                            if (value.Length == 0) break;
                            if (rowHasCell) sink.Append('\t');
                            sink.Append(value.ReplaceLineEndings(" "));
                            rowHasCell = true;
                            if (++cells >= limits.MaxCells) capped = true;
                            break;
                        case "row": if (rowHasCell) sink.Append('\n'); break;
                    }
                }
                else if (inValue && xml.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                    cell.Append(xml.Value);
            }
            if (sink.Full || capped) break;
        }
        var result = sink.Result();
        return result with { Truncated = result.Truncated || capped };
    }

    private static List<string> SharedStrings(SafeZip zip, ExtractionLimits limits)
    {
        var shared = new List<string>();
        using var xml = zip.Xml("xl/sharedStrings.xml");
        if (xml is null) return shared;
        StringBuilder? item = null;
        var inText = false;
        var inPhonetic = false;
        while (xml.Read() && shared.Count < limits.MaxCells)
        {
            var ours = Ours(xml.NamespaceURI);
            if (xml.NodeType == XmlNodeType.Element && ours)
            {
                if (xml.LocalName == "si" && !xml.IsEmptyElement) item = new StringBuilder();
                else if (xml.LocalName == "si") shared.Add("");
                else if (xml.LocalName == "rPh") inPhonetic = !xml.IsEmptyElement; // Reading aids, not cell text.
                else if (xml.LocalName == "t" && !xml.IsEmptyElement && !inPhonetic) inText = item is not null;
            }
            else if (xml.NodeType == XmlNodeType.EndElement && ours)
            {
                if (xml.LocalName == "t") inText = false;
                else if (xml.LocalName == "rPh") inPhonetic = false;
                else if (xml.LocalName == "si" && item is not null) { shared.Add(item.ToString()); item = null; }
            }
            else if (inText && item is { Length: < 32768 } && xml.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA)
                item.Append(xml.Value);
        }
        return shared;
    }
}

// .ods: every table, named, one row per line, cells separated by tabs.
// Repeated cells and rows are emitted once rather than expanded.
public sealed class OdsExtractor : ITextExtractor
{
    private const string Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    public string Kind => SearchKinds.Data;
    public IReadOnlyCollection<string> Extensions { get; } = [".ods"];

    public ExtractedText Extract(Stream file, ExtractionLimits limits, CancellationToken ct)
    {
        using var zip = new SafeZip(file, limits, ct);
        using var xml = zip.Xml("content.xml") ?? throw new UnreadableDocumentException("unreadable");
        var sink = new TextSink(limits, ct);
        var cell = new StringBuilder();
        var inCell = false;
        var rowHasCell = false;
        var cells = 0;
        var capped = false;
        while (!sink.Full && !capped && xml.Read())
        {
            var ns = xml.NamespaceURI;
            if (xml.NodeType == XmlNodeType.Element && ns == Table)
            {
                switch (xml.LocalName)
                {
                    case "table": sink.Line().Append("Sheet: " + (xml.GetAttribute("name", Table) ?? "Sheet") + "\n"); break;
                    case "table-row": rowHasCell = false; break;
                    case "table-cell" or "covered-table-cell" when !xml.IsEmptyElement: inCell = true; cell.Clear(); break;
                }
            }
            else if (xml.NodeType == XmlNodeType.EndElement && ns == Table)
            {
                if (xml.LocalName is "table-cell" or "covered-table-cell")
                {
                    inCell = false;
                    var value = cell.ToString().Trim();
                    if (value.Length == 0) continue;
                    if (rowHasCell) sink.Append('\t');
                    sink.Append(value);
                    rowHasCell = true;
                    if (++cells >= limits.MaxCells) capped = true;
                }
                else if (xml.LocalName == "table-row" && rowHasCell) sink.Append('\n');
            }
            else if (inCell && ns == XmlTextRules.OdfText && xml.NodeType == XmlNodeType.Element)
            {
                if (xml.LocalName is "p" or "h" && cell.Length > 0) cell.Append(' ');
                else if (xml.LocalName is "s" or "tab" or "line-break") cell.Append(' ');
            }
            else if (inCell && xml.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace && cell.Length < 32768)
                cell.Append(xml.Value);
        }
        var result = sink.Result();
        return result with { Truncated = result.Truncated || capped };
    }
}

// .pptx: each slide's text in presentation order, then its speaker notes.
public sealed class PptxExtractor : ITextExtractor
{
    public string Kind => SearchKinds.Document;
    public IReadOnlyCollection<string> Extensions { get; } = [".pptx"];

    public ExtractedText Extract(Stream file, ExtractionLimits limits, CancellationToken ct)
    {
        using var zip = new SafeZip(file, limits, ct);
        var order = new List<string>();
        using (var presentation = zip.Xml("ppt/presentation.xml") ?? throw new UnreadableDocumentException("unreadable"))
            while (presentation.Read())
                if (presentation.NodeType == XmlNodeType.Element && presentation.LocalName == "sldId" &&
                    XmlTextRules.Ooxml(presentation.NamespaceURI, "presentationml") && OoxmlParts.RelId(presentation) is { } id)
                    order.Add(id);
        var rels = OoxmlParts.Relationships(zip, "ppt/presentation.xml");
        var sink = new TextSink(limits, ct);
        var number = 0;
        foreach (var id in order.Take(limits.MaxParts))
        {
            if (sink.Full) break;
            if (!rels.TryGetValue(id, out var slide)) continue;
            using (var xml = zip.Xml(slide.Path))
            {
                if (xml is null) continue;
                sink.Line().Append($"Slide {++number}\n");
                XmlTextRules.Drawing.Walk(xml, sink);
            }
            var notes = OoxmlParts.Relationships(zip, slide.Path).Values
                .FirstOrDefault(r => r.Type.EndsWith("/notesSlide", StringComparison.Ordinal));
            if (notes.Path is null) continue;
            using var notesXml = zip.Xml(notes.Path);
            if (notesXml is null) continue;
            var notesText = new TextSink(limits with { MaxTextChars = Math.Min(limits.MaxTextChars, 65536) }, ct);
            XmlTextRules.Drawing.Walk(notesXml, notesText);
            var text = notesText.Result().Text!.Trim();
            if (text.Length > 0) sink.Line().Append("Notes: " + text + "\n");
        }
        return sink.Result();
    }
}

// .odp: each page's text, then its speaker notes.
public sealed class OdpExtractor : ITextExtractor
{
    private const string Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private const string Presentation = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";
    public string Kind => SearchKinds.Document;
    public IReadOnlyCollection<string> Extensions { get; } = [".odp"];

    public ExtractedText Extract(Stream file, ExtractionLimits limits, CancellationToken ct)
    {
        using var zip = new SafeZip(file, limits, ct);
        using var xml = zip.Xml("content.xml") ?? throw new UnreadableDocumentException("unreadable");
        var sink = new TextSink(limits, ct);
        TextSink? notes = null;
        var depth = 0;
        var pages = 0;
        var advance = true;
        while (!sink.Full && (!advance || xml.Read()))
        {
            if (xml.EOF) break;
            advance = true;
            if (xml.NodeType == XmlNodeType.Element && xml.NamespaceURI == Draw && xml.LocalName == "page")
            {
                if (++pages > limits.MaxParts) break;
                sink.Line().Append($"Slide {pages}\n");
            }
            else if (xml.NodeType == XmlNodeType.Element && xml.NamespaceURI == Presentation && xml.LocalName == "notes" && !xml.IsEmptyElement)
                notes = new TextSink(limits with { MaxTextChars = Math.Min(limits.MaxTextChars, 65536) }, ct);
            else if (xml.NodeType == XmlNodeType.EndElement && xml.NamespaceURI == Presentation && xml.LocalName == "notes" && notes is not null)
            {
                var text = notes.Result().Text!.Trim();
                if (text.Length > 0) sink.Line().Append("Notes: " + text + "\n");
                notes = null;
            }
            else advance = XmlTextRules.Odf.Step(xml, notes ?? sink, ref depth);
        }
        return sink.Result();
    }
}

// .pdf: the text layer, page by page, in reading order. A PDF with pages but
// no text (a scan) is reported as "no text layer"; it is never OCR'd.
public sealed class PdfExtractor : ITextExtractor
{
    public string Kind => SearchKinds.Document;
    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    public ExtractedText Extract(Stream file, ExtractionLimits limits, CancellationToken ct)
    {
        if (file.Length > limits.MaxFileBytes) return ExtractedText.Skip("too large");
        PdfDocument document;
        try
        {
            document = PdfDocument.Open(file, new ParsingOptions
            {
                UseLenientParsing = true, SkipMissingFonts = true, ClipPaths = false,
                FilterProvider = new BudgetFilterProvider(DefaultFilterProvider.Instance, limits.MaxUnpackedBytes, ct)
            });
        }
        catch (PdfDocumentEncryptedException) { return ExtractedText.Skip("encrypted"); }
        using (document)
        {
            var sink = new TextSink(limits, ct);
            var pages = Math.Min(document.NumberOfPages, limits.MaxPages);
            for (var i = 1; i <= pages && !sink.Full; i++)
            {
                ct.ThrowIfCancellationRequested();
                sink.Line().Append(ContentOrderTextExtractor.GetText(document.GetPage(i)));
            }
            var result = sink.Result();
            if (string.IsNullOrWhiteSpace(result.Text)) return ExtractedText.Skip(document.NumberOfPages == 0 ? "unreadable" : "no text layer");
            return result with { Truncated = result.Truncated || document.NumberOfPages > limits.MaxPages };
        }
    }

    // Counts every decoded stream against one budget. Flate streams are first
    // inflated into a counter, bounded, so a compression bomb is refused
    // before PdfPig allocates its output.
    private sealed class BudgetFilterProvider(IFilterProvider inner, long budget, CancellationToken ct) : IFilterProvider
    {
        private long _remaining = budget;
        public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary) => Wrap(inner.GetFilters(dictionary));
        public IReadOnlyList<IFilter> GetNamedFilters(IReadOnlyList<NameToken> names) => Wrap(inner.GetNamedFilters(names));
        public IReadOnlyList<IFilter> GetAllFilters() => Wrap(inner.GetAllFilters());
        private IReadOnlyList<IFilter> Wrap(IReadOnlyList<IFilter> filters) => filters.Select(f => (IFilter)new Budgeted(f, this)).ToArray();

        private void Spend(long bytes)
        {
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Add(ref _remaining, -bytes) < 0) throw new UnreadableDocumentException("too large when unpacked");
        }

        private sealed class Budgeted(IFilter filter, BudgetFilterProvider owner) : IFilter
        {
            public bool IsSupported => filter.IsSupported;

            public Memory<byte> Decode(Memory<byte> input, DictionaryToken streamDictionary, IFilterProvider filterProvider, int filterIndex)
            {
                if (filter.GetType().Name == "FlateFilter") Measure(input);
                var output = filter.Decode(input, streamDictionary, filterProvider, filterIndex);
                owner.Spend(output.Length);
                return output;
            }

            private void Measure(Memory<byte> input)
            {
                // Inflate into a scratch buffer only to count; stop at the budget.
                var data = input.ToArray();
                var offset = data.Length >= 2 && (data[0] & 0x0F) == 8 ? 2 : 0; // zlib header
                using var inflate = new DeflateStream(new MemoryStream(data, offset, data.Length - offset), CompressionMode.Decompress);
                var scratch = new byte[81920];
                long total = 0;
                try
                {
                    int n;
                    while ((n = inflate.Read(scratch, 0, scratch.Length)) > 0)
                        if ((total += n) > Volatile.Read(ref owner._remaining)) throw new UnreadableDocumentException("too large when unpacked");
                }
                catch (InvalidDataException) { /* PdfPig decides what to do with damaged data. */ }
            }
        }
    }
}

// .rtf: text with control words, hidden destinations (font and colour
// tables, pictures, metadata, field instructions) and binary data removed.
public sealed class RtfExtractor : ITextExtractor
{
    private const int MaxGroupDepth = 512;
    private static readonly HashSet<string> Destinations =
    [
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "themedata", "colorschememapping",
        "datastore", "latentstyles", "listtable", "listoverridetable", "rsidtbl", "generator", "xmlnstbl",
        "mmathPr", "filetbl", "revtbl", "pgdsctbl", "fldinst", "bkmkstart", "bkmkend", "nonshppict", "objdata",
        "header", "footer", "headerl", "headerr", "headerf", "footerl", "footerr", "footerf", "listtext"
    ];
    private static readonly Dictionary<string, string> Symbols = new()
    {
        ["emdash"] = "—", ["endash"] = "–", ["bullet"] = "•", ["lquote"] = "‘", ["rquote"] = "’",
        ["ldblquote"] = "“", ["rdblquote"] = "”", ["emspace"] = " ", ["enspace"] = " ", ["qmspace"] = " "
    };
    public string Kind => SearchKinds.Document;
    public IReadOnlyCollection<string> Extensions { get; } = [".rtf"];

    public ExtractedText Extract(Stream file, ExtractionLimits limits, CancellationToken ct)
    {
        var bytes = SearchFileReader.ReadBounded(file, limits.MaxFileBytes, out var truncated);
        var rtf = Encoding.Latin1.GetString(bytes);
        if (!rtf.StartsWith("{\\rtf", StringComparison.Ordinal)) throw new UnreadableDocumentException("unreadable");
        var sink = new TextSink(limits, ct);
        var codepage = TextEncodings.Get(1252, Encoding.Latin1);
        var stack = new Stack<(bool Skip, int Uc)>();
        var skip = false;
        var uc = 1;
        var fallback = 0; // characters still to drop after a \u escape
        void Emit(string text)
        {
            if (skip) return;
            if (fallback > 0) { fallback--; return; }
            sink.Append(text);
        }
        var i = 0;
        while (i < rtf.Length && !sink.Full)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            var c = rtf[i];
            if (c == '{')
            {
                if (stack.Count >= MaxGroupDepth) throw new UnreadableDocumentException("unreadable");
                stack.Push((skip, uc));
                i++;
            }
            else if (c == '}')
            {
                if (stack.Count > 0) (skip, uc) = stack.Pop();
                fallback = 0;
                i++;
            }
            else if (c == '\\' && i + 1 < rtf.Length)
            {
                var d = rtf[i + 1];
                if (char.IsAsciiLetter(d))
                {
                    var j = i + 1;
                    while (j < rtf.Length && char.IsAsciiLetter(rtf[j]) && j - i <= 32) j++;
                    var word = rtf[(i + 1)..j];
                    var k = j;
                    var negative = k < rtf.Length && rtf[k] == '-';
                    if (negative) k++;
                    var digits = k;
                    while (k < rtf.Length && char.IsAsciiDigit(rtf[k]) && k - digits < 10) k++;
                    int? parameter = k > digits && int.TryParse(rtf.AsSpan(digits, k - digits), out var p) ? (negative ? -p : p) : null;
                    if (k == digits) k = j;
                    if (k < rtf.Length && rtf[k] == ' ') k++;
                    i = k;
                    if (word == "bin" && parameter > 0) { i = (int)Math.Min(rtf.Length, (long)i + parameter.Value); continue; }
                    if (Destinations.Contains(word)) skip = true;
                    else if (word is "par" or "line" or "sect" or "page" or "row") { if (!skip) sink.Line(); }
                    else if (word is "tab" or "cell") Emit("\t");
                    else if (word == "uc") uc = Math.Clamp(parameter ?? 1, 0, 8);
                    else if (word == "ansicpg" && parameter is { } cp) codepage = TextEncodings.Get(cp, codepage);
                    else if (word == "u" && parameter is { } u)
                    {
                        if (!skip) sink.Append((char)(u < 0 ? u + 65536 : u));
                        fallback = uc;
                    }
                    else if (Symbols.TryGetValue(word, out var symbol)) Emit(symbol);
                }
                else if (d == '\'' && i + 3 < rtf.Length + 0 && byte.TryParse(rtf.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    Emit(codepage.GetString([b]));
                    i += 4;
                }
                else
                {
                    if (d == '*') skip = true;
                    else if (d is '\\' or '{' or '}') Emit(d.ToString());
                    else if (d == '~') Emit(" ");
                    else if (d == '_') Emit("-");
                    else if (d is '\n' or '\r' && !skip) sink.Line();
                    i += 2;
                }
            }
            else
            {
                if (c is not ('\r' or '\n' or '\\')) Emit(c.ToString());
                i++;
            }
        }
        var result = sink.Result();
        return result with { Truncated = result.Truncated || truncated };
    }
}

// Visible text of an HTML or XHTML document. Scripts, styles, templates,
// SVG and embedded objects are dropped; block elements become line breaks.
// A linear scanner rather than regexes, so hostile markup cannot make it slow.
public static class HtmlText
{
    private static readonly HashSet<string> Dropped = ["script", "style", "template", "svg", "object", "iframe", "noembed"];
    private static readonly HashSet<string> Blocks =
    [
        "p", "div", "br", "li", "ul", "ol", "tr", "table", "h1", "h2", "h3", "h4", "h5", "h6", "section", "article",
        "header", "footer", "nav", "aside", "main", "blockquote", "pre", "hr", "dt", "dd", "dl", "title", "figcaption",
        "address", "form", "fieldset", "details", "summary", "caption"
    ];

    public static void Append(string html, TextSink sink)
    {
        var i = 0;
        var n = html.Length;
        while (i < n && !sink.Full)
        {
            if (html[i] != '<')
            {
                var next = html.IndexOf('<', i);
                if (next < 0) next = n;
                AppendText(html[i..next], sink);
                i = next;
                continue;
            }
            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? n : end + 3;
                continue;
            }
            if (string.CompareOrdinal(html, i, "<![CDATA[", 0, 9) == 0)
            {
                var end = html.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                sink.Append(html[(i + 9)..(end < 0 ? n : end)]);
                i = end < 0 ? n : end + 3;
                continue;
            }
            var k = i + 1;
            var closing = k < n && html[k] == '/';
            if (closing) k++;
            var nameStart = k;
            while (k < n && (char.IsAsciiLetterOrDigit(html[k]) || html[k] is '-' or ':')) k++;
            if (k == nameStart && !(k < n && html[k] is '!' or '?'))
            {
                AppendText("<", sink); // A bare "<" in text.
                i++;
                continue;
            }
            var name = html[nameStart..k].ToLowerInvariant();
            if (name.Contains(':')) name = name[(name.IndexOf(':') + 1)..];
            // Find the end of the tag, respecting quoted attribute values.
            char quote = '\0';
            while (k < n && (quote != '\0' || html[k] != '>'))
            {
                if (quote != '\0') { if (html[k] == quote) quote = '\0'; }
                else if (html[k] is '"' or '\'') quote = html[k];
                k++;
            }
            var selfClosing = k > 0 && k < n && html[k - 1] == '/';
            i = Math.Min(n, k + 1);
            if (!closing && !selfClosing && Dropped.Contains(name))
            {
                var end = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                if (end < 0) { i = n; continue; }
                var close = html.IndexOf('>', end);
                i = close < 0 ? n : close + 1;
                continue;
            }
            if (Blocks.Contains(name)) sink.Line();
            else if (name is "td" or "th" && !closing && sink.Last is not ('\n' or '\t')) sink.Append('\t');
        }
        sink.Line();
    }

    private static void AppendText(string raw, TextSink sink)
    {
        var text = WebUtility.HtmlDecode(raw);
        var collapsed = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && (collapsed.Length > 0 || sink.Last is not ('\n' or '\t' or ' '))) collapsed.Append(' ');
            space = false;
            collapsed.Append(c);
        }
        if (space && collapsed.Length > 0) collapsed.Append(' ');
        sink.Append(collapsed.ToString());
    }

    // Charset from a BOM or a <meta charset> near the start, else UTF-8.
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            return new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true).ReadToEnd();
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
        var match = Regex.Match(head, "charset\\s*=\\s*[\"']?([A-Za-z0-9_.:-]{1,40})", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return match.Success && !match.Groups[1].Value.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            ? TextEncodings.Get(match.Groups[1].Value, Encoding.UTF8).GetString(bytes)
            : SearchFileReader.DecodeUtf8(bytes);
    }
}

public sealed class HtmlExtractor : ITextExtractor
{
    public string Kind => SearchKinds.Document;
    public IReadOnlyCollection<string> Extensions { get; } = [".html", ".htm", ".xhtml"];

    public ExtractedText Extract(Stream file, ExtractionLimits limits, CancellationToken ct)
    {
        var bytes = SearchFileReader.ReadBounded(file, limits.MaxFileBytes, out var truncated);
        var sink = new TextSink(limits, ct);
        HtmlText.Append(HtmlText.Decode(bytes), sink);
        var result = sink.Result();
        return result with { Truncated = result.Truncated || truncated };
    }
}

// .epub: the title, then each chapter in spine (reading) order.
public sealed class EpubExtractor : ITextExtractor
{
    private const string Container = "urn:oasis:names:tc:opendocument:xmlns:container";
    private const string Opf = "http://www.idpf.org/2007/opf";
    private const string DublinCore = "http://purl.org/dc/elements/1.1/";
    public string Kind => SearchKinds.Document;
    public IReadOnlyCollection<string> Extensions { get; } = [".epub"];

    public ExtractedText Extract(Stream file, ExtractionLimits limits, CancellationToken ct)
    {
        using var zip = new SafeZip(file, limits, ct);
        string? opfPath = null;
        using (var container = zip.Xml("META-INF/container.xml") ?? throw new UnreadableDocumentException("unreadable"))
            while (opfPath is null && container.Read())
                if (container.NodeType == XmlNodeType.Element && container.LocalName == "rootfile" && container.NamespaceURI == Container)
                    opfPath = container.GetAttribute("full-path");
        if (opfPath is null) throw new UnreadableDocumentException("unreadable");
        var folder = opfPath.Contains('/') ? opfPath[..opfPath.LastIndexOf('/')] : "";
        var manifest = new Dictionary<string, (string Href, string Type)>(StringComparer.Ordinal);
        var spine = new List<string>();
        string? title = null;
        using (var opf = zip.Xml(opfPath) ?? throw new UnreadableDocumentException("unreadable"))
            while (opf.Read())
            {
                if (opf.NodeType != XmlNodeType.Element) continue;
                if (opf.NamespaceURI == Opf && opf.LocalName == "item" && opf.GetAttribute("id") is { } id && opf.GetAttribute("href") is { } href)
                    manifest[id] = (href, opf.GetAttribute("media-type") ?? "");
                else if (opf.NamespaceURI == Opf && opf.LocalName == "itemref" && opf.GetAttribute("idref") is { } idref && spine.Count < limits.MaxParts)
                    spine.Add(idref);
                else if (opf.NamespaceURI == DublinCore && opf.LocalName == "title" && title is null && !opf.IsEmptyElement)
                    title = opf.ReadElementContentAsString();
            }
        var sink = new TextSink(limits, ct);
        if (!string.IsNullOrWhiteSpace(title)) sink.Append(title.Trim()).Line();
        foreach (var idref in spine)
        {
            if (sink.Full) break;
            if (!manifest.TryGetValue(idref, out var item) || !item.Type.Contains("html", StringComparison.OrdinalIgnoreCase)) continue;
            var path = SafeZip.Resolve(folder, Uri.UnescapeDataString(item.Href.Split('#')[0]));
            if (zip.ReadText(path) is { } chapter) HtmlText.Append(chapter, sink);
        }
        return sink.Result();
    }
}

// .eml: Subject, From, To and Date, then the text body (plain text
// preferred, else the HTML part's visible text). Attachments are listed by
// name only; their content is never decoded.
public sealed partial class EmlExtractor : ITextExtractor
{
    private const int MaxDepth = 10;
    private static readonly string[] KnownHeaders = ["from", "to", "subject", "date", "message-id", "received", "mime-version", "content-type"];
    public string Kind => SearchKinds.Mail;
    public IReadOnlyCollection<string> Extensions { get; } = [".eml"];

    [GeneratedRegex(@"=\?([^?\s]{1,40})\?([BbQq])\?([^?\s]*)\?=")]
    private static partial Regex EncodedWord();
    [GeneratedRegex(@"(\?=)\s+(=\?)")]
    private static partial Regex BetweenEncodedWords();

    public ExtractedText Extract(Stream file, ExtractionLimits limits, CancellationToken ct)
    {
        var bytes = SearchFileReader.ReadBounded(file, limits.MaxFileBytes, out var truncated);
        // Latin-1 maps every byte to one char, so the original bytes can be recovered per part.
        var (headers, body) = Split(Encoding.Latin1.GetString(bytes));
        if (!KnownHeaders.Any(headers.ContainsKey)) throw new UnreadableDocumentException("unreadable");
        var sink = new TextSink(limits, ct);
        foreach (var name in new[] { "subject", "from", "to", "date" })
            if (headers.TryGetValue(name, out var value))
                sink.Append(char.ToUpperInvariant(name[0]) + name[1..] + ": " + DecodeHeader(value)).Line();
        var attachments = new List<string>();
        var parts = 0;
        Walk(headers, body, 0, sink, attachments, ref parts, limits, ct);
        if (attachments.Count > 0) sink.Line().Append("Attachments: " + string.Join(", ", attachments)).Line();
        var result = sink.Result();
        return result with { Truncated = result.Truncated || truncated };
    }

    private static void Walk(Dictionary<string, string> headers, string body, int depth, TextSink sink,
        List<string> attachments, ref int parts, ExtractionLimits limits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (++parts > limits.MaxParts || depth > MaxDepth) return;
        var (type, parameters) = ParseValue(headers.GetValueOrDefault("content-type") ?? "text/plain");
        var (disposition, dispositionParameters) = ParseValue(headers.GetValueOrDefault("content-disposition") ?? "");
        var filename = dispositionParameters.GetValueOrDefault("filename") ?? parameters.GetValueOrDefault("name");
        if (type.StartsWith("multipart/", StringComparison.Ordinal) && parameters.TryGetValue("boundary", out var boundary))
        {
            var children = SplitMultipart(body, boundary).Select(Split).ToList();
            if (type == "multipart/alternative")
            {
                var plain = children.FirstOrDefault(c => ParseValue(c.Headers.GetValueOrDefault("content-type") ?? "text/plain").Type == "text/plain");
                var chosen = plain.Headers is not null ? plain : children.FirstOrDefault();
                if (chosen.Headers is not null) Walk(chosen.Headers, chosen.Body, depth + 1, sink, attachments, ref parts, limits, ct);
                return;
            }
            foreach (var (childHeaders, childBody) in children) Walk(childHeaders, childBody, depth + 1, sink, attachments, ref parts, limits, ct);
            return;
        }
        var isText = type is "text/plain" or "text/html";
        if (disposition == "attachment" || !isText)
        {
            if (filename is not null && attachments.Count < 100) attachments.Add(DecodeHeader(filename));
            return;
        }
        var raw = Decode(body, headers.GetValueOrDefault("content-transfer-encoding"));
        var text = TextEncodings.Get(parameters.GetValueOrDefault("charset"), Encoding.UTF8).GetString(raw);
        if (type == "text/html") HtmlText.Append(text, sink);
        else sink.Line().Append(text.ReplaceLineEndings("\n")).Line();
    }

    // Header block and body; header names lower-cased, folded lines unfolded, first occurrence wins.
    private static (Dictionary<string, string> Headers, string Body) Split(string entity)
    {
        var crlf = entity.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var lf = entity.IndexOf("\n\n", StringComparison.Ordinal);
        int end, skip;
        if (crlf >= 0 && (lf < 0 || crlf <= lf)) (end, skip) = (crlf, 4);
        else if (lf >= 0) (end, skip) = (lf, 2);
        else (end, skip) = (entity.Length, 0);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var value = new StringBuilder();
        void Flush()
        {
            if (current is not null && !headers.ContainsKey(current)) headers[current] = value.ToString().Trim();
            value.Clear();
        }
        foreach (var line in entity[..end].Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.Length > 0 && l[0] is ' ' or '\t' && current is not null) { value.Append(' ').Append(l.Trim()); continue; }
            Flush();
            var colon = l.IndexOf(':');
            current = colon > 0 ? l[..colon].Trim().ToLowerInvariant() : null;
            if (current is not null) value.Append(l[(colon + 1)..]);
        }
        Flush();
        return (headers, entity[Math.Min(entity.Length, end + skip)..]);
    }

    private static IEnumerable<string> SplitMultipart(string body, string boundary)
    {
        var delimiter = "--" + boundary;
        var part = new StringBuilder();
        var inside = false;
        foreach (var line in body.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l == delimiter || l == delimiter + "--")
            {
                if (inside) yield return part.ToString();
                part.Clear();
                inside = l == delimiter;
                if (!inside) yield break;
                continue;
            }
            if (inside) part.Append(line).Append('\n');
        }
        if (inside && part.Length > 0) yield return part.ToString();
    }

    // "text/plain; charset=utf-8; name*=utf-8''r%C3%A9sum%C3%A9.pdf"
    private static (string Type, Dictionary<string, string> Parameters) ParseValue(string value)
    {
        var pieces = value.Split(';');
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var piece in pieces.Skip(1))
        {
            var eq = piece.IndexOf('=');
            if (eq <= 0) continue;
            var name = piece[..eq].Trim().ToLowerInvariant();
            var v = piece[(eq + 1)..].Trim().Trim('"');
            if (name.EndsWith('*'))
            {
                name = name.TrimEnd('*');
                var quote = v.IndexOf("''", StringComparison.Ordinal);
                var charset = quote > 0 ? v[..quote] : "utf-8";
                v = TextEncodings.Get(charset, Encoding.UTF8).GetString(PercentDecode(quote >= 0 ? v[(quote + 2)..] : v));
            }
            parameters.TryAdd(name, v);
        }
        return (pieces[0].Trim().ToLowerInvariant(), parameters);
    }

    // RFC 2231 values: %XX escapes are bytes in the named charset.
    private static byte[] PercentDecode(string value)
    {
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && i + 2 < value.Length &&
                byte.TryParse(value.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else bytes.Add((byte)(value[i] > 255 ? '?' : value[i]));
        }
        return bytes.ToArray();
    }

    private static byte[] Decode(string body, string? transferEncoding)
    {
        switch (transferEncoding?.Trim().ToLowerInvariant())
        {
            case "base64":
                try { return Convert.FromBase64String(new string(body.Where(c => !char.IsWhiteSpace(c)).ToArray())); }
                catch (FormatException) { return []; }
            case "quoted-printable":
                return QuotedPrintable(body);
            default:
                return Encoding.Latin1.GetBytes(body);
        }
    }

    private static byte[] QuotedPrintable(string text, bool header = false)
    {
        var output = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (header && c == '_') { output.Add((byte)' '); continue; }
            if (c != '=') { output.Add((byte)(c > 255 ? '?' : c)); continue; }
            if (i + 1 < text.Length && text[i + 1] == '\n') { i++; continue; }
            if (i + 2 < text.Length && text[i + 1] == '\r' && text[i + 2] == '\n') { i += 2; continue; }
            if (i + 2 < text.Length && byte.TryParse(text.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            {
                output.Add(b);
                i += 2;
            }
            else output.Add((byte)'=');
        }
        return output.ToArray();
    }

    // RFC 2047 encoded words, and raw UTF-8 headers (RFC 6532).
    public static string DecodeHeader(string value)
    {
        var raw = Encoding.Latin1.GetBytes(value.Select(c => c > 255 ? '?' : c).ToArray());
        try { value = new UTF8Encoding(false, true).GetString(raw); }
        catch (DecoderFallbackException) { }
        value = BetweenEncodedWords().Replace(value, "$1$2");
        return EncodedWord().Replace(value, m =>
        {
            var encoding = TextEncodings.Get(m.Groups[1].Value.Split('*')[0], Encoding.UTF8);
            try
            {
                var bytes = m.Groups[2].Value is "B" or "b"
                    ? Convert.FromBase64String(m.Groups[3].Value)
                    : QuotedPrintable(m.Groups[3].Value, header: true);
                return encoding.GetString(bytes);
            }
            catch (FormatException) { return m.Value; }
        });
    }
}
