using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame;

// Each repository retains its own source/build journal; the whole-candidate report stays separate.
internal sealed class DeliveryScopeBrowserTools(WorkExecutionAssignment assignment, WorkExecutionInputV1 input,
    AgentRuntimeContext context)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<Guid, Repository> repositories = [];
    private sealed record Repository(GameQaInputWorkspace Source, GameQaJournal Journal,
        GameQaBrowserTesting Browser, GameQaInteractionTesting Interactions, GameQaBrowserScripts Scripts);
    internal bool Pending => repositories.Values.Any(r => r.Browser.Pending || r.Interactions.Pending);
    /// <summary>Why browser compute cannot run now, if it cannot. QA then waits without a verdict.</summary>
    internal string? Unavailable => repositories.Values.Select(r => r.Browser.Unavailable).FirstOrDefault(reason => reason is not null);
    internal bool HasCompletedBuild => repositories.Values.Any(r => r.Browser.CompletedBuild is not null);

    internal async Task InitializeAsync(DeliveryCandidateWorkspace workspace, CancellationToken token)
    {
        foreach (var repository in assignment.Candidate!.Repositories)
        {
            var source = await GameQaInputWorkspace.FromCandidateAsync(workspace, repository.RepositoryId, repository.CandidateCommitSha, token);
            var journal = new GameQaJournal(assignment, input, source.SourceIdentity, context.Platform);
            var browser = new GameQaBrowserTesting(assignment, source, journal, context);
            repositories.Add(repository.RepositoryId, new(source, journal, browser,
                new(assignment, source, journal, browser, context), new(assignment, source, journal, context)));
        }
        // Resume one admitted operation at a time; no model polling or concurrent environment mutations.
        foreach (var repository in repositories.Values)
        {
            await repository.Browser.ResumeAsync(token);
            if (repository.Browser.Pending || repository.Browser.Unavailable is not null) return;
            await repository.Interactions.ResumeAsync(token);
            if (repository.Interactions.Pending) return;
            await repository.Scripts.RestoreAsync(token);
        }
        await ExecuteSavedDraftsAsync(token);
    }
    internal async Task ExecuteSavedDraftsAsync(CancellationToken token)
    {
        if (Pending) return;
        foreach (var repository in repositories.Values)
        {
            // A saved source-bound draft is pending work, not an executed check. Drain only
            // the latest authored script; an admitted/unknown/completed request is never reset.
            if (repository.Browser.CompletedBuild is null) continue;
            var draft = await repository.Scripts.LatestDraftAsync(token);
            if (draft is null || repository.Interactions.HasScript(draft.Script)) continue;
            await repository.Scripts.RestoreAsync(token);
            await repository.Interactions.Tool().InvokeAsync(new() { ["testScriptPath"] = draft.Path }, token);
            if (Pending) return;
        }
    }
    private Repository Select(string repositoryId)
    {
        if (!Guid.TryParse(repositoryId, out var id) || !repositories.TryGetValue(id, out var repository))
            throw new ArgumentException("Choose one repositoryId from the assigned immutable candidate.");
        return repository;
    }
    internal IReadOnlyList<AIFunction> Tools(Action onPending) =>
    [
        AIFunctionFactory.Create(async (string repositoryId, string buildDirectory, IReadOnlyList<int> viewportWidths,
            bool requireWebGl, CancellationToken token) => await Select(repositoryId).Browser.Tool(onPending).InvokeAsync(new()
            { ["buildDirectory"] = buildDirectory, ["viewportWidths"] = viewportWidths, ["requireWebGl"] = requireWebGl }, token),
            "test_web_build", "Test this repository's actual built output using the approved isolated Chromium compute tool. Supply an assigned repositoryId, repository-relative buildDirectory, viewportWidths and requireWebGl. Pending means finish the turn without a verdict; events resume QA. Smoke proves loading only, not gameplay or device compatibility."),
        AIFunctionFactory.Create(async (string repositoryId, string script, CancellationToken token) =>
            await Select(repositoryId).Scripts.Tool().InvokeAsync(new() { ["script"] = script }, token),
            "save_browser_test_script", "Save an independent Playwright async function body for one assigned repositoryId. It receives page, context, origin and check(name,boolean,evidence). Use actual application APIs and input. page.evaluate needs one serialized argument; outer variables are unavailable. Never substitute an injected shim for application behavior. No source edits or shell quoting. Returns a repository-relative testScriptPath."),
        AIFunctionFactory.Create(async (string repositoryId, string testScriptPath, CancellationToken token) =>
            await Select(repositoryId).Interactions.Tool(onPending).InvokeAsync(new() { ["testScriptPath"] = testScriptPath }, token),
            "test_web_interactions", "Run the saved Playwright test against this repository's completed test_web_build production artifact. Supply assigned repositoryId and repository-relative testScriptPath. Real input/measurements only; at most 24 named checks and 22 seconds. Pending means finish the turn without a verdict; no polling."),
        AIFunctionFactory.Create(async (string repositoryId, int observationIndex, int page, CancellationToken token) =>
            await Select(repositoryId).Journal.CreateReadTool().InvokeAsync(new() { ["observationIndex"] = observationIndex, ["page"] = page }, token),
            "read_repository_qa_evidence", "Read a bounded page of this assigned repository's retained browser evidence. Supply repositoryId, observationIndex and page. Evidence is not a QA verdict.")
    ];
    internal async Task<string> EvidenceAsync(CancellationToken token)
    {
        var evidence = new List<object>();
        foreach (var (id, repository) in repositories)
            evidence.Add(new { repositoryId = id, observations = await repository.Journal.ReadEvidenceAsync(token),
                browser = repository.Browser.Evidence, interactions = repository.Interactions.Evidence });
        return JsonSerializer.Serialize(evidence, Json);
    }
    internal string ReportEvidence() => JsonSerializer.Serialize(repositories.Select(pair => new {
        repositoryId = pair.Key, commit = pair.Value.Source.Commit,
        browser = pair.Value.Browser.Evidence, interactions = pair.Value.Interactions.Evidence
    }), Json);
    internal WorkExecutionOutcomeV1 WaitOutcome()
    {
        var repository = repositories.Values.First(r => r.Browser.Pending || r.Interactions.Pending);
        var pending = repository.Browser.Pending ? repository.Browser.WaitOutcome() : repository.Interactions.WaitOutcome();
        var ids = pending.Output.GetProperty("operationIds").Deserialize<Guid[]>(Json)!;
        return pending with { Output = JsonSerializer.SerializeToElement(new WorkDeliveryComputePending(
            assignment.Candidate!.Digest, ids, pending.Output.GetProperty("retryAt").GetDateTimeOffset()), Json) };
    }
    internal void Validate(GameQaOutcome report)
    {
        if (Pending) throw new InvalidOperationException("Browser compute is pending; no aggregate QA verdict is accepted.");
        if (Unavailable is not null) throw new InvalidOperationException("Browser compute is unavailable; finish without a verdict. QA resumes automatically when compute is ready.");
        if (!report.Passed) return;
        if (repositories.Values.Any(r => r.Browser.Failed || r.Interactions.Failed))
            throw new InvalidOperationException("Retained exact-build browser checks failed; preserve the defects in the report.");
        if (BrowserAcceptance.Required(input.Planning!))
        {
            var browser = repositories.Values.FirstOrDefault(r => r.Browser.CompletedBuild is { Report.Passed: true })
                ?? throw new InvalidOperationException("Browser-visible acceptance requires test_web_build for the assigned candidate; static checks cannot pass it.");
            BrowserAcceptance.Validate(input.Planning!, report with { SourceCommitSha = browser.Source.Commit },
                browser.Source.SourceIdentity, browser.Browser.CompletedBuild);
        }
    }
}
