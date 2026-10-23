using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

// Deterministic stand-in for a local embedding model: words in the same
// concept group share a dimension, so "bill" lands near "invoice" although
// the two share no letters. Other words hash into their own dimensions.
public sealed class FakeEmbedder : IEmbedder
{
    private static readonly string[][] Concepts =
    [
        ["invoice", "bill", "receipt", "payment"],
        ["holiday", "vacation", "trip", "travel"],
        ["dog", "puppy", "hound"]
    ];
    public const int Dimensions = 64;
    public string? Model { get; set; } = "fake-embed";
    public bool Available { get; set; } = true;
    public int DocumentCalls { get; private set; }
    public int QueryCalls { get; private set; }

    public static float[] Vector(string text)
    {
        var v = new float[Dimensions];
        foreach (var word in SearchQuery.Terms(text))
        {
            var concept = Array.FindIndex(Concepts, c => c.Contains(word));
            var slot = concept >= 0 ? concept : 3 + (int)((uint)word.Aggregate(17, (h, c) => h * 31 + c) % (Dimensions - 3));
            v[slot] += 1;
        }
        return v;
    }

    public Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        DocumentCalls++;
        if (!Available) throw new EmbeddingUnavailableException("Local Ollama is unreachable.");
        return Task.FromResult(texts.Select(Vector).ToArray());
    }

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
    {
        QueryCalls++;
        if (!Available) throw new EmbeddingUnavailableException("Local Ollama is unreachable.");
        return Task.FromResult(Vector(query));
    }
}

public sealed class SearchFixture : IDisposable
{
    public TempState State { get; } = new();
    public string Home { get; } = Path.Combine(Path.GetTempPath(), "nexus-home-" + Guid.NewGuid().ToString("N"));
    public FakeEmbedder Embedder { get; } = new();
    public IConfiguration Config { get; }
    public SearchSourceStore Sources { get; }
    public SearchIndexStore Store { get; }
    public SearchIndexer Indexer { get; }
    public SearchQuery Query { get; }

    public SearchFixture(params (string Key, string? Value)[] settings)
    {
        Directory.CreateDirectory(Home);
        Config = State.Config([("NEXUS_SEARCH_HOME", Home), ("NEXUS_SEARCH_MIN_SIMILARITY", "0.3"), .. settings]);
        var paths = new NexusStatePaths(Config);
        Sources = new SearchSourceStore(new SqliteStateDatabase(paths), paths, Config);
        Store = new SearchIndexStore(paths);
        var searchSettings = new SearchSettings(Config);
        Indexer = new SearchIndexer(Store, Sources, TextExtractorRegistry.Default(), Embedder, searchSettings,
            NullLogger<SearchIndexer>.Instance);
        Query = new SearchQuery(Store, Embedder, searchSettings);
    }

