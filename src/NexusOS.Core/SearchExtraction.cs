using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using Microsoft.Win32.SafeHandles;

public static class SearchKinds
{
    public const string Text = "text";
    public const string Code = "code";
    public const string Data = "data";
    public const string Document = "document";
    public const string Mail = "mail";
    public static readonly string[] All = [Text, Code, Data, Document, Mail];
}

// Result of reading one file. Text is null when the file was skipped, and
// Skipped then says why in a few words. Extractors give the short reason
// ("unreadable", "no text layer"); TextExtractorRegistry.Run turns it into
// the reported form "not indexed (pdf: no text layer)".
public sealed record ExtractedText(string? Text, string? Skipped, bool Truncated = false)
{
    public static ExtractedText Skip(string reason) => new(null, reason);
}

// A file that cannot be read as its type. The message is a short, safe reason.
public sealed class UnreadableDocumentException(string reason) : Exception(reason);

// Bounds every extractor works within. The time limit is enforced by the
// registry; the others by the extractors and SafeZip.
public sealed record ExtractionLimits(
    long MaxFileBytes,
    int MaxTextChars,
    long MaxUnpackedBytes = 64L * 1024 * 1024,
    int MaxZipEntries = 10_000,
    int MaxCells = 200_000,
    int MaxPages = 2_000,
    int MaxParts = 2_000)
{
    public static ExtractionLimits For(long maxFileBytes) =>
        new(maxFileBytes, (int)Math.Min(maxFileBytes, 4 * 1024 * 1024));
}

public interface ITextExtractor
{
    string Kind { get; }
    IReadOnlyCollection<string> Extensions { get; }
    ExtractedText Extract(Stream file, ExtractionLimits limits, CancellationToken ct);
}

public sealed class PlainTextExtractor(string kind, params string[] extensions) : ITextExtractor
{
    public string Kind { get; } = kind;
    public IReadOnlyCollection<string> Extensions { get; } = extensions;

    public ExtractedText Extract(Stream file, ExtractionLimits limits, CancellationToken ct)
    {
        var bytes = SearchFileReader.ReadBounded(file, limits.MaxFileBytes, out var truncated);
        // A NUL byte in the first 8 KiB means this is not text, whatever its name says.
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0)) return ExtractedText.Skip("binary content");
        return new ExtractedText(SearchFileReader.DecodeUtf8(bytes), null, truncated);
    }
}

// Collects extracted text up to the character limit, and lets long loops
// notice the time limit.
public sealed class TextSink(ExtractionLimits limits, CancellationToken ct)
{
    private readonly StringBuilder _text = new();
    private int _checks;
    public bool Full { get; private set; }
    public int Length => _text.Length;
    public char Last => _text.Length == 0 ? '\n' : _text[^1];

    public TextSink Append(string? value)
    {
        if (Full || string.IsNullOrEmpty(value)) return this;
        if ((++_checks & 0xFF) == 0) ct.ThrowIfCancellationRequested();
        var room = limits.MaxTextChars - _text.Length;
        if (value.Length > room) { _text.Append(value, 0, Math.Max(0, room)); Full = true; }
        else _text.Append(value);
        return this;
    }

    public TextSink Append(char value) => Append(value.ToString());

    // Ends the current line unless it already ended.
    public TextSink Line()
    {
        if (_text.Length > 0 && Last != '\n') Append('\n');
        return this;
    }

    public ExtractedText Result() => new(_text.ToString(), null, Full);
}

