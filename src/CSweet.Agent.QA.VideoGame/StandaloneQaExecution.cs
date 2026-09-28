using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.QA.VideoGame;

public sealed partial class SpecialistAgent
{
    internal static async Task<WorkExecutionOutcomeV1> SaveStandaloneQaAsync(WorkExecutionAssignmentV1 assignment,
        WorkExecutionInputV1 input, GameQaOutcome outcome, string stateKey, AgentRuntimeContext context, CancellationToken token)
    {
        GameQaExecution.ValidateOutcome(outcome, outcome.SourceCommitSha);
        GameQaExecution.ValidateCriterionCoverage(outcome, input.Planning!.AcceptanceCriteria);
        var store = new RevisionSafeProjectState(context.Platform);
        var saved = await store.MergeAsync<GameQaReceipt>(stateKey, "video-game.qa-code-receipt.v1", 1,
            current => current ?? new(null, outcome), new Dictionary<string, string>(), $"{stateKey}:executed", token);
        if (saved.Payload.Outcome is { } completed) return completed;
        // Persist the observed report before external artifact writes. Lost replies reuse exactly this evidence.
        var final = await CompleteStandaloneQaAsync(assignment, input, saved.Payload.ValidatedReport
            ?? throw new InvalidOperationException("The durable QA execution report is unavailable."), context, token);
        var finalized = await store.MergeAsync<GameQaReceipt>(stateKey, "video-game.qa-code-receipt.v1", 1,
            current => current?.Outcome is not null ? current : new(final, current?.ValidatedReport ?? outcome),
            new Dictionary<string, string>(), $"{stateKey}:completed", token);
        return finalized.Payload.Outcome!;
    }
    internal static async Task<WorkExecutionOutcomeV1> CompleteStandaloneQaAsync(WorkExecutionAssignmentV1 assignment,
        WorkExecutionInputV1 input, GameQaOutcome outcome, AgentRuntimeContext context, CancellationToken token)
    {
        GameQaExecution.ValidateCriterionCoverage(outcome, input.Planning!.AcceptanceCriteria);
        var verdict = GameQaExecution.ValidateOutcome(outcome, outcome.SourceCommitSha);
        var evidence = new WorkExecutionEvidence("commit", "Independently tested source", outcome.SourceCommitSha);
        if (verdict != "passed")
        {
            var findings = outcome.Findings.Concat(outcome.Criteria.Where(x => !x.Satisfied).Select(x => x.Criterion + ": " + x.Evidence)).ToArray();
            return new(assignment.StageExecutionId, assignment.AttemptId, WorkExecutionDispositions.Blocked,
                "blocked", "Independent QA failed: " + outcome.Summary, JsonSerializer.SerializeToElement(outcome), [evidence], findings);
        }
        if (!input.AllowedOutcomeCodes.Contains("completed", StringComparer.Ordinal))
            throw new InvalidOperationException("The assigned standalone QA policy does not accept reviewed document completion.");
        var markdown = GameQaExecution.RenderReport(outcome);
        var artifact = await context.Platform.Artifacts.CreateAsync(new CreateArtifactDocument(
            "Independent QA execution report", markdown, "video-game.qa-evaluation-plan.v1",
            $"qa-report:{assignment.StageExecutionId:N}:{assignment.AttemptId:N}", OriginWorkItemId: assignment.ItemId)
            { WorkstreamId = input.WorkstreamId, TeamId = input.TeamId }, token);
        var revision = artifact.Revisions.Single(x => x.Id == artifact.LatestRevisionId);
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(markdown))).ToLowerInvariant();
        if (revision.Content != markdown || !string.Equals(revision.ContentSha256, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The persisted QA report does not match the independently tested evidence.");
        await context.Platform.Artifacts.SubmitAsync(new SubmitArtifactRevision(artifact.Id, revision.Id,
            $"qa-report-submit:{assignment.StageExecutionId:N}:{revision.Id:N}"), token);
        return new(assignment.StageExecutionId, assignment.AttemptId, WorkExecutionDispositions.Completed, "completed",
            $"Independent QA passed for {outcome.SourceCommitSha}; exact report submitted for Producer acceptance.",
            JsonSerializer.SerializeToElement(new { ArtifactId = artifact.Id, RevisionId = revision.Id, Sha256 = revision.ContentSha256 }),
            [evidence, new WorkExecutionEvidence("ArtifactRevision", artifact.Id.ToString("D"), revision.Id.ToString("D"), "application/json")], []);
    }
}

internal static partial class GameQaExecution
{
    internal static bool HasFinalizedDelivery(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("The authoritative QA ticket is unavailable.");
        if (!item.TryGetProperty("deliverySpecificationJson", out var value) && !item.TryGetProperty("DeliverySpecificationJson", out value)) return false;
        if (value.ValueKind == JsonValueKind.Null) return false;
        if (value.ValueKind != JsonValueKind.String) throw new InvalidOperationException("The finalized QA delivery is malformed.");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var delivery = JsonSerializer.Deserialize<WorkItemDeliverySpecification>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (delivery is null || delivery.RepositoryId == Guid.Empty || string.IsNullOrWhiteSpace(delivery.BaseBranch))
            throw new InvalidOperationException("Repository QA requires finalized repository and base-branch identity.");
        return true;
    }

    internal static void ValidateCriterionCoverage(GameQaOutcome outcome, IReadOnlyList<string> criteria)
    {
        if (criteria.Count == 0 || outcome.Criteria is null || outcome.Criteria.Any(x => x is null ||
            string.IsNullOrWhiteSpace(x.Criterion) || string.IsNullOrWhiteSpace(x.Evidence)) ||
            !criteria.Order(StringComparer.Ordinal).SequenceEqual(outcome.Criteria.Select(x => x.Criterion).Order(StringComparer.Ordinal)) ||
            outcome.Passed && outcome.Criteria.Any(x => !x.Satisfied))
            throw new InvalidOperationException("QA must report every exact acceptance criterion with observed evidence; missing or failed criteria cannot pass.");
    }

    internal static string RenderReport(GameQaOutcome outcome) =>
        "# Independent QA execution report\n\n## Build Under Test\n\nExact tested commit: `" + outcome.SourceCommitSha + "`\n\n" +
        "## Test Strategy\n\nIndependent execution against the assigned delivery criteria.\n\n" +
        "## Test Cases\n\n" + string.Join("\n\n", outcome.Criteria.Select(x => $"- {x.Criterion}\n  Result: {(x.Satisfied ? "passed" : "failed")}. Evidence: {x.Evidence}")) +
        "\n\n## Executed Commands\n\n" + JsonSerializer.Serialize(outcome.Validations, new JsonSerializerOptions { WriteIndented = true }) +
        "\n\n## Compatibility and Accessibility\n\nSee the criterion-specific observed evidence above; untested environments are not implied to pass.\n\n" +
        "## Defects\n\n" + (outcome.Findings.Count == 0 ? "No unresolved findings were reported by this execution." : string.Join("\n", outcome.Findings)) +
        "\n\n## Exit Criteria\n\n" + outcome.Summary + "\n\nProducer acceptance remains required.\n";
}