    public string Folder(string relative)
    {
        var path = Path.Combine(Home, relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public string Write(string relative, string content, DateTime? modified = null)
    {
        var path = Path.Combine(Home, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (modified is { } at) File.SetLastWriteTimeUtc(path, at);
        return path;
    }

    public string Choose(string relative)
    {
        var folder = Folder(relative);
        var (_, refused) = Sources.Add([folder]);
        Assert.Empty(refused);
        return folder;
    }

    public Task<SearchScanReport> ScanAsync() => Indexer.RunScanAsync(CancellationToken.None);

    public Task<SearchResponse> SearchAsync(string query, int? limit = null, params string[] kinds) =>
        Query.SearchAsync(new SearchRequest(query, limit, kinds.Length == 0 ? null : kinds), CancellationToken.None);

    public IEnumerable<byte[]> IndexFiles() =>
        Directory.GetFiles(State.Root, "search.sqlite3*").Select(File.ReadAllBytes);

    public void Dispose()
    {
        Indexer.Dispose();
        State.Dispose();
        if (Directory.Exists(Home)) Directory.Delete(Home, true);
    }
}

public sealed class SearchSourceTests
{
    [Fact]
    public void FolderInsideHomeIsAcceptedAndPersists()
    {
        using var f = new SearchFixture();
        var docs = f.Folder("Documents");
        var (list, refused) = f.Sources.Add([docs + "/"]);
        Assert.Empty(refused);
        Assert.Equal([docs], list);
        var paths = new NexusStatePaths(f.Config);
        Assert.Equal([docs], new SearchSourceStore(new SqliteStateDatabase(paths), paths, f.Config).List());
        // Adding it again does not duplicate it.
        Assert.Single(f.Sources.Add([docs]).Sources);
    }

    [Theory]
    [InlineData(".ssh")]
    [InlineData(".gnupg")]
    [InlineData(".local/share/keyrings")]
    [InlineData(".mozilla/firefox")]
    [InlineData(".config/google-chrome")]
    [InlineData("Documents/.secret")]
    [InlineData("snapdata/google-chrome")]
    [InlineData("Backups/keyrings")]
    [InlineData("Old/BraveSoftware")]
    public void HiddenAndCredentialFoldersAreRefused(string relative)
    {
        using var f = new SearchFixture();
        var (list, refused) = f.Sources.Add([f.Folder(relative)]);
        Assert.Empty(list);
        Assert.Contains("never searched", Assert.Single(refused).Reason);
    }

    [Fact]
    public void PathsOutsideHomeRelativeOrMissingAreRefused()
    {
        using var f = new SearchFixture();
        var outside = Path.Combine(Path.GetTempPath(), "nexus-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Assert.Contains("inside your home", f.Sources.Validate(outside).Reason);
            Assert.Contains("inside your home", f.Sources.Validate("/etc").Reason);
            Assert.Contains("inside your home", f.Sources.Validate(f.Home + "/../" + Path.GetFileName(outside)).Reason);
            Assert.Contains("inside your home", f.Sources.Validate(f.Home + "-sibling").Reason);
            Assert.Contains("absolute", f.Sources.Validate("Documents").Reason);
            Assert.Contains("absolute", f.Sources.Validate("~/Documents").Reason);
            Assert.Contains("absolute", f.Sources.Validate("").Reason);
            Assert.Contains("absolute", f.Sources.Validate(null).Reason);
            Assert.Contains("does not exist", f.Sources.Validate(Path.Combine(f.Home, "Nope")).Reason);
        }
        finally { Directory.Delete(outside); }
    }

    [Fact]
    public void SymlinkedFolderIsRefusedEvenWhenItPointsInsideHome()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new SearchFixture();
        var ssh = f.Folder(".ssh");
        Directory.CreateSymbolicLink(Path.Combine(f.Home, "Keys"), ssh);
        Assert.Contains("symbolic link", f.Sources.Validate(Path.Combine(f.Home, "Keys")).Reason);
        Directory.CreateSymbolicLink(Path.Combine(f.Home, "Etc"), "/etc");
        Assert.Contains("symbolic link", f.Sources.Validate(Path.Combine(f.Home, "Etc")).Reason);
    }

    [Fact]
    public void NexusStateFolderInsideHomeIsRefused()
    {
        var home = Path.Combine(Path.GetTempPath(), "nexus-home-" + Guid.NewGuid().ToString("N"));
        var state = Path.Combine(home, "nexus-state");
        try
        {
            var config = TestConfig.Of(("NEXUS_STATE_DIR", state), ("NEXUS_SEARCH_HOME", home));
            var paths = new NexusStatePaths(config);
            var sources = new SearchSourceStore(new SqliteStateDatabase(paths), paths, config);
            Assert.Contains("state folder", sources.Validate(state).Reason);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void OneRefusedPathMeansNothingIsAdded()
    {
        using var f = new SearchFixture();
        var (list, refused) = f.Sources.Add([f.Folder("Documents"), "/etc", f.Folder(".ssh")]);
        Assert.Empty(list);
        Assert.Equal(2, refused.Count);
        Assert.Empty(f.Sources.List());
    }

    [Fact]
    public void SourceCountIsBounded()
    {
        using var f = new SearchFixture();
        var folders = Enumerable.Range(0, SearchSourceStore.MaxSources + 1).Select(i => f.Folder("d" + i)).ToArray();
        Assert.Empty(f.Sources.Add(folders[..SearchSourceStore.MaxSources]).Refused);
        Assert.NotEmpty(f.Sources.Add([folders[^1]]).Refused);
        Assert.Equal(SearchSourceStore.MaxSources, f.Sources.List().Count);
    }
}

public sealed class SearchExtractionTests
{
    private static ExtractedText Extract(string name, byte[] bytes, long max = 1 << 20) =>
        SearchFormatTests.Extract(name, bytes, ExtractionLimits.For(max));

    private static byte[] Zip(string entry, string xml)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry(entry).Open()))
            writer.Write(xml);
        return buffer.ToArray();
    }

    [Theory]
    [InlineData("notes.txt", "text")]
    [InlineData("README.md", "text")]
    [InlineData("prices.csv", "data")]
    [InlineData("config.JSON", "data")]
    [InlineData("main.py", "code")]
    [InlineData("Program.cs", "code")]
    [InlineData("report.docx", "document")]
    [InlineData("essay.odt", "document")]
    [InlineData("paper.pdf", "document")]
    [InlineData("budget.xlsx", "data")]
    [InlineData("budget.ods", "data")]
    [InlineData("talk.pptx", "document")]
    [InlineData("talk.odp", "document")]
    [InlineData("letter.rtf", "document")]
    [InlineData("novel.epub", "document")]
    [InlineData("page.html", "document")]
    [InlineData("page.htm", "document")]
    [InlineData("message.eml", "mail")]
    public void KnownTypesHaveAnExtractor(string name, string kind) =>
        Assert.Equal(kind, TextExtractorRegistry.Default().For(name)!.Kind);

    [Theory]
    [InlineData("old.doc", "not indexed (doc)")]
    [InlineData("old.xls", "not indexed (xls)")]
    [InlineData("old.ppt", "not indexed (ppt)")]
    [InlineData("photo.jpg", null)]
    [InlineData("id_rsa", null)]
    public void OtherTypesAreReportedOrIgnored(string name, string? reason)
    {
        Assert.Null(TextExtractorRegistry.Default().For(name));
        Assert.Equal(reason, TextExtractorRegistry.UnsupportedReason(name));
    }

    [Fact]
    public void PlainTextDropsTheBomAndRejectsBinary()
    {
        Assert.Equal("héllo", Extract("a.txt", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("héllo")]).Text);
        var binary = Extract("a.txt", [0x41, 0x00, 0x42]);
        Assert.Null(binary.Text);
        Assert.Equal("not indexed (txt: binary content)", binary.Skipped);
    }

