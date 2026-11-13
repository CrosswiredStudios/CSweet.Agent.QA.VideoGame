using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK.Compute;
using CSweet.Agent.SDK.VideoGame.Contracts;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class BehavioralPolicyTests
{
    private static readonly WorkItemVerificationRequirement Smoke = new(WorkItemVerificationKinds.BrowserSmoke) { RequireCanvas = true };

    private static WorkExecutionAssignment Assignment(params WorkExecutionOutcomeV1[] prior) =>
        JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with
        { StageExecutionId = StageId, AttemptId = Guid.NewGuid(), PriorOutcomes = prior };

    private static readonly Guid StageId = Guid.NewGuid();

    private static WorkItemPlanningSpecification Planning(IReadOnlyList<WorkItemVerificationRequirement>? verifications) =>
        new(["Spike"], ["Scenarios are exposed", "Record captures tones", "Record notes uncertainty"]) { Verifications = verifications };

    private static WorkExecutionOutcomeV1 Blocked(Guid stage) => new(stage, Guid.NewGuid(), WorkExecutionDispositions.Blocked, "blocked",
        "stopped", JsonSerializer.SerializeToElement(new { qaProcessingError = "stopped" }), [], ["stopped"]);

    [Fact]
    public void Depth_and_budget_follow_the_declaration()
    {
        var smokeOnly = GameQaBehavioralPolicy.For(Planning([Smoke]), Assignment());
        Assert.Equal(GameQaBehavioralPolicy.OptionalRuns, smokeOnly.MaximumRuns);
        Assert.Empty(smokeOnly.InteractionCriteria);
        Assert.Equal(3, smokeOnly.CriterionCount);

        var legacy = GameQaBehavioralPolicy.For(Planning(null), Assignment());
        Assert.Equal(GameQaBehavioralPolicy.LegacyRuns, legacy.MaximumRuns);

        var declared = GameQaBehavioralPolicy.For(Planning([Smoke,
            new(WorkItemVerificationKinds.BrowserInteraction) { CriterionIndexes = [2, 0, 9] }]), Assignment());
        Assert.Equal(new[] { 0, 2 }, declared.InteractionCriteria); // out-of-range indexes are ignored
        Assert.Equal(4, declared.MaximumRuns); // the shared scenario run, one per declared criterion, one correction
    }

    [Fact]
    public void Each_retry_of_this_stage_grants_one_more_run()
    {
        var policy = GameQaBehavioralPolicy.For(Planning([Smoke]), Assignment(Blocked(StageId), Blocked(StageId), Blocked(Guid.NewGuid())));
        Assert.Equal(GameQaBehavioralPolicy.OptionalRuns + 2, policy.MaximumRuns);
    }

    [Fact]
    public void The_model_is_told_the_depth_the_budget_and_how_spikes_are_judged()
    {
        var optional = BrowserAcceptance.InteractionNote(Planning([Smoke]), VideoGameWorkItemTypeKeys.Task,
            GameQaBehavioralPolicy.For(Planning([Smoke]), Assignment()), 1);
        Assert.Contains("optional", optional);
        Assert.Contains("1 of 2", optional);
        Assert.DoesNotContain("research spike", optional);

        var declaredPlanning = Planning([Smoke, new(WorkItemVerificationKinds.BrowserInteraction) { CriterionIndexes = [1] }]);
        var declared = BrowserAcceptance.InteractionNote(declaredPlanning, VideoGameWorkItemTypeKeys.ResearchSpike,
            GameQaBehavioralPolicy.For(declaredPlanning, Assignment()), 0);
        Assert.Contains("1: Record captures tones", declared);
        Assert.Contains("criterion: index", declared);
        Assert.Contains("research spike", declared);

        Assert.Contains("planned before interaction declarations",
            BrowserAcceptance.InteractionNote(Planning(null), null, GameQaBehavioralPolicy.For(Planning(null), Assignment()), 0));
    }

    [Fact]
    public void A_pass_needs_a_satisfied_assertion_for_each_declared_interaction_criterion()
    {
        var planning = Planning([Smoke, new(WorkItemVerificationKinds.BrowserInteraction) { CriterionIndexes = [0, 1] }]);
        var policy = GameQaBehavioralPolicy.For(planning, Assignment());
        var pass = new GameQaOutcome(new('a', 40), "Observed", true, [], []);

        BrowserAcceptance.ValidateInteractions(pass, policy, new HashSet<int> { 0, 1 });
        var missing = Assert.Throws<InvalidOperationException>(() => BrowserAcceptance.ValidateInteractions(pass, policy, new HashSet<int> { 0 }));
        Assert.Contains("1", missing.Message);
        // A failed report and an undeclared ticket are never held to it.
        BrowserAcceptance.ValidateInteractions(pass with { Passed = false }, policy, new HashSet<int>());
        BrowserAcceptance.ValidateInteractions(pass, GameQaBehavioralPolicy.For(Planning([Smoke]), Assignment()), new HashSet<int>());
    }

    [Fact]
    public void Observations_never_fail_a_run_and_a_script_error_is_not_a_product_failure()
    {
        var state = new GameQaInteractionTesting.State("identity", new('a', 40), new('b', 64), Guid.NewGuid(), new('c', 64), "script");
        var request = Guid.NewGuid();
        var report = new GameQaInteractionTesting.Report(request, state.Commit, state.BuildSha256, state.ScriptSha256, "Chromium", true,
            [new("tone recorded", true, "record lists a tone", 1, "assertion"), new("oscillators", false, "0 oscillators", 1, "observation")],
            [], new('d', 64), 100);
        GameQaInteractionTesting.ValidateReport(report, state, request);
        Assert.Throws<InvalidOperationException>(() => GameQaInteractionTesting.ValidateReport(
            report with { Checks = [report.Checks[0] with { Kind = "maybe" }] }, state, request));
        Assert.Throws<InvalidOperationException>(() => GameQaInteractionTesting.ValidateReport(
            report with { ScriptError = "TypeError" }, state, request)); // a script error can never pass
        GameQaInteractionTesting.ValidateReport(report with { Passed = false, ScriptError = "TypeError" }, state, request);
    }

    [Fact]
    public void Compute_failures_name_the_real_reason()
    {
        var operation = new ComputeOperation(Guid.NewGuid(), Guid.NewGuid(), 2, "Completed", null,
            new(Command: new(Guid.NewGuid(), 1, false, [], Encoding.UTF8.GetBytes("Traceback\n  line 25\nValueError: QA interaction evidence exceeds 4 MiB\n"))));
        var reason = GameQaInteractionTesting.Describe(operation);
        Assert.Contains("exit code 1", reason);
        Assert.Contains("ValueError: QA interaction evidence exceeds 4 MiB", reason);
        Assert.Contains("outcome-unknown", GameQaInteractionTesting.Describe(operation with { Result = new(ErrorCode: "outcome-unknown") }));
        Assert.Contains("timed out", GameQaInteractionTesting.Describe(operation with { Result = new(Command: new(Guid.NewGuid(), null, true)) }));
    }

    [Fact]
    public void Retries_after_a_block_are_decisions_but_transport_interruptions_are_not()
    {
        var current = Assignment();
        Assert.False(GameQaInteractionTesting.FollowsBlockedAttempt(current));
        Assert.True(GameQaInteractionTesting.FollowsBlockedAttempt(current with { PriorOutcomes = [Blocked(StageId)] }));
        Assert.False(GameQaInteractionTesting.FollowsBlockedAttempt(current with { PriorOutcomes = [Blocked(Guid.NewGuid())] }));
        var transport = Blocked(StageId) with { Output = JsonSerializer.SerializeToElement(new {
            qaProcessingError = "QA could not complete exact-source validation (HttpRequestException); no validation verdict was accepted." }) };
        Assert.False(GameQaInteractionTesting.FollowsBlockedAttempt(current with { PriorOutcomes = [transport] }));
    }

    [Fact]
    public void Agent_defects_carry_the_shared_diagnostic_and_keep_their_exception_type()
    {
        var defect = GameQaDefects.Agent("binding changed");
        Assert.IsType<InvalidOperationException>(defect);
        Assert.Equal(new[] { "binding changed", WorkExecutionDiagnostics.AgentDefect }, GameQaDefects.Diagnostics(defect, "binding changed"));
        Assert.Equal(new[] { "other" }, GameQaDefects.Diagnostics(new InvalidOperationException("other"), "other"));
    }

    [Fact]
    public void Engine_notes_appear_only_when_the_project_or_criteria_call_for_them()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var plain = new WorkItemPlanningSpecification(["Menu"], ["The menu opens"]);
            Assert.Equal("", GameQaEngineNotes.For(root, plain));
            File.WriteAllText(Path.Combine(root, "package.json"), "{\"dependencies\":{\"phaser\":\"4.2.1\"}}");
            Assert.Contains("sys.events", GameQaEngineNotes.For(root, plain));
            Assert.DoesNotContain("OscillatorNode", GameQaEngineNotes.For(root, plain));
            Assert.Contains("OscillatorNode", GameQaEngineNotes.For(root, new(["Hit"], ["A brick hit plays a tone"])));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Core_instructions_are_engine_neutral_and_the_runner_keeps_all_evidence_without_a_size_cap()
    {
        Assert.DoesNotContain("Web Audio", GameQaExecution.Instructions);
        Assert.DoesNotContain("OscillatorNode", GameQaExecution.Instructions);
        Assert.Contains("save_qa_working_note", GameQaExecution.Instructions);
        var runner = GameQaInteractionTesting.Resource("qa-interactions.py");
        Assert.DoesNotContain("exceeds", runner);
        Assert.DoesNotContain("BUDGET", runner);
        Assert.Contains("trace.zip", runner);
        Assert.Contains("kind === 'observation'", GameQaInteractionTesting.Resource("qa-interactions.cjs"));
    }
}
