using System.Reflection;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

/// <summary>ADR-0006 M8 gate: native candidates stay outside the product process.</summary>
public class NativeDecoderProductGateTests
{
    [Fact]
    public void PdfAndPsd_AreKnownButNotAdvertisedByProductSurfaces()
    {
        Assert.Contains(".pdf", ImageFormatCatalog.KnownExtensions);
        Assert.Contains(".psd", ImageFormatCatalog.KnownExtensions);
        Assert.DoesNotContain(".pdf", ImageFormatCatalog.ViewableExtensions);
        Assert.DoesNotContain(".psd", ImageFormatCatalog.ViewableExtensions);
        Assert.Equal(
            new SniffResult(SniffStatus.KnownButUnsupported, ImageFormat.Pdf),
            FormatSniffer.Sniff("%PDF-1.7 gate"u8));
        Assert.Equal(
            new SniffResult(SniffStatus.KnownButUnsupported, ImageFormat.Psd),
            FormatSniffer.Sniff("8BPS00000000"u8));
    }

    [Theory]
    [InlineData("%PDF-1.7 gate")]
    [InlineData("8BPS00000000")]
    public async Task PdfAndPsd_LoadsFailClosedAsUnsupportedFeature(string header)
    {
        var loader = new DocumentLoader();

        var error = await Assert.ThrowsAsync<UnsupportedFormatException>(() =>
            loader.LoadMemoryAsync(
                System.Text.Encoding.ASCII.GetBytes(header),
                DocumentSource.FromClipboard(),
                CancellationToken.None));

        Assert.Equal(ImageLoadFailureKind.UnsupportedFeature, error.Kind);
    }

    [Fact]
    public void NativeCandidates_RemainOutsideProductProcessAndIsolationFoundationExists()
    {
        var root = FindRepositoryRoot();
        var imagingProject = File.ReadAllText(Path.Combine(
            root, "EzyImageViewer.Imaging", "EzyImageViewer.Imaging.csproj"));
        var appProject = File.ReadAllText(Path.Combine(
            root, "EzyImageViewer.App", "EzyImageViewer.App.csproj"));
        var appServices = File.ReadAllText(Path.Combine(
            root, "EzyImageViewer.App", "AppServices.cs"));
        var protocolProjectPath = Path.Combine(
            root, "EzyImageViewer.CodecProtocol", "EzyImageViewer.CodecProtocol.csproj");
        var hostProjectPath = Path.Combine(
            root, "EzyImageViewer.CodecHost", "EzyImageViewer.CodecHost.csproj");
        var testProject = File.ReadAllText(Path.Combine(
            root, "EzyImageViewer.Tests", "EzyImageViewer.Tests.csproj"));

        Assert.DoesNotContain("PDFtoImage", imagingProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Magick.NET", imagingProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PDFtoImage", appProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Magick.NET", appProject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "public static DocumentLoader Loader { get; } = new(Limits);",
            appServices,
            StringComparison.Ordinal);
        Assert.Contains("CreateIsolatedCodecSmokeLoader", appServices, StringComparison.Ordinal);
        Assert.Contains("PDFtoImage", testProject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Magick.NET", testProject, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(protocolProjectPath));
        Assert.True(File.Exists(hostProjectPath));

        var hostProject = File.ReadAllText(hostProjectPath);
        Assert.DoesNotContain("EzyImageViewer.App", hostProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EzyImageViewer.Imaging", hostProject, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EzyImageViewer.Rendering", hostProject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PDFtoImage", hostProject, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Magick.NET", hostProject, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PdfCandidate_HasNoCancelableSinglePageRender()
    {
        var methods = typeof(PDFtoImage.Conversion)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .ToArray();
        var singlePageMethods = methods
            .Where(method => method.Name == "ToImage")
            .ToArray();
        var asyncSequenceMethods = methods
            .Where(method => method.Name == "ToImagesAsync")
            .ToArray();

        Assert.NotEmpty(singlePageMethods);
        Assert.All(singlePageMethods, method => Assert.DoesNotContain(
            method.GetParameters(),
            parameter => parameter.ParameterType == typeof(CancellationToken)));
        Assert.NotEmpty(asyncSequenceMethods);
        Assert.All(asyncSequenceMethods, method => Assert.Contains(
            method.GetParameters(),
            parameter => parameter.ParameterType == typeof(CancellationToken)));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EzyImageViewer.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
