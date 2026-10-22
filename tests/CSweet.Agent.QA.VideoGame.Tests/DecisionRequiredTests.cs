using System.Text.Json;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.QA.VideoGame.Tests;

/// <summary>
/// QA must not bounce a problem only the Producer can resolve back to engineering. Reproduces VG4CC32F61E0-22:
/// static checks passed, but desktop fps and bundle size could not be measured without a browser or npm registry.
/// </summary>
public sealed class DecisionRequiredTests
{
    private static readonly string Sha = new('a', 40);
    private static readonly string[] Names = ["Phaser pinned", "Desktop Chrome sustains 60 fps", "Bundle baseline recorded"];

    private static GameQaOutcome Report(bool commandPassed = true, params GameQaCriterion[] criteria) =>
        new(Sha, "Static checks pass; desktop measurements are unavailable here.", false,
            [new("python3 tools/static_validate.py", commandPassed, commandPassed ? 0 : 1, "24 checks")], [])
        {
            Criteria = criteria.Length > 0 ? criteria : new GameQaCriterion[]
            {
                new(Names[0], true, "package.json pins phaser 3.85.2"),
                new(Names[1], false, "No browser is available in the QA runtime; no fps number claimed.", Unverifiable: true),
                new(Names[2], false, "npm registry is unreachable; baseline cannot be produced.", Unverifiable: true)
            }
        };

    private static WorkExecutionAssignment Assignment() =>
        JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with
        {
            Scope = "Task", SprintId = Guid.NewGuid(), SprintExecutionId = Guid.NewGuid(), PolicyRevisionId = Guid.NewGuid(), StageExecutionId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), Identifier = "VG4CC32F61E0-22" };

    [Fact]
    public void Only_unverifiable_gaps_become_a_decision_for_the_producer()
    {
        var report = Report();
        GameQaExecution.ValidateCriterionCoverage(report, Names);
        Assert.True(GameQaExecution.RequiresDecision(report));

        var assignment = Assignment();
        var outcome = GameQaExecution.DecisionOutcome(assignment, report);
        Assert.Equal(WorkExecutionDispositions.Blocked, outcome.Disposition);
        Assert.Equal("blocked", outcome.OutcomeCode);
        Assert.Equal(assignment.AttemptId, outcome.AttemptId);
        Assert.Contains(GameQaExecution.DecisionRequiredDiagnostic, outcome.Diagnostics);
        Assert.StartsWith("Decision needed on VG4CC32F61E0-22", outcome.Summary);
        Assert.Contains("Desktop Chrome sustains 60 fps", outcome.Summary);
        Assert.Contains("npm registry is unreachable", outcome.Summary);
        Assert.DoesNotContain("Phaser pinned", outcome.Summary);
        Assert.Equal(Sha, Assert.Single(outcome.Evidence, x => x.Kind == "commit").Value);
    }

    [Fact]
    public void Defects_and_failed_commands_still_fail_back_to_engineering()
    {
        Assert.False(GameQaExecution.RequiresDecision(Report(commandPassed: false)));
        // A defect reported only as a finding still goes back to engineering.
        Assert.False(GameQaExecution.RequiresDecision(Report() with { Findings = ["Ball tunnels through bricks at wave 10."] }));
        Assert.False(GameQaExecution.RequiresDecision(Report(true,
            new(Names[0], false, "package.json uses a caret range."),
            new(Names[1], false, "No browser available.", Unverifiable: true),
            new(Names[2], true, "Recorded."))));
        Assert.False(GameQaExecution.RequiresDecision(Report(true,
            new(Names[0], false, "Pin missing."), new(Names[1], true, "Measured."), new(Names[2], true, "Recorded."))));
    }

    [Fact]
    public void Unverifiable_criteria_cannot_be_reported_as_satisfied()
    {
        var report = Report(true,
            new(Names[0], true, "Pinned."),
            new(Names[1], true, "Assumed fine.", Unverifiable: true),
            new(Names[2], true, "Recorded."));
        Assert.Throws<InvalidOperationException>(() => GameQaExecution.ValidateCriterionCoverage(report, Names));
    }

    [Fact]
    public void Missing_environment_is_visible_even_after_a_long_passing_evidence_prefix()
    {
        var criterion = new GameQaCriterion("Verify browser loading", false,
            string.Concat(Enumerable.Repeat("Build and static checks passed. ", 40)) +
            "Firefox and Safari cannot be executed in this sandbox; test_web_build provides Chromium only.", true);
        var outcome = GameQaExecution.DecisionOutcome(Assignment(), Report(true, criterion));
        Assert.Contains("Firefox and Safari cannot be executed", outcome.Summary);
        Assert.DoesNotContain("Build and static checks passed", outcome.Summary);
        Assert.DoesNotContain("any available role", outcome.Summary);
        Assert.Contains("Then retry this QA stage", outcome.Summary);
        Assert.Equal(criterion.Evidence, outcome.Output.GetProperty("Criteria")[0].GetProperty("Evidence").GetString());
    }

    [Fact]
    public void Structured_missing_evidence_is_kept_separate_from_successful_checks()
    {
        var criterion = new GameQaCriterion("Measure audio", false, "Build passed. " + new string('x', 1000), true,
            "An audio capture device is unavailable.");
        Assert.Equal("An audio capture device is unavailable.", GameQaExecution.DecisionGap(criterion));
    }

    [Fact]
    public void Decision_summary_fits_the_platform_block_reason()
    {
        var many = Enumerable.Range(1, 30).Select(i =>
            new GameQaCriterion($"Criterion {i} " + new string('x', 300), false, new string('y', 400), Unverifiable: true)).ToArray();
        var outcome = GameQaExecution.DecisionOutcome(Assignment(), Report(true, many));
        Assert.True(outcome.Summary.Length <= 3500);
        Assert.Contains("Then retry this QA stage", outcome.Summary);
        Assert.Contains("additional gaps", outcome.Summary);
    }

    [Fact]
    public void Report_renders_unverifiable_checks_without_calling_them_failures()
    {
        var markdown = GameQaExecution.RenderReport(Report());
        Assert.Contains("not verifiable in this environment", markdown);
        Assert.Contains("Result: passed", markdown);
    }
}
