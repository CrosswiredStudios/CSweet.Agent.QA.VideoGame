using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.QA.VideoGame;

/// <summary>
/// How much behavioral (Playwright) testing a QA stage gets, derived from what planning declared. Depth follows the
/// ticket, not a fixed rule: declared interaction criteria need live evidence, everything else is optional. The budget
/// exists so QA converges on a verdict; it never blocks the stage on its own.
/// </summary>
internal sealed record GameQaBehavioralPolicy(int MaximumRuns, int CriterionCount, IReadOnlyList<int> InteractionCriteria)
{
    /// <summary>Upper bound however many criteria are declared.</summary>
    internal const int MaximumDeclaredRuns = 8;
    /// <summary>Explicit "no interaction declared": one exploratory run plus one correction.</summary>
    internal const int OptionalRuns = 2;
    /// <summary>Planned before verification declarations existed: behavioral needs were never stated, so allow more.</summary>
    internal const int LegacyRuns = 4;

    /// <summary>No budget and no declaration; used where no planning is available.</summary>
    internal static GameQaBehavioralPolicy Unbounded { get; } = new(int.MaxValue, 64, []);

    internal static GameQaBehavioralPolicy For(WorkItemPlanningSpecification planning, WorkExecutionAssignment assignment)
    {
        var count = planning.AcceptanceCriteria?.Count ?? 0;
        var interaction = WorkItemVerifications.InteractionCriteria(planning).Where(i => i >= 0 && i < count).Distinct().Order().ToArray();
        // Declared interaction adds the shared scenario run (run_scenario_contract) to the focused runs.
        var baseRuns = interaction.Length > 0 ? Math.Min(interaction.Length + 1, MaximumDeclaredRuns) + 1
            : WorkItemVerifications.IsDeclared(planning) ? OptionalRuns : LegacyRuns;
        // Each retry a person or manager granted after this stage stopped is a decision to keep going: one more run.
        var retries = (assignment.PriorOutcomes ?? []).Count(o =>
            o.Disposition == WorkExecutionDispositions.Blocked && o.StageExecutionId == assignment.StageExecutionId);
        return new(baseRuns + retries, Math.Max(1, count), interaction);
    }
}

/// <summary>
/// Marks failures in QA's own processing (state, identity or contract invariants), so the Blocked outcome carries
/// <see cref="WorkExecutionDiagnostics.AgentDefect"/> and a manager does not offer a plain retry as the remedy.
/// The exception type stays <see cref="InvalidOperationException"/>.
/// </summary>
internal static class GameQaDefects
{
    private const string DataKey = "csweet.diagnostic";

    internal static InvalidOperationException Agent(string message)
    {
        var error = new InvalidOperationException(message);
        error.Data[DataKey] = WorkExecutionDiagnostics.AgentDefect;
        return error;
    }

    internal static bool IsAgentDefect(Exception error) =>
        error.Data[DataKey] as string == WorkExecutionDiagnostics.AgentDefect;

    /// <summary>Diagnostics for a Blocked outcome: the reason, plus the agent-defect token when it applies.</summary>
    internal static string[] Diagnostics(Exception error, string reason) =>
        IsAgentDefect(error) ? [reason, WorkExecutionDiagnostics.AgentDefect] : [reason];
}
