using System.IO.Compression;
using CSweet.Agent.SDK;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class GameQaWorkspaceTests
{
    [Theory]
    [InlineData("reports-only")]
    [InlineData("edit")]
    [InlineData("delete")]
    [InlineData("retained-edit")]
    public async Task Qa_downloads_exact_source_and_rejects_edits_without_uploading(string change)
    {
        var id = Guid.NewGuid(); var item = Guid.NewGuid(); var root = PlatformGitWorkspaceClient.LocalWorkspacePath(id);
        var remote = new GitWorkspaceResult(id, item, "/remote/unmounted", Guid.NewGuid(), "InternalGit", "PullRequest", new string('a', 40), "Ready", false);
        var runtime = new AgentTestRuntime().RegisterCapability<GitWorkspaceSyncRequest, GitWorkspaceSyncResult>(
            PlatformGitWorkspaceClient.SyncCapability, (request, _) =>
            {
                Assert.Equal("pull", request.Direction); Assert.Null(request.Archive);
                Assert.Equal(id, request.WorkspaceId); Assert.Equal(3, request.AssignmentRevision);
                return Task.FromResult(new GitWorkspaceSyncResult(Archive("src/game.js", "original")));
            });
        try
        {
            var prepared = await GameQaWorkspace.MaterializeAsync(remote, item, 3, runtime.CreateContext(), default);
            Assert.Equal(root, prepared.Workspace.Path);
            var source = Path.Combine(root, "src", "game.js");
            if (change == "delete") File.Delete(source);
            else if (change != "reports-only") await File.WriteAllTextAsync(source, "changed");
            await File.WriteAllTextAsync(Path.Combine(root, ".csweet", "qa-outcome.json"), "{}");
            if (change == "reports-only") await prepared.VerifySourceUnchangedAsync(default);
            else await Assert.ThrowsAsync<InvalidOperationException>(() => prepared.VerifySourceUnchangedAsync(default));
            if (change == "retained-edit")
                await Assert.ThrowsAsync<InvalidOperationException>(() => GameQaWorkspace.MaterializeAsync(remote, item, 3, runtime.CreateContext(), default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("wrong-item")]
    [InlineData("not-ready")]
    [InlineData("invalid-commit")]
    [InlineData("denied")]
    public async Task Invalid_or_denied_snapshot_never_reaches_qa(string failure)
    {
        var item = Guid.NewGuid(); var id = Guid.NewGuid();
        var remote = new GitWorkspaceResult(id, failure == "wrong-item" ? Guid.NewGuid() : item, "/remote/unmounted", Guid.NewGuid(),
            "InternalGit", "PullRequest", failure == "invalid-commit" ? "invalid" : new string('b', 40), failure == "not-ready" ? "Pending" : "Ready", false);
        var runtime = new AgentTestRuntime().RegisterCapability<GitWorkspaceSyncRequest, GitWorkspaceSyncResult>(
            PlatformGitWorkspaceClient.SyncCapability, (_, _) => throw new PlatformCapabilityException(
                PlatformGitWorkspaceClient.SyncCapability, PlatformCapabilityErrorCode.Denied, "QA authorization revoked"));
        await Assert.ThrowsAnyAsync<Exception>(() => GameQaWorkspace.MaterializeAsync(remote, item, 1, runtime.CreateContext(), default));
        Assert.False(Directory.Exists(PlatformGitWorkspaceClient.LocalWorkspacePath(id)));
    }

    private static byte[] Archive(string name, string content)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) writer.Write(content);
        return output.ToArray();
    }
}
