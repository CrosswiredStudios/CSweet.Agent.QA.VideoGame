using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame;

public sealed partial class SpecialistAgent
{
    protected override async Task<AgentWorkResult> ExecuteDeliveryScopeAsync(WorkExecutionAssignmentV2 assignment,
        AgentRuntimeContext context, CancellationToken token)
    {
        try
        {
            if (assignment.StageKey != "quality" || assignment.Candidate is not { } candidate)
                throw new InvalidOperationException("QA requires its assigned aggregate quality candidate.");
            var client = context.CreateChatClient(new AgentLlmSelection(Settings.GetGuid("llmProviderId") ??
                throw new InvalidOperationException("Configure an approved QA provider."), Settings.GetString("llmModel")));
            if (candidate.Repositories.Count == 0) return await DeliveryScopeReview.ExecuteAsync(assignment, context, client, token);
            var input = assignment.Input.Deserialize<WorkExecutionInputV1>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            await using var workspace = await DeliveryCandidateWorkspace.MaterializeAsync(assignment, context, token);
            await using var shell = GameQaHarness.CreateShell(workspace.Path);
            var harness = client.AsHarnessAgent(await CalendarHarness.ConfigureAsync(context,
                GameQaHarness.CreateOptions(context.Identity?.DisplayName ?? "Video Game QA", workspace.Path, shell, null,
                    ResolveContextWindowTokens(Settings), ResolveOutputTokens(Settings),
                    GameQaReport.CreateTool(workspace.Path, candidate.Digest, input.Planning!.AcceptanceCriteria)), token));
            var session = await harness.CreateSessionAsync(token); GameQaReport.Reset(workspace.Path);
            await harness.RunAsync($"Run full {assignment.Scope} testing and regression against this complete immutable candidate. " +
                "The report sourceCommitSha is the supplied complete candidate digest. Each command must include its repository identifier in its path, " +
                "so the platform can bind observed validation to each repository. Execute checks for EVERY repository under repositories/<repositoryId N format>. " +
                "Read all candidate documents under documents/<revisionId N format>. Do not change source or invent tests.\n" +
                JsonSerializer.Serialize(new { assignment.Instructions, Candidate = candidate, input.Planning }), session, null, token);
            var report = await GameQaReport.ReadAsync(workspace.Path, token) ?? throw new InvalidOperationException("The QA harness returned no candidate report.");
            GameQaExecution.ValidateOutcome(report, candidate.Digest); GameQaExecution.ValidateCriterionCoverage(report, input.Planning.AcceptanceCriteria);
            await workspace.VerifySourceUnchangedAsync(token);
            var validations = candidate.Repositories.SelectMany(repository => report.Validations.Where(x => x.Command.Contains(repository.RepositoryId.ToString("N"), StringComparison.OrdinalIgnoreCase))
                .Select(x => new WorkDeliveryValidationEvidence(repository.RepositoryId, repository.CandidateCommitSha, x.Command, x.ExitCode, x.Succeeded, x.DiagnosticExcerpt ?? ""))).ToArray();
            if (report.Passed && candidate.Repositories.Any(r => validations.All(v => v.RepositoryId != r.RepositoryId)))
                throw new InvalidOperationException("QA did not execute identified validation for every candidate repository.");
            var result = new WorkDeliveryReviewResult(candidate.Digest, report.Passed, report.Summary,
                report.Criteria.Select(x => new WorkDeliveryCriterionResult(x.Criterion, x.Satisfied, x.Evidence)).ToArray(),
                report.Passed ? [] : report.Findings.Count > 0 ? report.Findings : [report.Summary]) { Validations = validations };
            DeliveryScopeReview.Validate(result, candidate.Digest, input.Planning.AcceptanceCriteria);
            return DeliveryScopeReview.Outcome(assignment, result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return AgentWorkResult.Success(new WorkExecutionOutcomeV1(assignment.StageExecutionId, assignment.AttemptId,
            WorkExecutionDispositions.Blocked, "blocked", error.Message, JsonSerializer.SerializeToElement(new { }), [], [error.Message])); }
    }
}
