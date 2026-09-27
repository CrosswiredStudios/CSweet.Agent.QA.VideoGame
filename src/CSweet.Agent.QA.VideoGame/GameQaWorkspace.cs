using System.IO.Compression;
using System.Security.Cryptography;
using CSweet.Agent.SDK;

namespace CSweet.Agent.QA.VideoGame;

internal sealed record GameQaWorkspace(GitWorkspaceResult Workspace, IReadOnlyDictionary<string, string> SourceHashes)
{
    internal static async Task<GameQaWorkspace> MaterializeAsync(GitWorkspaceResult workspace, Guid itemId,
        long revision, AgentRuntimeContext context, CancellationToken token)
    {
        if (workspace.WorkItemId != itemId || workspace.Status != "Ready")
            throw new InvalidOperationException("The platform returned an unavailable or mismatched QA workspace.");
        GameQaExecution.RequireCommit(workspace.BaseCommitSha);
        workspace = await context.Platform.Git.MaterializeAsync(workspace, revision, token);
        // Materialize preserves local edits on reconnect. Obtain the authoritative baseline again,
        // so a retained edit can never become the accepted baseline of a later QA attempt.
        var snapshot = await context.Platform.InvokeAsync<GitWorkspaceSyncRequest, GitWorkspaceSyncResult>(
            PlatformGitWorkspaceClient.SyncCapability,
            new(workspace.WorkspaceId, revision, "pull", $"workspace:{workspace.WorkspaceId:N}:pull:{revision}"), token);
        using var stream = new MemoryStream(snapshot.Archive ?? throw new InvalidOperationException("QA source snapshot is missing."));
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            var relative = entry.FullName;
            if (relative.EndsWith('/')) continue;
            if (relative.Split('/').Any(x => x is "" or "." or ".." or ".git" or ".csweet") || relative.Contains('\\') || relative.Contains(':') ||
                hashes.Count >= 20000 || (expanded += entry.Length) > 268435456)
                throw new InvalidOperationException("QA source snapshot contains an invalid path or exceeds content limits.");
            _ = ResolveSourcePath(workspace.Path, relative);
            await using var source = entry.Open();
            if (!hashes.TryAdd(relative, Convert.ToHexString(await SHA256.HashDataAsync(source, token))))
                throw new InvalidOperationException("QA source snapshot contains duplicate paths.");
        }
        if (hashes.Count == 0) throw new InvalidOperationException("QA source snapshot contains no source files.");
        var result = new GameQaWorkspace(workspace, hashes);
        await result.VerifySourceUnchangedAsync(token);
        return result;
    }

    internal async Task VerifySourceUnchangedAsync(CancellationToken token)
    {
        foreach (var (relative, expectedHash) in SourceHashes)
        {
            var path = ResolveSourcePath(Workspace.Path, relative);
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("QA source must not contain symbolic links.");
                if (string.Equals(current, Path.GetFullPath(Workspace.Path), StringComparison.Ordinal)) break;
            }
            if (!File.Exists(path)) throw new InvalidOperationException("QA removed original source; the exact tested revision must remain unchanged.");
            await using var source = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
            if (actual != expectedHash) throw new InvalidOperationException("QA changed original source; the exact tested revision must remain unchanged.");
        }
    }

    private static string ResolveSourcePath(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("QA source snapshot escapes the assigned workspace.");
        return path;
    }
}
