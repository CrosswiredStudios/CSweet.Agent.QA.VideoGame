using System.Security.Cryptography;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.QA.VideoGame;

/// <summary>Artifact output and read-only repository input remain separate authorities.</summary>
internal sealed class GameQaInputWorkspace : IAsyncDisposable
{
    private DeliveryCandidateWorkspace? snapshot;
    private GameQaWorkspace? code;
    private IReadOnlyDictionary<string, string> hashes = new Dictionary<string, string>();
    internal string Path { get; private set; } = "";
    internal string Commit { get; private set; } = "";
    internal int SourceCount => hashes.Count;
    internal string SourceIdentity => Convert.ToHexStringLower(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new {
        Commit, Files=hashes.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>new {x.Key,x.Value}).ToArray() })));
    internal static async Task<GameQaInputWorkspace> PrepareAsync(WorkExecutionAssignment assignment, WorkItemDeliverySpecification delivery,
        AgentRuntimeContext context, CancellationToken ct)
    {
        var result = new GameQaInputWorkspace();
        try
        {
            if (delivery.DeliveryKind == "Artifact")
            {
                var repository = assignment.Candidate!.Repositories.Single();
                result.snapshot = await DeliveryCandidateWorkspace.MaterializeAsync(assignment, context, ct);
                result.Path = System.IO.Path.Combine(result.snapshot.Path, "repositories", repository.RepositoryId.ToString("N"));
                result.Commit = repository.CandidateCommitSha;
                var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var file in Directory.EnumerateFiles(result.Path, "*", SearchOption.AllDirectories))
                    hashes.Add(System.IO.Path.GetRelativePath(result.Path, file), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file, ct))));
                result.hashes = hashes;
                if (hashes.Count == 0) throw new InvalidOperationException("The authorized repository input has no source files.");
                await GameQaWorkspace.VerifySourceUnchangedAsync(result.Path, hashes, ct);
            }
            else
            {
                var workspace = await context.Platform.Git.PrepareAsync(new(assignment.TaskItemId, assignment.AssignmentRevision,
                    $"game-qa:{assignment.AttemptId:N}:prepare"), ct);
                result.code = await GameQaWorkspace.MaterializeAsync(workspace, assignment.TaskItemId, assignment.AssignmentRevision, context, ct);
                result.Path = result.code.Workspace.Path; result.Commit = result.code.Workspace.BaseCommitSha; result.hashes = result.code.SourceHashes;
            }
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }
    internal async Task VerifyAsync(WorkExecutionAssignment assignment, AgentRuntimeContext context, CancellationToken ct)
    {
        await GameQaWorkspace.VerifySourceUnchangedAsync(Path, hashes, ct);
        if (code is not null)
        {
            var inspection = await context.Platform.Git.InspectAsync(new(code.Workspace.WorkspaceId, assignment.AssignmentRevision), ct);
            if (inspection.HasTrackedChanges) throw new InvalidOperationException("QA changed tracked source; the tested revision must remain unchanged.");
        }
        else
        {
            var evidence = await context.Platform.Work.ReadDeliveryEvidenceAsync(new(assignment.DeliveryPlanId!.Value, assignment.ExecutionId), ct);
            if (evidence.Candidate.Digest != assignment.Candidate!.Digest)
                throw new InvalidOperationException("The repository input assignment changed during QA.");
        }
    }
    internal async Task CleanupAsync(WorkExecutionAssignment assignment, AgentRuntimeContext context, CancellationToken ct)
    {
        if (code is not null) await context.Platform.Git.CleanupAsync(new(code.Workspace.WorkspaceId, assignment.AssignmentRevision, RetainOnFailure: true), ct);
    }
    public async ValueTask DisposeAsync() { if (snapshot is not null) await snapshot.DisposeAsync(); }
}