    [Fact]
    public void PlainTextIsCappedAndMarkedTruncated()
    {
        var result = Extract("a.txt", Encoding.UTF8.GetBytes(new string('x', 5000)), max: 1000);
        Assert.Equal(1000, result.Text!.Length);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void DocxTextRunsAndParagraphsAreExtracted()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:r><w:t>Supplier</w:t></w:r><w:r><w:t xml:space="preserve"> prices for March</w:t></w:r></w:p>
                <w:p><w:r><w:t>Second</w:t><w:tab/><w:t>paragraph</w:t></w:r></w:p>
              </w:body>
            </w:document>
            """;
        var text = Extract("prices.docx", Zip("word/document.xml", xml)).Text!;
        Assert.Equal("Supplier prices for March\nSecond\tparagraph\n", text);
    }

    [Fact]
    public void OdtParagraphsHeadingsAndSpansAreExtracted()
    {
        var xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0">
              <office:body><office:text>
                <text:h>Holiday plan</text:h>
                <text:p>Book the <text:span>train</text:span> early<text:s/>please</text:p>
              </office:text></office:body>
            </office:document-content>
            """;
        var text = Extract("plan.odt", Zip("content.xml", xml)).Text!;
        Assert.Equal("Holiday plan\nBook the train early please\n", text);
    }

    [Fact]
    public void BrokenOrBombedDocumentsAreSkippedNotFatal()
    {
        Assert.Equal("not indexed (docx: unreadable)", Extract("x.docx", Encoding.UTF8.GetBytes("not a zip")).Skipped);
        Assert.Equal("not indexed (docx: unreadable)", Extract("x.docx", Zip("other.xml", "<a/>")).Skipped);
        // A DTD (billion laughs) is refused by the XML reader settings.
        Assert.Equal("not indexed (docx: unreadable)", Extract("x.docx", Zip("word/document.xml",
            "<?xml version=\"1.0\"?><!DOCTYPE a [<!ENTITY e \"x\">]><a>&e;</a>")).Skipped);
        // A tiny archive that inflates past the limit stops at the limit.
        var bomb = Zip("word/document.xml", "<a>" + new string(' ', 80 * 1024 * 1024) + "</a>");
        Assert.True(bomb.Length < 200_000);
        Assert.Equal("not indexed (docx: too large when unpacked)", Extract("x.docx", bomb, max: 1 << 20).Skipped);
    }

    [Fact]
    public void ChunksCoverTheTextWithAccurateOffsetsAndLines()
    {
        var lines = Enumerable.Range(1, 1000).Select(i => $"line {i} " + new string('w', i % 17)).ToArray();
        var text = string.Join('\n', lines);
        var chunks = TextChunker.Split(text, 1000);
        Assert.True(chunks.Count > 5);
        foreach (var c in chunks)
        {
            Assert.True(c.Length <= TextChunker.MaxChars);
            Assert.StartsWith(c.Text, text.Substring(c.Start, c.Length));
            Assert.Equal(c.FirstLine.ToString(), c.Text.Split('\n')[0].Split(' ')[1]);
            Assert.Equal(c.LastLine.ToString(), c.Text.Split('\n')[^1].Split(' ')[1]);
        }
        Assert.Equal(1, chunks[0].FirstLine);
        Assert.Equal(1000, chunks[^1].LastLine);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Ordinal));
    }

    [Fact]
    public void ChunkingIsBoundedAndHandlesTextWithoutBreaks()
    {
        var chunks = TextChunker.Split(new string('a', 100_000), 10);
        Assert.Equal(10, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(TextChunker.MaxChars, c.Length));
        Assert.Empty(TextChunker.Split("   \n\n  ", 10));
    }
}

public sealed class SearchIndexingTests
{
    [Fact]
    public async Task NothingIsIndexedUntilAFolderIsChosen()
    {
        using var f = new SearchFixture();
        f.Write("Documents/notes.txt", "supplier prices");
        var report = await f.ScanAsync();
        Assert.Equal(0, report.Indexed);
        Assert.Equal(0, f.Store.Counts(null).Files);
    }

    [Fact]
    public async Task FullTextSearchFindsTheFileAndExplainsWhy()
    {
        using var f = new SearchFixture();
        f.Embedder.Model = null;
        f.Choose("Documents");
        f.Write("Documents/suppliers.csv", "item,price\nflour,2.10\nThe supplier prices from March are final.");
        f.Write("Documents/other.md", "Nothing relevant here about the garden.");
        f.Write("Documents/code/main.py", "def supplier():\n    return 'acme'\n");
        var report = await f.ScanAsync();
        Assert.Equal(3, report.Indexed);

        var result = await f.SearchAsync("supplier prices");
        Assert.Equal("text", result.Mode);
        Assert.False(result.MeaningSearch);
        var top = result.Results[0];
        Assert.Equal(Path.Combine(f.Home, "Documents/suppliers.csv"), top.Path);
        Assert.Equal("suppliers.csv", top.Title);
        Assert.Equal("data", top.Kind);
        Assert.Contains("«", top.Snippet);
        Assert.Equal(["supplier", "prices"], top.MatchedTerms);
        Assert.Equal("Contains \"supplier\", \"prices\", in lines 1–3.", top.Why);
        Assert.Null(top.Similarity);
        Assert.DoesNotContain(result.Results, r => r.Title == "other.md");

        var code = await f.SearchAsync("supplier", kinds: "code");
        Assert.Equal("main.py", Assert.Single(code.Results).Title);
    }

