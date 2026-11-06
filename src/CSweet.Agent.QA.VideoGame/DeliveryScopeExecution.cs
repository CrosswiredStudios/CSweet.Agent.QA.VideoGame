using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame;

public sealed partial class SpecialistAgent
{
    protected override async Task<AgentWorkResult> ExecuteDeliveryScopeAsync(WorkExecutionAssignment assignment,
        AgentRuntimeContext context, CancellationToken token)
    {
        try
        {
            var client = context.CreateChatClient(new AgentLlmSelection(Settings.GetGuid("llmProviderId") ??
                throw new InvalidOperationException("Configure an approved QA provider."), Settings.GetString("llmModel")));
            return await RunDeliveryScopeAsync(assignment, context, client, token,
                ResolveContextWindowTokens(Settings), ResolveOutputTokens(Settings),
                ResolveOutputTokens(Settings));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return BlockedDelivery(assignment, error); }
    }

    internal static async Task<AgentWorkResult> RunDeliveryScopeAsync(WorkExecutionAssignment assignment,
        AgentRuntimeContext context, IChatClient client, CancellationToken token, int contextTokens = GameQaHarness.MaxContextWindowTokens, int outputTokens = GameQaHarness.MaxOutputTokens,
        int reportOutputTokens = GameQaHarness.MaxOutputTokens)
    {
        try
        {
            if (assignment.StageKey != "quality" && assignment.Handler != "quality" || assignment.Candidate is not { } candidate)
                throw new InvalidOperationException("QA requires its assigned aggregate quality candidate.");
            if (candidate.Repositories.Count == 0) return await DeliveryScopeReview.ExecuteAsync(assignment, context, client, token);
            var input = assignment.Input.Deserialize<WorkExecutionInputV1>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            await using var workspace = await DeliveryCandidateWorkspace.MaterializeAsync(assignment, context, token);
            var journal = new GameQaJournal(assignment, input, candidate.Digest, context.Platform);
            var browsers = new DeliveryScopeBrowserTools(assignment, input, context);
            await browsers.InitializeAsync(workspace, token);
            if (browsers.Pending) return AgentWorkResult.Success(browsers.WaitOutcome());
            await using var shell = GameQaHarness.CreateShell(workspace.Path);
            using var reportBudget = new GameQaReport.CorrectionBudget(token, stopAfterSavedReport: true);
            void Validate(GameQaOutcome report) { _ = DeliveryScopeEvidence.Bind(report, candidate); browsers.Validate(report); }
            var reportTool = GameQaReport.CreateTool(workspace.Path, candidate.Digest, input.Planning!.AcceptanceCriteria,
                reportBudget, async (report, ct) => { await workspace.VerifySourceUnchangedAsync(ct); await journal.SaveReportAsync(report, ct); }, Validate);
            var options = GameQaHarness.CreateOptions(context.Identity?.DisplayName ?? "Video Game QA", workspace.Path, shell,
                "Test instrumentation is not application behavior. Verify real source in bounded reads; never treat truncated text as complete source or an injected bridge as a product feature.",
                contextTokens, outputTokens, reportTool, journal);
            options.MaximumIterationsPerRequest = 24;
            foreach (var tool in browsers.Tools(reportBudget.PauseForCompute)) options.ChatOptions!.Tools!.Add(tool);
            var harness = client.AsHarnessAgent(options);
            var session = await harness.CreateSessionAsync(token); GameQaReport.Reset(workspace.Path);
            var handoff = "Validate ONLY the supplied acceptance criteria for this complete immutable candidate. " +
                "Each reported validation command must contain exactly one repositories/<repositoryId N format> path. " +
                "Inspect relevant source in small bounded reads. Save a focused plan, execute missing checks, then submit_qa_report immediately. " +
                "Do not re-audit unrelated full-game requirements. For browser tools choose repositoryId from Candidate and use paths relative to that repository. " +
                "Browser callbacks resume the SAME source-bound evaluation: reuse retained results and scripts, do not restart completed checks. " +
                "When browser compute is pending, finish without a report; the platform resumes it. " +
                "Missing environment evidence is an unverifiable criterion, never a fabricated pass. " +
                JsonSerializer.Serialize(new { assignment.Instructions, Candidate = candidate, input.Planning }) +
                "\n" + await journal.ReadEvidenceAsync(token) + "\n" + await browsers.EvidenceAsync(token);
            var report = await journal.ReadResumeReportAsync(token);
            if (report is null)
            {
                AgentResponse? evaluation = null;
                try { evaluation = await harness.RunAsync(handoff, session, null, reportBudget.Token); }
                catch (Exception error) when (reportBudget.IsReportCompletion(error, token) || reportBudget.IsComputeCompletion(error, token)) { }
                catch (OperationCanceledException) when (reportBudget.Exhausted && !token.IsCancellationRequested)
                { throw new InvalidOperationException(reportBudget.FailureMessage); }
                if (browsers.Pending) return AgentWorkResult.Success(browsers.WaitOutcome());
                if (reportBudget.AcceptedReport is null)
                {
                    await browsers.ExecuteSavedDraftsAsync(token);
                    if (browsers.Pending) return AgentWorkResult.Success(browsers.WaitOutcome());
                }
                if (reportBudget.AcceptedReport is null)
                    await GameQaFinalization.RunAsync(client, reportTool,
                        "Submit the report from these actual observations; do not restart validation.\n" +
                        JsonSerializer.Serialize(new { Candidate = candidate, input.Planning }) + "\n" +
                        await journal.ReadReportEvidenceAsync(token) + "\n" + browsers.ReportEvidence() + "\n" +
                        GameQaFinalization.Assessment(evaluation),
                        reportBudget, reportOutputTokens, token);
                report = reportBudget.AcceptedReport ?? await GameQaReport.ReadAsync(workspace.Path, token, reportBudget.LastRejection);
            }
            Validate(report);
            GameQaExecution.ValidateOutcome(report, candidate.Digest); GameQaExecution.ValidateCriterionCoverage(report, input.Planning.AcceptanceCriteria);
            await workspace.VerifySourceUnchangedAsync(token);
            var validations = DeliveryScopeEvidence.Bind(report, candidate);
            var result = new WorkDeliveryReviewResult(candidate.Digest, report.Passed, report.Summary,
                report.Criteria.Select(x => new WorkDeliveryCriterionResult(x.Criterion, x.Satisfied, x.Evidence)).ToArray(),
                report.Passed ? [] : report.Findings.Count > 0 ? report.Findings : [report.Summary]) { Validations = validations };
            DeliveryScopeReview.Validate(result, candidate.Digest, input.Planning.AcceptanceCriteria);
            return DeliveryScopeReview.Outcome(assignment, result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { return BlockedDelivery(assignment, error); }
    }
    private static AgentWorkResult BlockedDelivery(WorkExecutionAssignment assignment, Exception error) =>
        AgentWorkResult.Success(new WorkExecutionOutcomeV1(assignment.StageExecutionId, assignment.AttemptId,
            WorkExecutionDispositions.Blocked, "blocked", error.Message, JsonSerializer.SerializeToElement(new { }), [], [error.Message]));
}
