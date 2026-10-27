using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.Compute;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.QA.VideoGame;

/// <summary>Retains one logical export; callback replacement never replays a browser execution.</summary>
internal sealed class GameQaEvidenceTransfer(AgentRuntimeContext context, string identity)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal Guid? OperationId { get; private set; }
    internal async Task<byte[]?> ReadAsync(ComputeEnvironment environment, ComputeEvidenceArchive archive, CancellationToken token)
    {
        var key = "qa-archive:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity + JsonSerializer.Serialize(archive, Json))));
        var saved = (await context.Platform.ReadOperatingStateAsync<State>(key, token))?.Payload;
        if (saved is null && environment.State != "ready") return null;
        var state = saved ?? new State(new(environment.Id, environment.Generation, Key(0), archive));
        if (state.Request.EnvironmentId != environment.Id || state.Request.Archive != archive)
            throw new InvalidOperationException("Evidence transfer identity changed.");
        if (saved is null) await SaveAsync();
        var operation = state.OperationId is { } id ? await context.Platform.Compute.ReadOperationAsync(id, token)
            : await context.Platform.Compute.ExportEvidenceAsync(state.Request, token);
        if (state.OperationId is null) { state = state with { OperationId = operation.Id }; await SaveAsync(); }
        OperationId = operation.Id;
        if (operation.EnvironmentId != environment.Id || operation.Generation != state.Request.ExpectedGeneration + 1)
            throw new InvalidOperationException("Evidence export scope changed.");
        if (operation.Status is "Pending" or "Dispatching" or "Reconciling") return null;
        if (operation.Status == "Completed" && operation.Result?.ErrorCode == "outcome-unknown")
        {
            if (state.Recoveries >= 2) throw new InvalidOperationException("Evidence archive read recovery exceeded its budget.");
            var current = await context.Platform.Compute.ReadAsync(environment.Id, token);
            state = state with { Recoveries = state.Recoveries + 1, OperationId = null,
                Request = state.Request with { ExpectedGeneration = current.Generation, IdempotencyKey = Key(state.Recoveries + 1) } };
            await SaveAsync();
            operation = await context.Platform.Compute.ExportEvidenceAsync(state.Request, token);
            state = state with { OperationId = operation.Id }; await SaveAsync(); OperationId = operation.Id;
            return null;
        }
        if (operation.Status != "Completed" || operation.Result is not { ErrorCode: null, Artifact: { } artifact } ||
            artifact.SourceCommitSha != archive.SourceCommitSha || artifact.BuildSha256 != archive.BuildSha256 ||
            artifact.ScriptSha256 != archive.ScriptSha256 || artifact.Sha256 != archive.Sha256 || artifact.Length != archive.Length)
            throw new InvalidOperationException("Evidence archive transfer did not retain the pinned result.");
        try { return await context.Platform.Compute.ReadEvidenceAsync(operation, token); }
        catch (InvalidDataException error) { throw new InvalidOperationException("Retained evidence archive failed integrity validation.", error); }

        string Key(int recovery) => "qae:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key + ":" + recovery)));
        async Task SaveAsync()
        {
            var desired = state with { Cursor = state.Cursor + 1 };
            var receipt = await new RevisionSafeProjectState(context.Platform).MergeAsync<State>(key, "video-game.qa-archive-transfer.v1", 1,
                current => (current?.Cursor ?? 0) == desired.Cursor - 1 ? desired :
                    JsonSerializer.Serialize(current, Json) == JsonSerializer.Serialize(desired, Json) ? current! :
                    throw new InvalidOperationException("Evidence transfer advanced elsewhere; resume current state."),
                new Dictionary<string, string>(), key + ":" + desired.Cursor, token);
            state = receipt.Payload;
        }
    }
    internal sealed record State(ExportComputeEvidenceRequest Request, Guid? OperationId = null, int Recoveries = 0, int Cursor = 0);
}