    [Fact]
    public async Task StemmingAndFileNamesMatch()
    {
        using var f = new SearchFixture();
        f.Embedder.Model = null;
        f.Choose("Documents");
        f.Write("Documents/march-invoices.txt", "Totals for the quarter.");
        await f.ScanAsync();
        var result = await f.SearchAsync("invoice");
        Assert.Equal("march-invoices.txt", Assert.Single(result.Results).Title);
        Assert.Equal(["invoice"], result.Results[0].MatchedTerms);
    }

    [Fact]
    public async Task MeaningSearchFindsPassagesWithNoWordsInCommon()
    {
        using var f = new SearchFixture();
        f.Choose("Documents");
        f.Write("Documents/money.txt", "The invoice from the plumber is overdue.");
        f.Write("Documents/pets.txt", "Walk the puppy twice a day.");
        var report = await f.ScanAsync();
        Assert.Equal(2, report.Embedded);
        Assert.Equal(2, f.Store.Counts("fake-embed").EmbeddedChunks);

        var result = await f.SearchAsync("bill");
        Assert.Equal("hybrid", result.Mode);
        Assert.True(result.MeaningSearch);
        Assert.Null(result.Notice);
        var top = result.Results[0];
        Assert.Equal("money.txt", top.Title);
        Assert.Empty(top.MatchedTerms);
        Assert.StartsWith("Close in meaning to your search (similarity 0.", top.Why);
        Assert.Equal("The invoice from the plumber is overdue.", top.Snippet);
        Assert.DoesNotContain(result.Results, r => r.Title == "pets.txt");

        // Words and meaning together rank a passage with both above either alone.
        f.Write("Documents/both.txt", "Plumber bill: the invoice total.");
        await f.ScanAsync();
        var both = await f.SearchAsync("plumber bill");
        Assert.Equal("both.txt", both.Results[0].Title);
        Assert.Contains("contains \"plumber\", \"bill\"", both.Results[0].Why, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("close in meaning", both.Results[0].Why);
    }

    [Fact]
    public async Task UnavailableEmbeddingsFallBackToWordsAndSaySo()
    {
        using var f = new SearchFixture();
        f.Embedder.Available = false;
        f.Choose("Documents");
        f.Write("Documents/money.txt", "The invoice from the plumber is overdue.");
        var report = await f.ScanAsync();
        Assert.Equal(1, report.Indexed);
        Assert.Equal(0, report.Embedded);
        var status = f.Indexer.Status();
        Assert.False(status.Embeddings.Available);
        Assert.Equal("Local Ollama is unreachable.", status.Embeddings.Reason);
        Assert.Equal(1, status.Files);

        var result = await f.SearchAsync("plumber");
        Assert.Equal("text", result.Mode);
        Assert.Contains("words only", result.Notice);
        Assert.Equal("money.txt", Assert.Single(result.Results).Title);

        // When the model comes back, the next scan fills in the missing embeddings.
        f.Embedder.Available = true;
        Assert.Equal(1, (await f.ScanAsync()).Embedded);
        Assert.True(f.Indexer.Status().Embeddings.Available);
        Assert.Equal("hybrid", (await f.SearchAsync("bill")).Mode);

        // And if it goes away again at query time, search still answers.
        f.Embedder.Available = false;
        var later = await f.SearchAsync("plumber");
        Assert.Equal("text", later.Mode);
        Assert.Single(later.Results);
    }

    [Fact]
    public async Task ChangedAndDeletedFilesAreDetected()
    {
        using var f = new SearchFixture();
        f.Embedder.Model = null;
        f.Choose("Notes");
        var path = f.Write("Notes/a.txt", "alpha version", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        f.Write("Notes/b.txt", "bravo stays");
        Assert.Equal(2, (await f.ScanAsync()).Indexed);

        var again = await f.ScanAsync();
        Assert.Equal((0, 0, 2), (again.Indexed, again.Updated, again.Unchanged));

        File.WriteAllText(path, "gamma version");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        var changed = await f.ScanAsync();
        Assert.Equal((1, 1), (changed.Updated, changed.Unchanged));
        Assert.Empty((await f.SearchAsync("alpha")).Results);
        var hit = Assert.Single((await f.SearchAsync("gamma")).Results);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), hit.Modified);

        File.Delete(path);
        var deleted = await f.ScanAsync();
        Assert.Equal(1, deleted.Removed);
        Assert.Empty((await f.SearchAsync("gamma")).Results);
        Assert.Equal(1, f.Store.Counts(null).Files);
        // Removed words leave the database file, not just the results.
        Assert.All(f.IndexFiles(), bytes => Assert.DoesNotContain("gamma", Encoding.UTF8.GetString(bytes)));
    }

    [Fact]
    public async Task ForcedReindexRereadsEverything()
    {
        using var f = new SearchFixture();
        f.Embedder.Model = null;
        f.Choose("Notes");
        f.Write("Notes/a.txt", "alpha");
        await f.ScanAsync();
        f.Indexer.RequestScan(force: true);
        var forced = await f.ScanAsync();
        Assert.True(forced.Forced);
        Assert.Equal(1, forced.Updated);
    }

