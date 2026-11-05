using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame;

internal static class GameQaFinalization
{
    // Same isolated evaluation-only token controls used by GameQaHarness.
#pragma warning disable MAAI001
    internal static HarnessAgentOptions Options(AIFunction report, AIFunction evidence, int context, int output) => new()
    {
        Id = "com.csweet.video-game-qa.report", Name = "QA report finalization", MaximumIterationsPerRequest = 8,
        MaxContextWindowTokens = context, MaxOutputTokens = output,
        ChatOptions = new() { Instructions = GameQaExecution.Instructions +
            "\nThis is the report phase. Assess only retained observed evidence against the exact assigned criteria. " +
            "No new experiments or invented output. Submit the structured report now. Missing evidence must remain unverified; real defects remain findings.",
            Tools = [report, evidence] },
        DisableTodoProvider = true, DisableAgentModeProvider = true, DisableAgentSkillsProvider = true, DisableFileMemory = true,
        DisableToolAutoApproval = true, DisableWebSearch = true
    };
#pragma warning restore MAAI001
    internal static async Task RunAsync(IChatClient client, AIFunction report, AIFunction evidence,
        string handoff, GameQaReport.CorrectionBudget budget, int context, int output, CancellationToken outer,
        AIFunction? repositoryEvidence = null)
    {
        var options = Options(report, evidence, context, output);
        if (repositoryEvidence is not null) options.ChatOptions!.Tools!.Add(repositoryEvidence);
        var agent = client.AsHarnessAgent(options);
        var session = await agent.CreateSessionAsync(outer);
        try { await agent.RunAsync(handoff, session, null, budget.Token); }
        catch (Exception error) when (budget.IsReportCompletion(error, outer)) { }
        catch (OperationCanceledException) when (budget.Exhausted && !outer.IsCancellationRequested)
        { throw new InvalidOperationException(budget.FailureMessage); }
    }
}
