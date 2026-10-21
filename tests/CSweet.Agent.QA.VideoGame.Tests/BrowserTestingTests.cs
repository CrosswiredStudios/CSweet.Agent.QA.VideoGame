using System.IO.Compression;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class BrowserTestingTests
{
    [Fact]
    public async Task Archive_is_deterministic_and_binds_the_actual_built_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "qa-browser-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "dist"));
            await File.WriteAllTextAsync(Path.Combine(root, "dist", "index.html"), "<canvas></canvas>");
            var first = await GameQaBrowserTesting.ArchiveAsync(root, "dist", default);
            Assert.Equal(first, await GameQaBrowserTesting.ArchiveAsync(root, "dist", default));
            using var archive = new ZipArchive(new MemoryStream(first));
            Assert.Equal("index.html", Assert.Single(archive.Entries).FullName);
            await File.WriteAllTextAsync(Path.Combine(root, "dist", "index.html"), "<canvas>changed</canvas>");
            Assert.NotEqual(first, await GameQaBrowserTesting.ArchiveAsync(root, "dist", default));
            await Assert.ThrowsAsync<ArgumentException>(() => GameQaBrowserTesting.ArchiveAsync(root, "../", default));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Browser_result_cannot_substitute_source_build_request_or_viewports()
    {
        var expected = new GameQaBrowserTesting.BrowserState(new('a', 40), new('b', 64), new('c', 64), 100, [375, 1280], true);
        var request = Guid.NewGuid();
        var report = new GameQaBrowserTesting.BrowserReport(request, expected.Commit, expected.BuildSha256, "Chromium fixture", true,
            [new(375, 720, true, true, true, []), new(1280, 720, true, true, true, [])], new('d', 64), 100);
        GameQaBrowserTesting.ValidateReport(report, expected, request);
        Assert.Throws<InvalidOperationException>(() => GameQaBrowserTesting.ValidateReport(report with { SourceCommitSha = new('e', 40) }, expected, request));
        Assert.Throws<InvalidOperationException>(() => GameQaBrowserTesting.ValidateReport(report with { BuildSha256 = new('e', 64) }, expected, request));
        Assert.Throws<InvalidOperationException>(() => GameQaBrowserTesting.ValidateReport(report, expected, Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => GameQaBrowserTesting.ValidateReport(report with { Viewports = [report.Viewports[0]] }, expected, request));
        Assert.Throws<InvalidOperationException>(() => GameQaBrowserTesting.ValidateReport(report with {
            Viewports = [report.Viewports[0] with { WebGl = false }, report.Viewports[1]] }, expected, request));
        Assert.Throws<InvalidOperationException>(() => GameQaBrowserTesting.ValidateReport(report with {
            Viewports = [report.Viewports[0] with { Errors = ["JavaScript failure"] }, report.Viewports[1]] }, expected, request));
    }
}
