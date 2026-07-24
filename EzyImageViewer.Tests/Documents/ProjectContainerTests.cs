using System.Text;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Documents.Serialization;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

public class ProjectContainerTests
{
    private static EzyProject SampleProject() => new()
    {
        Manifest = ProjectManifest.Create("0.1.0-m0"),
        DocumentJson = """{"annotations":[{"type":"arrow","from":[10,10],"to":[200,120]}]}""",
        PreviewPng = [0x89, 0x50, 0x4E, 0x47],
        Assets = new Dictionary<string, byte[]>
        {
            ["pasted-001.png"] = Encoding.ASCII.GetBytes("fake-png-bytes"),
            ["fonts/substitution.json"] = Encoding.UTF8.GetBytes("{}"),
        },
    };

    [Fact]
    public void RoundTrip_PreservesManifestDocumentPreviewAndAssets()
    {
        var original = SampleProject();
        using var stream = new MemoryStream();

        EzyProjectArchive.Write(stream, original);
        stream.Position = 0;
        var loaded = EzyProjectArchive.Read(stream);

        Assert.Equal(original.Manifest, loaded.Manifest);
        Assert.Equal(original.DocumentJson, loaded.DocumentJson);
        Assert.Equal(original.PreviewPng, loaded.PreviewPng);
        Assert.Equal(original.Assets.Count, loaded.Assets.Count);
        Assert.Equal(original.Assets["pasted-001.png"], loaded.Assets["pasted-001.png"]);
        Assert.Equal(original.Assets["fonts/substitution.json"], loaded.Assets["fonts/substitution.json"]);
    }

    [Fact]
    public void ValidV3Project_DocumentReadsUnderItsManifestVersion()
    {
        var annotation = new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(1, 2, 3, 4),
            StrokeArgb = 0xFF11_2233,
            StrokeWidth = 2f,
        };
        var project = new EzyProject
        {
            Manifest = ProjectManifest.Create("test"),
            DocumentJson = ProjectDocumentSerializer.Write(
                [new ProjectPageState(DocumentState.Empty.AddAnnotation(annotation), AnnotationLayer.InitialLayerId)],
                activePageIndex: 0),
        };
        using var stream = RoundTripStream(project);

        var loaded = EzyProjectArchive.Read(stream);

