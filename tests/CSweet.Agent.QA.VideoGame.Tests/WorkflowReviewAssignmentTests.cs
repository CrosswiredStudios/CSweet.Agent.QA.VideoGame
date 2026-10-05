using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class WorkflowReviewAssignmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_callback_reaches_platform_and_preserves_actionable_denials(bool wrongRole)
    {
        var (assignment, input) = Create("quality", "game-quality-assurance");
        if (wrongRole)
            assignment = assignment with { Input = JsonSerializer.SerializeToElement(input with
                { AssignmentRequirements = input.AssignmentRequirements! with { RequiredRoleKey = "game-engineer" } },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        var reads = 0;
        var runtime = new AgentTestRuntime().RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(
            PlatformCapabilities.AgentOperatingStateRead, (_, _) =>
            {
                reads++;
                throw new PlatformCapabilityException(PlatformCapabilities.AgentOperatingStateRead,
                    PlatformCapabilityErrorCode.Denied, "The operating-state grant is missing.");
            });
        var result = await runtime.ExecuteCapabilityAsync(new SpecialistAgent(), WorkManagementCapabilityNames.ExecutionRunV1, assignment);
        Assert.True(result.Succeeded);
        var outcome = result.Value!.Value.Deserialize<WorkExecutionOutcomeV1>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Blocked", outcome.Disposition);
        Assert.Equal(wrongRole ? 0 : 1, reads);
        Assert.Contains(wrongRole ? "canonical assignment" : "The operating-state grant is missing.", outcome.Summary);
        Assert.Empty(outcome.Evidence);
    }

    [Theory]
    [InlineData("technical-review", "game-technical-director")]
    [InlineData("merge-decision", "game-technical-director")]
    [InlineData("quality", "game-quality-assurance")]
    public void Manager_assigned_review_accepts_original_plan_that_only_delegated_implementation(string stage, string role)
    {
        var (assignment, input) = Create(stage, role);
        var validated = SpecialistAssignmentValidator.Validate(assignment, role);
        Assert.Single(validated.Planning!.DelegationRecommendations);
        Assert.Equal("specialist-execution", validated.Planning.DelegationRecommendations[0].StageKey);
        Assert.Equal(input.Planning!.ArtifactPackageDigest!.Sha256, validated.Planning.ArtifactPackageDigest!.Sha256);
    }

    [Theory]
    [InlineData("wrong-role")]
    [InlineData("wrong-stage-role")]
    [InlineData("missing-selection")]
    [InlineData("missing-requirements")]
    [InlineData("wrong-package")]
    [InlineData("unknown-stage")]
    [InlineData("standalone-owner")]
    public void Review_still_requires_canonical_eligibility_and_approved_inputs(string problem)
    {
        var (assignment, input) = Create("technical-review", "game-technical-director");
        input = problem switch
        {
            "wrong-role" => input with { AssignmentRequirements = input.AssignmentRequirements! with { RequiredRoleKey = "game-engineer" } },
            "missing-selection" => input with { AssignmentSelection = null },
            "missing-requirements" => input with { AssignmentRequirements = null },
            "wrong-package" => input with { Planning = input.Planning! with { ArtifactPackageDigest = input.Planning.ArtifactPackageDigest! with { Sha256 = new string('b', 64) } } },
            _ => input
        };
        assignment = assignment with
        {
            StageKey = problem switch { "wrong-stage-role" => "quality", "unknown-stage" => "custom-review", "standalone-owner" => "specialist-execution", _ => assignment.StageKey },
            Input = JsonSerializer.SerializeToElement(input, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        Assert.ThrowsAny<Exception>(() => SpecialistAssignmentValidator.Validate(assignment, "game-technical-director"));
    }

    private static (WorkExecutionAssignmentV1, WorkExecutionInputV1) Create(string stage, string role)
    {
        var digest = new string('a', 64);
        var members = new[] { new ArtifactPackageMemberDigest(Guid.NewGuid(), Guid.NewGuid(), "brief", digest) };
        var package = Guid.NewGuid();
        var input = new WorkExecutionInputV1(Guid.NewGuid(), Guid.NewGuid(), 1,
            new WorkItemPlanningSpecification(["Implement"], ["Test"], [])
            {
                DelegationRecommendations = [new("specialist-execution", "game-engineer", ["work.execution.run.v1"], null, true, "Original approved planning")],
                ArtifactPackageDigest = new(package, 1, ArtifactPackageDigestCalculator.Calculate(package, 1, members), DateTimeOffset.UtcNow, members)
            })
        {
            AssignmentRequirements = new(role, [], [], ["work.execution.run.v1"]),
            AssignmentSelection = new(Guid.NewGuid(), 5, digest, [], digest, DateTimeOffset.UtcNow)
        };
        var assignment = JsonSerializer.Deserialize<WorkExecutionAssignmentV1>("{}")! with
        {
            SprintExecutionId = Guid.NewGuid(), ItemExecutionId = Guid.NewGuid(), StageExecutionId = Guid.NewGuid(), AttemptId = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(), BoardId = Guid.NewGuid(), SprintId = Guid.NewGuid(), ItemId = Guid.NewGuid(),
            AssignmentRevision = 1, Attempt = 1, Deadline = DateTimeOffset.UtcNow.AddMinutes(5), StageKey = stage,
            Item = JsonSerializer.SerializeToElement(new { }), Evidence = [],
            Input = JsonSerializer.SerializeToElement(input, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        return (assignment, input);
    }
}