    [Fact]
    public async Task HiddenEntriesSymlinksAndUnsupportedTypesAreNotIndexed()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new SearchFixture();
        f.Embedder.Model = null;
        f.Choose("Work");
        f.Write("Work/.env", "secretword");
        f.Write("Work/.git/notes.txt", "secretword");
        f.Write("Work/sub/.ssh/notes.txt", "secretword");
        f.Write("Work/keyrings/notes.txt", "secretword");
        var outside = f.Write(".ssh/id.txt", "secretword");
        File.CreateSymbolicLink(Path.Combine(f.Home, "Work/link.txt"), outside);
        Directory.CreateSymbolicLink(Path.Combine(f.Home, "Work/linkdir"), Path.GetDirectoryName(outside)!);
        f.Write("Work/paper.doc", "secretword");
        f.Write("Work/broken.pdf", "%PDF-1.4 secretword");
        f.Write("Work/photo.png", "secretword");
        f.Write("Work/visible.txt", "publicword");

        var report = await f.ScanAsync();
        Assert.Equal(1, report.Indexed);
        Assert.Equal(1, report.Skipped["not indexed (doc)"]);
        Assert.Equal(1, report.Skipped["not indexed (pdf: unreadable)"]);
        Assert.Contains(report.SkippedExamples, x => x.Path.EndsWith("paper.doc") && x.Reason == "not indexed (doc)");
        Assert.Empty((await f.SearchAsync("secretword")).Results);
        Assert.Single((await f.SearchAsync("publicword")).Results);
        Assert.All(f.IndexFiles(), bytes => Assert.DoesNotContain("secretword", Encoding.UTF8.GetString(bytes)));
    }

    [Fact]
    public async Task FifoIsSkippedWithoutBlockingTheScan()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var f = new SearchFixture();
        f.Embedder.Model = null;
        f.Choose("Pipes");
        var fifo = Path.Combine(f.Home, "Pipes/pipe.txt");
        using (var mk = Process.Start("mkfifo", fifo)) mk.WaitForExit();
        f.Write("Pipes/real.txt", "content");
        var report = await f.ScanAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, report.Indexed);
        Assert.Equal(1, report.Skipped["not a regular file"]);
    }

    [Fact]
    public async Task OversizedFilesAndFileLimitsAreBounded()
    {
        using var f = new SearchFixture(("NEXUS_SEARCH_MAX_FILE_BYTES", "2048"), ("NEXUS_SEARCH_MAX_FILES", "3"));
        f.Embedder.Model = null;
        f.Choose("Big");
        f.Write("Big/huge.txt", new string('z', 4096));
        for (var i = 0; i < 2; i++) f.Write($"Big/small{i}.txt", "small file " + i);
        var report = await f.ScanAsync();
        Assert.Equal(2, report.Indexed);
        Assert.Equal(1, report.Skipped["too large"]);
        Assert.True(report.Complete);

        for (var i = 2; i < 6; i++) f.Write($"Big/small{i}.txt", "small file " + i);
        var limited = await f.ScanAsync();
        Assert.False(limited.Complete);
        Assert.Equal(0, limited.Removed); // A partial walk never counts unseen files as deleted.
        Assert.Contains(f.Indexer.Status().Errors, e => e.Reason.Contains("file limit"));
        Assert.True(f.Store.Counts(null).Files <= 3);
    }

    [Fact]
    public async Task ChunkBudgetStopsTheIndexGrowing()
    {
        using var f = new SearchFixture(("NEXUS_SEARCH_MAX_CHUNKS", "2"), ("NEXUS_SEARCH_WORKERS", "1"));
        f.Embedder.Model = null;
        f.Choose("Many");
        for (var i = 0; i < 5; i++) f.Write($"Many/f{i}.txt", "words " + i);
        var report = await f.ScanAsync();
        Assert.Equal(2, report.Indexed);
        Assert.Equal(3, report.Skipped["index full"]);
    }

    [Fact]
    public async Task RemovingAFolderRemovesItsEntriesFromTheFile()
    {
        using var f = new SearchFixture();
        var keep = f.Choose("Keep");
        var drop = f.Choose("Drop");
        f.Write("Keep/a.txt", "keepword");
        f.Write("Drop/b.txt", "dropword-" + new string('q', 50));
        await f.ScanAsync();
        Assert.Contains(f.IndexFiles(), bytes => Encoding.UTF8.GetString(bytes).Contains("dropword"));

        var removed = await f.Indexer.ExclusiveAsync(() =>
        {
            Assert.True(f.Sources.Remove(drop, out _));
            return f.Store.RemoveSource(drop);
        }, CancellationToken.None);
        Assert.Equal(1, removed);
        Assert.Empty((await f.SearchAsync("dropword")).Results);
        Assert.Single((await f.SearchAsync("keepword")).Results);
        Assert.All(f.IndexFiles(), bytes => Assert.DoesNotContain("dropword", Encoding.UTF8.GetString(bytes)));
        Assert.Equal([keep], f.Sources.List());
    }

    [Fact]
    public async Task EraseLeavesNoContentInTheDatabaseFile()
    {
        using var f = new SearchFixture();
        f.Choose("Docs");
        var marker = "erase-me-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 20; i++) f.Write($"Docs/f{i}.txt", marker + " passage " + i);
        await f.ScanAsync();
        Assert.Contains(f.IndexFiles(), bytes => Encoding.UTF8.GetString(bytes).Contains(marker));
        Assert.True(f.Store.Erase());
        Assert.Equal(new SearchCounts(0, 0, 0), f.Store.Counts("fake-embed"));
        Assert.All(f.IndexFiles(), bytes => Assert.DoesNotContain(marker, Encoding.UTF8.GetString(bytes)));
        Assert.Empty((await f.SearchAsync(marker)).Results);
    }

    [Fact]
    public void IndexFileIsPrivateAndCannotBeASymlink()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new SearchFixture();
        var file = Path.Combine(f.State.Root, "search.sqlite3");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        using (f.Store.Open()) { }
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        using var state = new TempState();
        Directory.CreateDirectory(state.Root);
        var elsewhere = Path.Combine(Path.GetTempPath(), "nexus-redirect-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(elsewhere, "");
        try
        {
            File.CreateSymbolicLink(Path.Combine(state.Root, "search.sqlite3"), elsewhere);
            Assert.Throws<IOException>(() => new SearchIndexStore(state.Paths()));
        }
        finally { File.Delete(elsewhere); }
    }

    [Theory]
    [InlineData("", null, null)]
    [InlineData("   ", null, null)]
    [InlineData("ok", 0, null)]
    [InlineData("ok", 51, null)]
    [InlineData("ok", 10, "video")]
    public void InvalidQueriesAreRejected(string query, int? limit, string? kind) =>
        Assert.NotNull(SearchQuery.Validate(new SearchRequest(query, limit, kind is null ? null : [kind])));

    [Fact]
    public void QueryLengthAndTermsAreBounded()
    {
        Assert.NotNull(SearchQuery.Validate(new SearchRequest(new string('a', 513), null, null)));
        Assert.Null(SearchQuery.Validate(new SearchRequest(new string('a', 512), 50, ["text", "code"])));
        var terms = SearchQuery.Terms(string.Join(' ', Enumerable.Range(0, 40).Select(i => "w" + i)) + " \"quoted\" OR NEAR(x)");
        Assert.Equal(SearchSettings.MaxQueryTerms, terms.Count);
        // FTS syntax in a query is treated as words, never as operators.
        Assert.Equal(["quoted", "or", "near", "x"], SearchQuery.Terms("\"quoted\" OR NEAR(x)"));
    }

    [Fact]
    public async Task FtsSyntaxInQueriesCannotBreakSearch()
    {
        using var f = new SearchFixture();
        f.Embedder.Model = null;
        f.Choose("D");
        f.Write("D/a.txt", "plain words");
        foreach (var q in new[] { "\"", "a*", "NOT", "(", "title:x", "^", "-", "*" })
            Assert.NotNull(await f.SearchAsync(q));
    }

    [Fact]
    public async Task ResultsAreLimitedAndOnePerFile()
    {
        using var f = new SearchFixture();
        f.Embedder.Model = null;
        f.Choose("D");
        for (var i = 0; i < 8; i++) f.Write($"D/f{i}.txt", string.Join("\n\n", Enumerable.Repeat("needle " + new string('h', 600), 6)));
        await f.ScanAsync();
        Assert.True(f.Store.Counts(null).Chunks > 8);
        var result = await f.SearchAsync("needle", limit: 5);
        Assert.Equal(5, result.Results.Count);
        Assert.Equal(5, result.Results.Select(r => r.Path).Distinct().Count());
    }
}