        Assert.Equal(ProjectManifest.CurrentSchemaVersion, loaded.Manifest.SchemaVersion);
        var document = ProjectDocumentSerializer.Read(loaded.DocumentJson, loaded.Manifest.SchemaVersion);
        Assert.Equal(annotation.Id, Assert.Single(document.Pages[0].State.Annotations).Id);
        Assert.Equal(AnnotationLayer.InitialLayerId, document.Pages[0].ActiveLayerId);
    }

    [Fact]
    public void V3_MultiplePagesRoundTrip_AndV2MigratesToOnePage()
    {
        var first = DocumentState.Empty.AddAnnotation(new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(1, 2, 3, 4),
        });
        var second = DocumentState.Empty.AddAnnotation(new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(5, 6, 7, 8),
        });
        var json = ProjectDocumentSerializer.Write(
            [
                new ProjectPageState(first, AnnotationLayer.InitialLayerId),
                new ProjectPageState(second, AnnotationLayer.InitialLayerId),
            ],
            activePageIndex: 1);

        var v3 = ProjectDocumentSerializer.Read(json, 3);

        Assert.Equal(2, v3.Pages.Count);
        Assert.Equal(1, v3.ActivePageIndex);
        Assert.Equal(second.Annotations[0].Id, v3.Pages[1].State.Annotations[0].Id);

        var v2Json = DocumentStateSerializer.Write(first, AnnotationLayer.InitialLayerId);
        var migrated = ProjectDocumentSerializer.Read(v2Json, 2);
        Assert.Single(migrated.Pages);
        Assert.Equal(0, migrated.ActivePageIndex);
        Assert.Equal(first.Annotations[0].Id, migrated.Pages[0].State.Annotations[0].Id);
    }

    [Fact]
    public void V3_RejectsInvalidActivePageAndUnknownMembers()
    {
        var fragment = DocumentStateSerializer.Write(DocumentState.Empty);
        var invalidIndex = $$"""{"activePageIndex":1,"pages":[{"document":{{fragment}}}]}""";
        var unknown = $$"""{"activePageIndex":0,"pages":[{"document":{{fragment}}}],"extra":true}""";

        Assert.Throws<InvalidDataException>(() => ProjectDocumentSerializer.Read(invalidIndex, 3));
        Assert.Throws<InvalidDataException>(() => ProjectDocumentSerializer.Read(unknown, 3));
    }

    [Fact]
    public void EmbeddedSource_RoundTrips_AndRejectsHostileNames()
    {
        var project = new EzyProject
        {
            Manifest = ProjectManifest.Create("test"),
            DocumentJson = "{}",
            SourceName = "원본 사진.jpg",
            SourceBytes = [0xFF, 0xD8, 0xFF],
        };
        using var stream = RoundTripStream(project);
        var loaded = EzyProjectArchive.Read(stream);

        Assert.Equal("원본 사진.jpg", loaded.SourceName);
        Assert.Equal(project.SourceBytes, loaded.SourceBytes);

        // 경로 순회 이름은 쓰기에서 거절. 이름 없는 바이트 프로젝트는 존재 불가.
        using var refused = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => EzyProjectArchive.Write(refused, new EzyProject
        {
            Manifest = ProjectManifest.Create("test"),
            DocumentJson = "{}",
            SourceName = "../evil.png",
            SourceBytes = [1],
        }));
        Assert.Throws<InvalidDataException>(() => EzyProjectArchive.Write(refused, new EzyProject
        {
            Manifest = ProjectManifest.Create("test"),
            DocumentJson = "{}",
            SourceBytes = [1],
        }));
    }

    [Fact]
    public void Write_RefusesWhatItsOwnReaderWouldRefuse_AndPassesAtTheExactBoundary()
    {
        // 양방향 같은 상한. 경계는 왕복, 한 바이트 초과는 다음 열기가 아니라 쓰기에서 거절.
        var boundary = new EzyProjectLimits { MaxEntryBytes = 512 };
        EzyProject WithSource(int sourceBytes) => new()
        {
            Manifest = ProjectManifest.Create("test"),
            DocumentJson = "{}",
            SourceName = "s.bin",
            SourceBytes = new byte[sourceBytes],
        };

        using var atLimit = new MemoryStream();
        EzyProjectArchive.Write(atLimit, WithSource(512), boundary);
        atLimit.Position = 0;
        var loaded = EzyProjectArchive.Read(atLimit, boundary);
        Assert.Equal(512, loaded.SourceBytes!.Length);

        using var over = new MemoryStream();
        var entryEx = Assert.Throws<InvalidDataException>(
            () => EzyProjectArchive.Write(over, WithSource(513), boundary));
        Assert.Contains("size limit", entryEx.Message);
        Assert.Equal(0, over.Length); // 한 바이트도 쓰기 전에 거절.

        using var overTotal = new MemoryStream();
        var totalEx = Assert.Throws<InvalidDataException>(() => EzyProjectArchive.Write(
            overTotal, WithSource(60), boundary with { MaxTotalBytes = 80 }));
        Assert.Contains("total size limit", totalEx.Message);
    }

    [Fact]
    public void DefaultLimits_AdmitTheLargestLoadableSourceFile()
    {
        // 로더 최대 파일을 내장한 프로젝트도 자기 읽기 상한을 넘으면 안 됨.
        Assert.Equal(
            EzyImageViewer.Core.Imaging.InputLimits.Default.MaxFileBytes,
            EzyProjectLimits.Default.MaxEntryBytes);
        Assert.True(EzyProjectLimits.Default.MaxTotalBytes
            > EzyProjectLimits.Default.MaxEntryBytes);
    }

    [Fact]
    public void Read_RejectsMultipleEmbeddedSources()
    {
        using var stream = BuildRawArchive(
            ("mimetype", EzyProjectArchive.MimeType),
            ("manifest.json", ValidManifestJson()),
            ("document.json", "{}"),
            ("source/a.png", "x"),
            ("source/b.png", "y"));

        var ex = Assert.Throws<InvalidDataException>(() => EzyProjectArchive.Read(stream));
        Assert.Contains("Multiple embedded source", ex.Message);
    }

    [Fact]
    public void Read_MimeTypeIsFirstEntryAndStored_SoFormatSniffingWorks()
    {
        using var stream = new MemoryStream();
        EzyProjectArchive.Write(stream, SampleProject());

        // 로컬 파일 헤더: 이름은 오프셋 30, 저장 데이터는 바로 뒤.
        var bytes = stream.ToArray();
        var head = Encoding.ASCII.GetString(bytes, 30, "mimetype".Length + EzyProjectArchive.MimeType.Length);
        Assert.Equal("mimetype" + EzyProjectArchive.MimeType, head);
    }

    [Fact]
    public void Read_RejectsPlainZipWithoutMimeType()
    {
        using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("something.txt");
            using var s = entry.Open();
            s.Write("hello"u8);
        }
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => EzyProjectArchive.Read(stream));
    }

    [Fact]
    public void Read_RejectsNewerSchemaVersion()
    {
        var project = new EzyProject
        {
            Manifest = new ProjectManifest(ProjectManifest.FormatId, ProjectManifest.CurrentSchemaVersion + 1, "9.9.9", DateTimeOffset.UtcNow),
            DocumentJson = "{}",
        };
        using var stream = new MemoryStream();
        EzyProjectArchive.Write(stream, project);
        stream.Position = 0;

        Assert.Throws<NotSupportedException>(() => EzyProjectArchive.Read(stream));
    }

    [Theory]
    [InlineData("../evil.png")]
    [InlineData("..\\evil.png")]
    [InlineData("/rooted.png")]
    [InlineData("C:/rooted.png")]
    [InlineData("")]
    public void Write_RejectsUnsafeAssetNames(string name)
    {
        var project = new EzyProject
        {
            Manifest = ProjectManifest.Create("0.1.0-m0"),
            DocumentJson = "{}",
            Assets = new Dictionary<string, byte[]> { [name] = [1] },
        };
        using var stream = new MemoryStream();

        Assert.Throws<InvalidDataException>(() => EzyProjectArchive.Write(stream, project));
    }

    [Fact]
    public void Read_RejectsDuplicateEntryNames()
    {
        using var stream = BuildRawArchive(
            ("mimetype", EzyProjectArchive.MimeType),
            ("manifest.json", ValidManifestJson()),
            ("document.json", "{}"),
            ("document.json", "{\"second\":true}"));

        var ex = Assert.Throws<InvalidDataException>(() => EzyProjectArchive.Read(stream));
        Assert.Contains("Duplicate", ex.Message);
    }

    [Fact]
    public void Read_RejectsWhenMimeTypeIsNotFirstEntry()
    {
        using var stream = BuildRawArchive(
            ("manifest.json", ValidManifestJson()),
            ("mimetype", EzyProjectArchive.MimeType),
            ("document.json", "{}"));

        Assert.Throws<InvalidDataException>(() => EzyProjectArchive.Read(stream));
    }

    [Fact]
    public void Read_RejectsTraversalAssetNameInsideArchive()
    {
        using var stream = BuildRawArchive(
            ("mimetype", EzyProjectArchive.MimeType),
            ("manifest.json", ValidManifestJson()),
            ("document.json", "{}"),
            ("assets/../evil.png", "x"));

        var ex = Assert.Throws<InvalidDataException>(() => EzyProjectArchive.Read(stream));
        Assert.Contains("Invalid asset name", ex.Message);
    }

    [Fact]
    public void Read_RejectsSchemaVersionBelowOne()
    {
        var project = new EzyProject
        {
            Manifest = new ProjectManifest(ProjectManifest.FormatId, 0, "0.1.0", DateTimeOffset.UtcNow),
            DocumentJson = "{}",
        };
        using var stream = new MemoryStream();
        EzyProjectArchive.Write(stream, project);
        stream.Position = 0;

        Assert.Throws<InvalidDataException>(() => EzyProjectArchive.Read(stream));
    }

    [Fact]
    public void Read_EnforcesEntryCountLimit()
    {
        using var stream = RoundTripStream(SampleProject());

        var ex = Assert.Throws<InvalidDataException>(
            () => EzyProjectArchive.Read(stream, new EzyProjectLimits { MaxEntryCount = 3 }));
        Assert.Contains("entry limit", ex.Message);
    }

    [Fact]
    public void Read_EnforcesTotalSizeLimit()
    {
        using var stream = RoundTripStream(SampleProject());

        var ex = Assert.Throws<InvalidDataException>(
            () => EzyProjectArchive.Read(stream, new EzyProjectLimits { MaxTotalBytes = 16 }));
        Assert.Contains("total size limit", ex.Message);
    }

    [Fact]
    public void Read_EnforcesPerEntrySizeLimit()
    {
        using var stream = RoundTripStream(SampleProject());

        var ex = Assert.Throws<InvalidDataException>(
            () => EzyProjectArchive.Read(stream, new EzyProjectLimits { MaxEntryBytes = 4 }));
        Assert.Contains("size limit", ex.Message);
    }

    private static string ValidManifestJson() =>
        $$"""{"format":"ezyimg","schemaVersion":1,"appVersion":"0.1.0","createdUtc":"2026-07-16T00:00:00+00:00"}""";

    private static MemoryStream RoundTripStream(EzyProject project)
    {
        var stream = new MemoryStream();
        EzyProjectArchive.Write(stream, project);
        stream.Position = 0;
        return stream;
    }

    /// <summary>실제 작성기가 거절할 중복·잘못된 순서·경로 순회 아카이브 생성.</summary>
    private static MemoryStream BuildRawArchive(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            foreach (var (name, content) in entries)
            {
                var level = name == "mimetype"
                    ? System.IO.Compression.CompressionLevel.NoCompression
                    : System.IO.Compression.CompressionLevel.Optimal;
                var entry = zip.CreateEntry(name, level);
                using var s = entry.Open();
                s.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        stream.Position = 0;
        return stream;
    }
}
