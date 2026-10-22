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
    public static readonly string[] All = [Text, Code, Data, Document];
}

// Result of reading one file. Text is null when the file was skipped, and
// Skipped then says why in a few words ("not indexed (pdf)", "too large").
public sealed record ExtractedText(string? Text, string? Skipped, bool Truncated = false)
{
    public static ExtractedText Skip(string reason) => new(null, reason);
}

public interface ITextExtractor
{
    string Kind { get; }
    IReadOnlyCollection<string> Extensions { get; }
    ExtractedText Extract(Stream file, long maxBytes);
}

public sealed class PlainTextExtractor(string kind, params string[] extensions) : ITextExtractor
{
    public string Kind { get; } = kind;
    public IReadOnlyCollection<string> Extensions { get; } = extensions;

    public ExtractedText Extract(Stream file, long maxBytes)
    {
        var bytes = SearchFileReader.ReadBounded(file, maxBytes, out var truncated);
        // A NUL byte in the first 8 KiB means this is not text, whatever its name says.
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0)) return ExtractedText.Skip("binary content");
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new ExtractedText(Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset), null, truncated);
    }
}

// .docx and .odt are zip archives of XML. Reading them needs no external tool:
// take the main body part and keep only its text runs, with every archive
// entry and the extracted text bounded so a zip bomb cannot exhaust memory.
public sealed class OfficeXmlExtractor : ITextExtractor
{
    private const long MaxXmlBytes = 16 * 1024 * 1024;
    private readonly string _entry;
    private readonly string _namespace;
    private readonly HashSet<string> _containers;
    private readonly HashSet<string> _paragraphs;
    public string Kind => SearchKinds.Document;
    public IReadOnlyCollection<string> Extensions { get; }

    private OfficeXmlExtractor(string extension, string entry, string ns, string[] containers, string[] paragraphs)
    {
        Extensions = [extension];
        _entry = entry;
        _namespace = ns;
        _containers = [.. containers];
        _paragraphs = [.. paragraphs];
    }

    // Word keeps text in w:t runs inside w:p paragraphs; ODF keeps it directly
    // inside text:p and text:h (with nested spans).
    public static OfficeXmlExtractor Docx() => new(".docx", "word/document.xml",
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main", ["t"], ["p"]);
    public static OfficeXmlExtractor Odt() => new(".odt", "content.xml",
        "urn:oasis:names:tc:opendocument:xmlns:text:1.0", ["p", "h"], ["p", "h"]);

    public ExtractedText Extract(Stream file, long maxBytes)
    {
        if (file.Length > maxBytes) return ExtractedText.Skip("too large");
        try
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.GetEntry(_entry);
            if (entry is null) return ExtractedText.Skip("unreadable document");
            if (entry.Length > MaxXmlBytes) return ExtractedText.Skip("too large");
            using var xml = XmlReader.Create(new BoundedStream(entry.Open(), MaxXmlBytes), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true,
                IgnoreProcessingInstructions = true, MaxCharactersInDocument = MaxXmlBytes
            });
            var text = new StringBuilder();
            var limit = (int)Math.Min(maxBytes, int.MaxValue / 2);
            var truncated = false;
            var depth = 0;
            while (xml.Read())
            {
                if (text.Length >= limit) { truncated = true; break; }
                var ours = xml.NamespaceURI == _namespace;
                switch (xml.NodeType)
                {
                    case XmlNodeType.Element when ours:
                        if (_containers.Contains(xml.LocalName) && !xml.IsEmptyElement) depth++;
                        else if (xml.LocalName == "tab") text.Append('\t');
                        else if (xml.LocalName is "br" or "line-break") text.Append('\n');
                        else if (xml.LocalName == "s") text.Append(' ');
                        if (_paragraphs.Contains(xml.LocalName) && xml.IsEmptyElement) text.Append('\n');
                        break;
                    case XmlNodeType.EndElement when ours:
                        if (_containers.Contains(xml.LocalName)) depth = Math.Max(0, depth - 1);
                        if (_paragraphs.Contains(xml.LocalName)) text.Append('\n');
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace
                        when depth > 0:
                        text.Append(xml.Value);
                        break;
                }
            }
            if (text.Length > limit) { text.Length = limit; truncated = true; }
            return new ExtractedText(text.ToString(), null, truncated);
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException or NotSupportedException)
        {
            return ExtractedText.Skip("unreadable document");
        }
    }

    // Stops reading past a fixed number of decompressed bytes.
    private sealed class BoundedStream(Stream inner, long max) : Stream
    {
        private long _read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, (int)Math.Min(count, Math.Max(0, max - _read + 1)));
            _read += n;
            if (_read > max) throw new IOException("Document part exceeds the size limit.");
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}

// The pluggable list of extractors. Types listed in Unsupported are known
// documents that are reported as "not indexed (type)" rather than ignored.
public sealed class TextExtractorRegistry
{
    private readonly Dictionary<string, ITextExtractor> _byExtension = new(StringComparer.OrdinalIgnoreCase);
    public static readonly string[] Unsupported = [".pdf", ".doc", ".rtf", ".xlsx", ".xls", ".ods", ".pptx", ".ppt", ".odp", ".epub"];

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
            ".swift", ".scala", ".hs", ".ml", ".r", ".jl", ".css", ".scss", ".html", ".vue", ".svelte", ".qml",
            ".el", ".clj", ".ex", ".exs", ".dart", ".pl", ".ps1", ".zig", ".cmake", ".gradle", ".m", ".mm"),
        OfficeXmlExtractor.Docx(),
        OfficeXmlExtractor.Odt()
    ]);

    public ITextExtractor? For(string path) => _byExtension.GetValueOrDefault(Path.GetExtension(path));

    // null means "not a document type at all": not counted, not reported.
    public static string? UnsupportedReason(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return Unsupported.Contains(extension) ? $"not indexed ({extension.TrimStart('.')})" : null;
    }
}

public static class SearchFileReader
{
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
