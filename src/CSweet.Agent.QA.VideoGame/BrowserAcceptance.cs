using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.QA.VideoGame;

internal static class BrowserAcceptance
{
    /// <summary>
    /// A planning declaration is authoritative, so the engineer, the technical reviewer and QA apply one rule.
    /// The text heuristics only cover tickets planned before declarations existed.
    /// </summary>
    internal static bool Required(WorkItemPlanningSpecification planning) =>
        WorkItemVerifications.IsDeclared(planning) ? WorkItemVerifications.BrowserSmoke(planning) is not null : LegacyRequired(planning);

    internal static bool CanvasRequired(WorkItemPlanningSpecification planning) =>
        WorkItemVerifications.IsDeclared(planning)
            ? WorkItemVerifications.BrowserSmoke(planning)?.RequireCanvas == true
            : System.Text.RegularExpressions.Regex.IsMatch(string.Join("\n", planning.AcceptanceCriteria), @"\b(Phaser|canvas)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>What QA's browser test must cover, stated for the model. Empty for tickets that need none.</summary>
    internal static string Note(WorkItemPlanningSpecification planning)
    {
        if (!Required(planning)) return WorkItemVerifications.IsDeclared(planning)
            ? "This ticket declares no browser verification; do not fail or block it for missing browser evidence." : "";
        var declared = WorkItemVerifications.BrowserSmoke(planning);
        var widths = declared?.ViewportWidths is { Count: > 0 } w ? w : [1280];
        return $"Browser verification is required: run test_web_build on the built output with viewportWidths {JsonSerializer.Serialize(widths)} " +
            $"and requireWebGl {(declared?.RequireWebGl == true ? "true" : "false")}" + (CanvasRequired(planning) ? "; every viewport must show a canvas." : ".");
    }

    /// <summary>
    /// How deep behavioral testing goes for this ticket, stated for the model: which criteria need live interaction
    /// evidence, whether behavioral runs are optional, the stage's run budget, and how research spikes are judged.
    /// </summary>
    internal static string InteractionNote(WorkItemPlanningSpecification planning, string? workItemTypeKey,
        GameQaBehavioralPolicy policy, int runsUsed)
    {
        var criteria = planning.AcceptanceCriteria ?? [];
        var note = policy.InteractionCriteria.Count > 0
            ? "Live interaction evidence is required for these acceptance criteria only (zero-based index: text): " +
              string.Join("; ", policy.InteractionCriteria.Select(i => $"{i}: {criteria[i]}")) +
              ". First call run_scenario_contract: it repeats the author's shared browser scenario run on this exact build, judges every check " +
              "from emitted events, reports one assertion per criterion and compares the run with each generated observation record. Report " +
              "every failed check and every record difference together in this verdict. Use further test_web_interactions scripts only to " +
              "settle a declared criterion the scenario checks do not really measure; cite it (check(name, satisfied, evidence, { criterion: index })). " +
              "Other criteria need no live interaction evidence; judge them from the build, tests, source and smoke result."
            : WorkItemVerifications.IsDeclared(planning)
                ? "No acceptance criterion is declared as needing live interaction evidence. test_web_interactions is optional: use it only to settle a specific criterion you cannot judge otherwise."
                : "This ticket was planned before interaction declarations existed. Use test_web_interactions only where a criterion needs behavior observed in the running build.";
        if (workItemTypeKey == CSweet.Agent.SDK.VideoGame.Contracts.VideoGameWorkItemTypeKeys.ResearchSpike)
            note += " This is a research spike: its deliverable is a record of observations. Judge whether the record covers what each criterion says it must capture and does not contradict what you observe, not whether the observed behavior was correct.";
        var limit = policy.MaximumRuns == int.MaxValue ? "" : $" Behavioral runs used in this stage: {runsUsed} of {policy.MaximumRuns}.";
        return note + limit;
    }

    /// <summary>
    /// A pass on a ticket that declared interaction criteria needs a satisfied live assertion citing each of them,
    /// from this stage's runs. The latest run must not have failed (enforced separately).
    /// </summary>
    internal static void ValidateInteractions(GameQaOutcome report, GameQaBehavioralPolicy policy, IReadOnlySet<int> satisfied)
    {
        if (!report.Passed || policy.InteractionCriteria.Count == 0) return;
        var missing = policy.InteractionCriteria.Where(i => !satisfied.Contains(i)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException("These declared interaction criteria have no satisfied test_web_interactions assertion citing them (zero-based): " +
                string.Join(", ", missing) + ". Prove them with a focused script, or report them unsatisfied or unverifiable.");
    }

    private static bool LegacyRequired(WorkItemPlanningSpecification planning)
    {
        var criteria = string.Join("\n", planning.AcceptanceCriteria);
        var context = criteria + "\n" + string.Join("\n", planning.Requirements.Concat(planning.Constraints ?? []));
        return System.Text.RegularExpressions.Regex.IsMatch(context, @"\b(browser|Chromium|Phaser|HTML|web)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
            System.Text.RegularExpressions.Regex.IsMatch(criteria, @"\b(render\w*|display\w*|visible|canvas|loads?|opens?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    internal static void Validate(WorkItemPlanningSpecification planning, GameQaOutcome report,
        string sourceIdentity, GameQaBrowserTesting.BrowserState? browser)
    {
        if (!report.Passed || !Required(planning)) return;
        if (browser is not { Complete: true, Report.Passed: true } || browser.Commit != report.SourceCommitSha ||
            browser.SourceIdentity != sourceIdentity)
            throw new InvalidOperationException("Browser-visible acceptance needs completed test_web_build evidence for this exact source and build. Static tests, a DOM mock or a claimed screenshot cannot produce a QA pass. Run the browser tool, or report the criterion unverified.");
        if (CanvasRequired(planning) && (browser.Report.Viewports.Count == 0 || browser.Report.Viewports.Any(x => !x.Canvas)))
            throw new InvalidOperationException("The accepted canvas criterion needs an observed canvas in every requested viewport; do not substitute a WebGL requirement or a static source check.");
        if (WorkItemVerifications.BrowserSmoke(planning) is { } declared)
        {
            if ((declared.ViewportWidths ?? []).Any(width => browser.Report.Viewports.All(viewport => viewport.Width != width)))
                throw new InvalidOperationException($"The declared browser verification needs viewports {JsonSerializer.Serialize(declared.ViewportWidths)}; run test_web_build with those widths.");
            if (declared.RequireWebGl && browser.Report.Viewports.Any(viewport => !viewport.WebGl))
                throw new InvalidOperationException("The declared browser verification requires WebGL in every viewport; run test_web_build with requireWebGl true.");
        }
    }

    internal static string Handoff(string commit, string target) =>
        $"Authoritative QA input is immutable commit {commit}; assigned target is {target}. " +
        "Validate this supplied snapshot. Older implementation notes naming main do not select another branch or commit. " +
        "If an explicit accepted criterion requires final main promotion, report that downstream evidence as pending; never fabricate merge or branch membership.";
}
