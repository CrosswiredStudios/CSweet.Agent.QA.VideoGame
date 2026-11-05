using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.QA.VideoGame;

internal static class BrowserAcceptance
{
    internal static bool Required(WorkItemPlanningSpecification planning)
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
        if (System.Text.RegularExpressions.Regex.IsMatch(string.Join("\n", planning.AcceptanceCriteria), @"\b(Phaser|canvas)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) &&
            (browser.Report.Viewports.Count == 0 || browser.Report.Viewports.Any(x => !x.Canvas)))
            throw new InvalidOperationException("The accepted canvas criterion needs an observed canvas in every requested viewport; do not substitute a WebGL requirement or a static source check.");
    }

    internal static string Handoff(string commit, string target) =>
        $"Authoritative QA input is immutable commit {commit}; assigned target is {target}. " +
        "Validate this supplied snapshot. Older implementation notes naming main do not select another branch or commit. " +
        "If an explicit accepted criterion requires final main promotion, report that downstream evidence as pending; never fabricate merge or branch membership.";
}