public sealed class SearchEmbedderTests
{
    [Fact]
    public void EmbedderOnlyAcceptsLoopbackAndValidModelNames()
    {
        var factory = new StubHttpFactory(StubHandler.Json(HttpStatusCode.OK, "{}"));
        Assert.Throws<InvalidOperationException>(() => new OllamaEmbedder(factory, TestConfig.Of(("NEXUS_OLLAMA_URL", "http://example.com:11434"))));
        Assert.Throws<InvalidOperationException>(() => new OllamaEmbedder(factory, TestConfig.Of(("NEXUS_OLLAMA_URL", "https://api.openai.com/v1"))));
        Assert.Throws<InvalidOperationException>(() => new OllamaEmbedder(factory, TestConfig.Of(("NEXUS_EMBED_MODEL", "bad model; rm"))));
        Assert.Equal("nomic-embed-text", new OllamaEmbedder(factory, TestConfig.Of()).Model);
        Assert.Null(new OllamaEmbedder(factory, TestConfig.Of(("NEXUS_EMBED_MODEL", "off"))).Model);
    }

    [Fact]
    public async Task EmbedderPostsToLocalOllamaWithTaskPrefixes()
    {
        var stub = StubHandler.Json(HttpStatusCode.OK, """{"embeddings":[[0.1,0.2],[0.3,0.4]]}""");
        var embedder = new OllamaEmbedder(new StubHttpFactory(stub), TestConfig.Of(("NEXUS_OLLAMA_URL", "http://127.0.0.1:11435/")));
        var vectors = await embedder.EmbedDocumentsAsync(["one", "two"], CancellationToken.None);
        Assert.Equal([0.3f, 0.4f], vectors[1]);
        Assert.Equal("http://127.0.0.1:11435/api/embed", stub.Requests[0].RequestUri!.ToString());
        using var body = JsonDocument.Parse(stub.Bodies[0]);
        Assert.Equal("nomic-embed-text", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("search_document: one", body.RootElement.GetProperty("input")[0].GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}", "ollama pull")]
    [InlineData(HttpStatusCode.InternalServerError, "{}", "HTTP 500")]
    [InlineData(HttpStatusCode.OK, "{\"embeddings\":[]}", "unusable")]
    [InlineData(HttpStatusCode.OK, "not json", "unusable")]
    [InlineData(HttpStatusCode.OK, "{\"embeddings\":[[1,2],[3]]}", "unusable")]
    public async Task EmbedderFailuresBecomeSafeMessages(HttpStatusCode status, string body, string expected)
    {
        var embedder = new OllamaEmbedder(new StubHttpFactory(StubHandler.Json(status, body)), TestConfig.Of());
        var error = await Assert.ThrowsAsync<EmbeddingUnavailableException>(() => embedder.EmbedDocumentsAsync(["a", "b"], CancellationToken.None));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void PackedVectorsKeepCosineRanking()
    {
        var a = Vectors.Pack([3, 4]);
        Assert.Equal(1f, Vectors.Cosine(Vectors.Normalise([3, 4]), a), 2);
        Assert.Equal(0f, Vectors.Cosine(Vectors.Normalise([-4, 3]), a), 2);
        Assert.True(float.IsNaN(Vectors.Cosine([1, 0, 0], a)));
    }
}

// The real Core pipeline: auth, rate limits, the hosted indexer and the
// Ollama embeddings client against a stubbed loopback model.
public sealed class SearchApiTests
{
    private static StubHandler OllamaWithEmbeddings(bool embeddings = true) => new((request, _) =>
    {
        if (request.RequestUri!.AbsolutePath == "/api/embed" && embeddings)
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
            var inputs = body.RootElement.GetProperty("input").EnumerateArray().Select(x => x.GetString()!);
            var vectors = inputs.Select(x => FakeEmbedder.Vector(x.Replace("search_document: ", "").Replace("search_query: ", "")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { embeddings = vectors }))
            });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
    });

    private static async Task<JsonElement> WaitForStatus(HttpClient client, Func<JsonElement, bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var status = await client.GetFromJsonAsync<JsonElement>("/search/status");
            if (!status.GetProperty("running").GetBoolean() && done(status)) return status;
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(status.ToString());
            await Task.Delay(100);
        }
    }

