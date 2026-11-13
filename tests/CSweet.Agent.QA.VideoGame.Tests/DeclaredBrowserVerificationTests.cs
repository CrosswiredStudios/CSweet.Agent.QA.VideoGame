using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.QA.VideoGame.Tests;

/// <summary>
/// Super Awesome Games (2026-11-10): QA, the engineer and the reviewer read different text to decide whether a ticket
/// needed browser evidence, and Prism Break #20 was failed back to engineering three times because QA's browser compute
/// was not prepared. A planning declaration now decides, and missing compute is a wait, never a verdict.
/// </summary>
public sealed class DeclaredBrowserVerificationTests
{
    private static WorkItemPlanningSpecification Planning(string criterion, IReadOnlyList<WorkItemVerificationRequirement>? verifications) =>
        new(["Chromium desktop Phaser scene"], [criterion]) { Verifications = verifications };

    private static readonly WorkItemVerificationRequirement Canvas = new(WorkItemVerificationKinds.BrowserSmoke) { RequireCanvas = true };

    [Fact]
    public void A_declaration_decides_and_legacy_tickets_keep_the_text_rule()
    {
        const string noBrowserVerb = "Ball hits and breaks a block and restart returns to a playable state.";
        Assert.False(BrowserAcceptance.Required(Planning(noBrowserVerb, null)));
        Assert.True(BrowserAcceptance.Required(Planning(noBrowserVerb, [Canvas])));
        Assert.True(BrowserAcceptance.CanvasRequired(Planning(noBrowserVerb, [Canvas])));
        Assert.False(BrowserAcceptance.Required(Planning("A visible Phaser canvas renders", [])));
        Assert.True(BrowserAcceptance.Required(Planning("A visible Phaser canvas renders", null)));
    }

    [Fact]
    public void Declared_widths_and_webgl_must_be_observed()
    {
        var planning = Planning("Break a block", [Canvas with { ViewportWidths = [375, 1280], RequireWebGl = true }]);
        var report = new GameQaOutcome(new('a', 40), "Observed", true, [], []);
        GameQaBrowserTesting.BrowserState Browser(params GameQaBrowserTesting.ViewportResult[] viewports) =>
            new(report.SourceCommitSha, "exact-files", new('b', 64), 10, viewports.Select(v => v.Width).ToArray(), true,
                Report: new(Guid.NewGuid(), report.SourceCommitSha, new('b', 64), "Chromium", true, viewports, new('c', 64), 10), Complete: true);

        BrowserAcceptance.Validate(planning, report, "exact-files", Browser(new(375, 720, true, true, true, []), new(1280, 720, true, true, true, [])));
        Assert.Throws<InvalidOperationException>(() => BrowserAcceptance.Validate(planning, report, "exact-files",
            Browser(new GameQaBrowserTesting.ViewportResult(1280, 720, true, true, true, []))));
        Assert.Throws<InvalidOperationException>(() => BrowserAcceptance.Validate(planning, report, "exact-files",
            Browser(new(375, 720, true, true, false, []), new(1280, 720, true, true, true, []))));
    }

    [Fact]
    public void The_model_is_told_what_the_declaration_requires()
    {
        Assert.Contains("viewportWidths [1280]", BrowserAcceptance.Note(Planning("Break", [Canvas])));
        Assert.Contains("every viewport must show a canvas", BrowserAcceptance.Note(Planning("Break", [Canvas])));
        Assert.Contains("declares no browser verification", BrowserAcceptance.Note(Planning("Break", [])));
        Assert.Equal("", BrowserAcceptance.Note(Planning("Pin the dependency", null)));
    }

    [Fact]
    public void Missing_compute_is_a_bounded_wait_and_never_a_verdict()
    {
        Assert.Null(BrowserComputeReadiness.Reason("Ready", null, true));
        var reason = BrowserComputeReadiness.Reason("Failed", "compute_checks_failed", false)!;
        Assert.Contains("compute_checks_failed", reason);
        Assert.Contains("does not return to engineering", reason);

        var assignment = System.Text.Json.JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with
            { StageExecutionId = Guid.NewGuid(), AttemptId = Guid.NewGuid() };
        var outcome = BrowserComputeReadiness.WaitOutcome(assignment, reason);

        Assert.Equal(WorkExecutionDispositions.Completed, outcome.Disposition);
        Assert.Equal("awaiting-compute-preparation", outcome.OutcomeCode);
        Assert.NotEqual("failed", outcome.OutcomeCode);
        var retryAt = outcome.Output.GetProperty("retryAt").GetDateTimeOffset();
        Assert.InRange(retryAt, DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow.AddMinutes(15));
    }
}