// Zip-based formats (docx, xlsx, pptx, odt, ods, odp, epub). Archives are
// refused when they have too many entries or claim too much unpacked data,
// and every entry is read through one shared budget of real decompressed
// bytes, so headers that lie about sizes cannot get past the cap either.
public sealed class SafeZip : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly CancellationToken _ct;
    private long _budget;

    public SafeZip(Stream file, ExtractionLimits limits, CancellationToken ct)
    {
        if (file.Length > limits.MaxFileBytes) throw new UnreadableDocumentException("too large");
        _ct = ct;
        try { _zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true); }
        catch (InvalidDataException) { throw new UnreadableDocumentException("unreadable"); }
        if (_zip.Entries.Count > limits.MaxZipEntries) throw new UnreadableDocumentException("too many archive entries");
        if (_zip.Entries.Sum(e => e.Length) > limits.MaxUnpackedBytes) throw new UnreadableDocumentException("too large when unpacked");
        _budget = limits.MaxUnpackedBytes;
    }

    public IEnumerable<string> Names => _zip.Entries.Select(e => e.FullName);

    public Stream? Open(string name)
    {
        var entry = _zip.GetEntry(name.TrimStart('/'));
        return entry is null ? null : new BudgetStream(entry.Open(), this);
    }

    public XmlReader? Xml(string name) => Open(name) is { } stream ? SafeXml.Reader(stream) : null;

    public string? ReadText(string name)
    {
        using var stream = Open(name);
        if (stream is null) return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return SearchFileReader.DecodeUtf8(buffer.ToArray());
    }

    public void Dispose() => _zip.Dispose();

    // Resolves a relationship target relative to the part that holds it.
    public static string Resolve(string fromFolder, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = new List<string>(fromFolder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var part in target.Split('/'))
        {
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (part is not ("." or "")) parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private sealed class BudgetStream(Stream inner, SafeZip owner) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            owner._ct.ThrowIfCancellationRequested();
            var n = inner.Read(buffer, offset, count);
            owner._budget -= n;
            if (owner._budget < 0) throw new UnreadableDocumentException("too large when unpacked");
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

public static class SafeXml
{
    // No DTDs (so no entity expansion), no external resolution.
    public static XmlReader Reader(Stream stream) => XmlReader.Create(stream, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true,
        IgnoreProcessingInstructions = true, CloseInput = true
    });
}

// The pluggable list of extractors. Types listed in Unsupported are known
// documents that are reported as "not indexed (type)" rather than ignored.
public sealed class TextExtractorRegistry
{
    private readonly Dictionary<string, ITextExtractor> _byExtension = new(StringComparer.OrdinalIgnoreCase);
    // Legacy binary Office formats: there is no small, maintained, pure-.NET
    // reader for them, and parsing OLE2 compound files would add attack
    // surface for little gain. They are reported, not read.
    public static readonly string[] Unsupported = [".doc", ".xls", ".ppt", ".pages", ".numbers", ".key"];

    public TextExtractorRegistry(IEnumerable<ITextExtractor> extractors)
    {
        foreach (var extractor in extractors)
            foreach (var extension in extractor.Extensions) _byExtension[extension] = extractor;
    }

    public static TextExtractorRegistry Default() => new([
        new PlainTextExtractor(SearchKinds.Text, ".txt", ".md", ".markdown", ".rst", ".org", ".tex", ".adoc"),
        new PlainTextExtractor(SearchKinds.Data, ".csv", ".tsv", ".json", ".yaml", ".yml", ".toml", ".xml", ".ini"),
        new PlainTextExtractor(SearchKinds.Code,
            ".cs", ".fs", ".py", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".go", ".rs", ".java", ".kt", ".c", ".h",
            ".cpp", ".hpp", ".cc", ".rb", ".php", ".sh", ".bash", ".zsh", ".fish", ".nix", ".lua", ".sql",
            ".swift", ".scala", ".hs", ".ml", ".r", ".jl", ".css", ".scss", ".vue", ".svelte", ".qml",
            ".el", ".clj", ".ex", ".exs", ".dart", ".pl", ".ps1", ".zig", ".cmake", ".gradle", ".m", ".mm"),
        OfficeXmlExtractor.Docx(),
        OfficeXmlExtractor.Odt(),
        new XlsxExtractor(),
        new OdsExtractor(),
        new PptxExtractor(),
        new OdpExtractor(),
        new PdfExtractor(),
        new RtfExtractor(),
        new EpubExtractor(),
        new HtmlExtractor(),
        new EmlExtractor()
    ]);

    public ITextExtractor? For(string path) => _byExtension.GetValueOrDefault(Path.GetExtension(path));

    // Kind → extensions, for status and the CLI.
    public IReadOnlyDictionary<string, string[]> Formats() => _byExtension
        .GroupBy(x => x.Value.Kind)
        .OrderBy(g => Array.IndexOf(SearchKinds.All, g.Key))
        .ToDictionary(g => g.Key, g => g.Select(x => x.Key.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray());

    // null means "not a document type at all": not counted, not reported.
    public static string? UnsupportedReason(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return Unsupported.Contains(extension) ? $"not indexed ({extension.TrimStart('.')})" : null;
    }

    // Runs one extractor on one file within the time limit. Whatever goes
    // wrong with a malformed or hostile file becomes a reported skip such as
    // "not indexed (pdf: unreadable)"; it never fails the scan. On a timeout
    // the scan moves on; the extractor sees its token cancelled and the
    // caller closes the file under it.
    public static ExtractedText Run(ITextExtractor extractor, string path, Stream file, ExtractionLimits limits, TimeSpan timeout)
    {
        var type = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        using var cts = new CancellationTokenSource(timeout);
        var work = Task.Run(() => extractor.Extract(file, limits, cts.Token));
        ExtractedText result;
        try
        {
            if (!work.Wait(timeout + TimeSpan.FromSeconds(2))) return ExtractedText.Skip($"not indexed ({type}: timed out)");
            result = work.Result;
        }
        catch (AggregateException ex)
        {
            var reason = ex.InnerException switch
            {
                OperationCanceledException => "timed out",
                UnreadableDocumentException unreadable => unreadable.Message,
                _ => "unreadable"
            };
            return ExtractedText.Skip($"not indexed ({type}: {reason})");
        }
        return result.Skipped is null ? result : result with { Skipped = $"not indexed ({type}: {result.Skipped})" };
    }
}

public static class SearchFileReader
{
    public static string DecodeUtf8(byte[] bytes)
    {
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
    }

    public static byte[] ReadBounded(Stream file, long maxBytes, out bool truncated)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while (buffer.Length < maxBytes && (n = file.Read(chunk, 0, (int)Math.Min(chunk.Length, maxBytes - buffer.Length))) > 0)
            buffer.Write(chunk, 0, n);
        truncated = buffer.Length >= maxBytes && file.ReadByte() >= 0;
        return buffer.ToArray();
    }

    // Opens a regular file without following a symbolic link in its last
    // component, and without blocking on a FIFO. Returns null for anything
    // that is not a regular file. On Linux this is open(O_NOFOLLOW|O_NONBLOCK),
    // so a file swapped for a link after the directory walk is still refused.
    public static FileStream? OpenRegularFile(string path)
    {
        var noFollow = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 or Architecture.X86 => 0x20000,
            Architecture.Arm64 or Architecture.Arm => 0x8000,
            _ => 0
        };
        FileStream stream;
        if (OperatingSystem.IsLinux() && noFollow != 0)
        {
            const int ReadOnly = 0, NonBlock = 0x800, CloseOnExec = 0x80000;
            var fd = open(path, ReadOnly | NonBlock | CloseOnExec | noFollow);
            if (fd < 0) return null;
            var handle = new SafeFileHandle(fd, ownsHandle: true);
            try { stream = new FileStream(handle, FileAccess.Read, 1); }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                handle.Dispose();
                return null;
            }
        }
        else
        {
            if (new FileInfo(path).LinkTarget is not null) return null;
            try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
        // FIFOs, sockets and character devices cannot seek; directories fail above.
        if (!stream.CanSeek) { stream.Dispose(); return null; }
        return stream;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}

public sealed record TextChunk(int Ordinal, int Start, int Length, int FirstLine, int LastLine, string Text);

public static class TextChunker
{
    public const int TargetChars = 1000;
    public const int MaxChars = 1500;

    // Splits text into passages of about TargetChars, preferring to cut at a
    // line break, then at a space. Offsets are character positions in the
    // extracted text; lines are 1-based.
    public static IReadOnlyList<TextChunk> Split(string text, int maxChunks)
    {
        var chunks = new List<TextChunk>();
        var start = 0;
        var line = 1;
        while (start < text.Length && chunks.Count < maxChunks)
        {
            // Skip leading blank space so chunks start at content.
            while (start < text.Length && char.IsWhiteSpace(text[start]))
            {
                if (text[start] == '\n') line++;
                start++;
            }
            if (start >= text.Length) break;
            var end = Math.Min(text.Length, start + MaxChars);
            if (end < text.Length)
            {
                var window = text.AsSpan(start + TargetChars, end - start - TargetChars);
                var cut = window.LastIndexOf('\n');
                if (cut < 0) cut = window.LastIndexOfAny(' ', '\t');
                if (cut >= 0) end = start + TargetChars + cut + 1;
                else if (char.IsHighSurrogate(text[end - 1])) end--;
            }
            var body = text[start..end];
            var lines = body.AsSpan().TrimEnd().Count('\n');
            if (!string.IsNullOrWhiteSpace(body))
                chunks.Add(new TextChunk(chunks.Count, start, end - start, line, line + lines, body.TrimEnd()));
            line += body.AsSpan().Count('\n');
            start = end;
        }
        return chunks;
    }
}