    private sealed class ApiHome : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "nexus-home-" + Guid.NewGuid().ToString("N"));
        public ApiHome() => Directory.CreateDirectory(Root);
        public string Write(string relative, string content)
        {
            var path = Path.Combine(Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }

    [Fact]
    public async Task ChooseIndexSearchRemoveAndEraseThroughTheApi()
    {
        using var home = new ApiHome();
        var marker = "confidential-" + Guid.NewGuid().ToString("N");
        home.Write("Documents/money.txt", $"The invoice {marker} from the plumber.");
        home.Write("Documents/pets.txt", "Walk the puppy.");
        var ai = OllamaWithEmbeddings();
        var cloud = StubHandler.Json(HttpStatusCode.OK, "{}");
        using var factory = new CoreApiFactory(("NEXUS_SEARCH_HOME", home.Root), ("NEXUS_EMBED_MODEL", "test-embed"),
            ("NEXUS_SEARCH_MIN_SIMILARITY", "0.3"), ("NEXUS_CLOUD_EGRESS_ALLOWED", "true"))
            { AiHandler = ai, CloudHandler = cloud };
        var client = factory.Authorized();
        // Even with cloud chat selected and allowed, indexing stays local.
        (await client.PostAsJsonAsync("/providers/select", new { name = "openai-compatible" })).EnsureSuccessStatusCode();

        using (var refused = await client.PostAsJsonAsync("/search/sources", new { paths = new[] { Path.Combine(home.Root, "Documents"), "/etc" } }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("/etc", Assert.Single(body.GetProperty("refused").EnumerateArray().ToArray()).GetProperty("path").GetString());
        }
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/search/sources")).GetProperty("sources").EnumerateArray());

        var docs = Path.Combine(home.Root, "Documents");
        (await client.PostAsJsonAsync("/search/sources", new { paths = new[] { docs } })).EnsureSuccessStatusCode();
        var status = await WaitForStatus(client, s => s.GetProperty("files").GetInt64() == 2 && s.GetProperty("embeddedChunks").GetInt64() == 2);
        Assert.True(status.GetProperty("embeddings").GetProperty("available").GetBoolean());
        Assert.Equal(docs, status.GetProperty("sources")[0].GetProperty("path").GetString());

        using (var search = await client.PostAsJsonAsync("/search", new { query = "bill", limit = 5 }))
        {
            var body = await search.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("hybrid", body.GetProperty("mode").GetString());
            var top = body.GetProperty("results")[0];
            Assert.Equal(Path.Combine(docs, "money.txt"), top.GetProperty("path").GetString());
            Assert.Contains("close in meaning", top.GetProperty("why").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(top.TryGetProperty("modified", out _));
            Assert.True(top.TryGetProperty("snippet", out _));
        }
        Assert.Empty(cloud.Requests);
        Assert.All(ai.Requests, r => Assert.Equal("/api/embed", r.RequestUri!.AbsolutePath));
        Assert.DoesNotContain(marker, factory.Logs.All);
        Assert.DoesNotContain("money.txt", factory.Logs.All);

        using (var reindex = await client.PostAsync("/search/reindex", null))
            Assert.Equal(HttpStatusCode.Accepted, reindex.StatusCode);
        await WaitForStatus(client, s => s.GetProperty("lastRun").GetProperty("forced").GetBoolean());

        using (var missing = await client.DeleteAsync("/search/sources?path=" + Uri.EscapeDataString(Path.Combine(home.Root, "Other"))))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        (await client.PostAsJsonAsync("/search/sources", new { paths = new[] { docs } })).EnsureSuccessStatusCode();

        using (var erase = await client.DeleteAsync("/search/index"))
        {
            erase.EnsureSuccessStatusCode();
            var body = await erase.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.GetProperty("verified").GetBoolean());
            Assert.Equal(1, body.GetProperty("sourcesCleared").GetInt32());
        }
        var after = await client.GetFromJsonAsync<JsonElement>("/search/status");
        Assert.Equal(0, after.GetProperty("files").GetInt64());
        Assert.Empty(after.GetProperty("sources").EnumerateArray());
        foreach (var file in Directory.GetFiles(factory.StateRoot, "search.sqlite3*"))
            Assert.DoesNotContain(marker, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
        using (var gone = await client.PostAsJsonAsync("/search", new { query = marker }))
            Assert.Empty((await gone.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task RemovingASourceThroughTheApiDropsItsResults()
    {
        using var home = new ApiHome();
        home.Write("A/a.txt", "apple orchard");
        home.Write("B/b.txt", "banana orchard");
        using var factory = new CoreApiFactory(("NEXUS_SEARCH_HOME", home.Root), ("NEXUS_EMBED_MODEL", "off"));
        var client = factory.Authorized();
        (await client.PostAsJsonAsync("/search/sources", new { paths = new[] { Path.Combine(home.Root, "A"), Path.Combine(home.Root, "B") } }))
            .EnsureSuccessStatusCode();
        await WaitForStatus(client, s => s.GetProperty("files").GetInt64() == 2);
        using (var remove = await client.DeleteAsync("/search/sources?path=" + Uri.EscapeDataString(Path.Combine(home.Root, "B"))))
        {
            remove.EnsureSuccessStatusCode();
            Assert.Equal(1, (await remove.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removedFiles").GetInt32());
        }
        using var search = await client.PostAsJsonAsync("/search", new { query = "orchard" });
        var body = await search.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("text", body.GetProperty("mode").GetString());
        Assert.Contains("switched off", body.GetProperty("notice").GetString());
        Assert.Equal("a.txt", Assert.Single(body.GetProperty("results").EnumerateArray().ToArray()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task UnavailableOllamaStillIndexesAndSearchesWords()
    {
        using var home = new ApiHome();
        home.Write("Docs/a.md", "kettle descaling notes");
        using var factory = new CoreApiFactory(("NEXUS_SEARCH_HOME", home.Root)) { AiHandler = OllamaWithEmbeddings(embeddings: false) };
        var client = factory.Authorized();
        (await client.PostAsJsonAsync("/search/sources", new { paths = new[] { Path.Combine(home.Root, "Docs") } })).EnsureSuccessStatusCode();
        var status = await WaitForStatus(client, s => s.GetProperty("files").GetInt64() == 1 &&
            s.GetProperty("embeddings").GetProperty("available").ValueKind == JsonValueKind.False);
        Assert.Contains("ollama pull nomic-embed-text", status.GetProperty("embeddings").GetProperty("reason").GetString());
        using var search = await client.PostAsJsonAsync("/search", new { query = "kettle" });
        var body = await search.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("text", body.GetProperty("mode").GetString());
        Assert.Single(body.GetProperty("results").EnumerateArray());
    }

    [Theory]
    [InlineData("""{"query":""}""")]
    [InlineData("""{"query":"ok","limit":0}""")]
    [InlineData("""{"query":"ok","limit":500}""")]
    [InlineData("""{"query":"ok","kinds":["video"]}""")]
    public async Task InvalidSearchRequestsAreRejected(string json)
    {
        using var factory = new CoreApiFactory();
        using var response = await factory.Authorized().PostAsync("/search", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OverlongQueryAndEmptySourceListAreRejected()
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        using (var longQuery = await client.PostAsJsonAsync("/search", new { query = new string('a', 513) }))
            Assert.Equal(HttpStatusCode.BadRequest, longQuery.StatusCode);
        using (var none = await client.PostAsJsonAsync("/search/sources", new { paths = Array.Empty<string>() }))
            Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        using (var noPath = await client.DeleteAsync("/search/sources"))
            Assert.Equal(HttpStatusCode.BadRequest, noPath.StatusCode);
    }

    [Fact]
    public async Task SearchIsRateLimitedAfterSixtyRequestsPerMinute()
    {
        using var factory = new CoreApiFactory(("NEXUS_EMBED_MODEL", "off"));
        var client = factory.Authorized();
        for (var i = 0; i < 60; i++)
        {
            using var ok = await client.PostAsJsonAsync("/search", new { query = "q" + i });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        using var limited = await client.PostAsJsonAsync("/search", new { query = "over" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }
}
