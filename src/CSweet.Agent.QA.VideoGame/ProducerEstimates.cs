using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.Contracts;
using System.Text.Json;

namespace CSweet.Agent.QA.VideoGame;

public sealed partial class SpecialistAgent
{
    public override Task<AgentCoordinationTurnResult> HandleCoordinationTurnAsync(
        AgentCoordinationTurnRequest request, AgentRuntimeContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var original = request.Transcript.FirstOrDefault(x => x.SpeakerOrganizationUserId == request.Counterpart.OrganizationUserId &&
            x.Artifact?.Type == "video-game.production.role-estimate-request.v1")?.Artifact;
        var scope = original?.Payload.Deserialize<GameRoleEstimateCapacityRequestV1>();
        if (scope?.RoleKey != VideoGameRoleKeys.Producer)
            return base.HandleCoordinationTurnAsync(request, context, cancellationToken);
        if (request.SourceKind != "Board" || request.WorkContext is null || original is not { IsFinalPage: true } ||
            scope.RequestFingerprint != original.Key || scope.WorkItems.Count == 0 ||
            scope.WorkItems.Any(x => x.AccountableRoleKey != VideoGameRoleKeys.Producer))
            return Task.FromResult(AgentCoordinationTurnResult.Blocked("Producer estimates require an exact, non-empty board scope."));
        var latest = request.Transcript.LastOrDefault(x => x.SpeakerOrganizationUserId == request.Counterpart.OrganizationUserId);
        if (latest?.Artifact == original && !request.Transcript.Any(x => x.SpeakerOrganizationUserId == request.Self.OrganizationUserId))
            return Task.FromResult(AgentCoordinationTurnResult.Continue(
                "Provide your own estimates and capacity for these exact Producer-owned tickets. I will check coverage and provenance before sprint readiness.",
                new(original.Type, original.SchemaVersion, original.Key, original.PageOrdinal, true, original.Payload)));
        var artifact = latest?.Artifact;
        var proposal = artifact is { Type: "video-game.production.role-estimate-capacity-proposal.v1", IsFinalPage: true }
            ? artifact.Payload.Deserialize<GameRoleEstimateCapacityProposalV1>() : null;
        if (artifact?.Key != scope.RequestFingerprint || proposal is null || proposal.RoleKey != scope.RoleKey ||
            proposal.BoardId != scope.BoardId || proposal.PlanningRevision != scope.PlanningRevision ||
            proposal.PlanningDigest != scope.PlanningDigest || proposal.AvailableSprintCapacity <= 0 ||
            !proposal.Estimates.Select(x => x.WorkItemId).Order().SequenceEqual(scope.WorkItems.Select(x => x.WorkItemId).Order()) ||
            proposal.Estimates.Any(x => x.EstimatePoints <= 0))
            return Task.FromResult(AgentCoordinationTurnResult.Blocked("The Producer estimate proposal is stale, incomplete, or invalid."));
        return Task.FromResult(AgentCoordinationTurnResult.Completed(
            "Producer-owned estimates cover the requested scope. This records estimate evidence; sprint readiness still requires its separate QA assessment.",
            new(artifact.Type, artifact.SchemaVersion, artifact.Key, artifact.PageOrdinal, true, artifact.Payload)));
    }
}
