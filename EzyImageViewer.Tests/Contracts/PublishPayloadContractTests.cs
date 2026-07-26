using System.Text.Json;
using Xunit;

namespace EzyImageViewer.Tests.Contracts;

/// <summary>뷰어가 쓰지 않는 Windows AI/ML 런타임은 win-x64에서 약 39MB.
/// WindowsAppSDK 갱신 때 슬쩍 돌아오지 못하게 막음.</summary>
public sealed class PublishPayloadContractTests
{
    private const string MachineLearningPackageId = "Microsoft.WindowsAppSDK.ML";

    [Fact]
    public void MachineLearningRuntime_IsStrippedFromOutputAndPublishPayload()
    {
        var appProject = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "EzyImageViewer.App.csproj"));

        Assert.Contains(
            $"<EzyMachineLearningPackageId>{MachineLearningPackageId}</EzyMachineLearningPackageId>",
            appProject,
            StringComparison.Ordinal);
        Assert.Contains("EzyRemoveUnusedMachineLearningRuntime", appProject, StringComparison.Ordinal);
        Assert.Contains(
            "EzyRemoveUnusedMachineLearningRuntimeFromPublish",
            appProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "Remove=\"@(ReferenceCopyLocalPaths)\"",
            appProject,
            StringComparison.Ordinal);
        Assert.Contains(
            "Remove=\"@(ResolvedFileToPublish)\"",
            appProject,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MachineLearningPackage_IsStillOnlyTransitiveSoTheStripStaysMeaningful()
    {
        var appProject = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "EzyImageViewer.App.csproj"));
        Assert.DoesNotContain(
            $"PackageReference Include=\"{MachineLearningPackageId}\"",
            appProject,
            StringComparison.Ordinal);

        using var lockFile = JsonDocument.Parse(File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "packages.lock.json")));
        var entries = lockFile.RootElement
            .GetProperty("dependencies")
            .EnumerateObject()
            .SelectMany(framework => framework.Value.EnumerateObject())
            .Where(dependency => dependency.NameEquals(MachineLearningPackageId))
            .ToArray();

        Assert.NotEmpty(entries);
        Assert.All(entries, dependency => Assert.Equal(
            "Transitive",
            dependency.Value.GetProperty("type").GetString()));
    }

    private static string RepoFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "EzyImageViewer.slnx")))
                continue;
            return Path.Combine([directory.FullName, .. segments]);
        }
        throw new DirectoryNotFoundException(
            "Repository root was not found from the test output directory.");
    }
}
