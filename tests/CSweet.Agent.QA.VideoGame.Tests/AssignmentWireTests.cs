using System.Text.Json;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class AssignmentWireTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HostWireInputPreservesExactRoleAndPackageEvidence(bool camelCase)
    {
        var digest = new string('a', 64);
        var members = new[] { new ArtifactPackageMemberDigest(Guid.NewGuid(), Guid.NewGuid(), "brief", digest) };
        var package = Guid.NewGuid();
        var input = new WorkExecutionInputV1(Guid.NewGuid(), Guid.NewGuid(), 1,
            new WorkItemPlanningSpecification(["Move"], ["Moves"], [])
            {
                DelegationRecommendations = [new("specialist-execution", "game-quality-assurance", ["work.execution.run.v1"], null, true, "Engineering")],
                ArtifactPackageDigest = new(package, 1, ArtifactPackageDigestCalculator.Calculate(package, 1, members), DateTimeOffset.UtcNow, members)
            })
        {
            AssignmentRequirements = new("game-quality-assurance", [], [], ["work.execution.run.v1"]),
            AssignmentSelection = new(Guid.NewGuid(), 1, digest, [], digest, DateTimeOffset.UtcNow)
        };
        var assignment = JsonSerializer.Deserialize<WorkExecutionAssignmentV1>("{}")! with
        {
            SprintExecutionId = Guid.NewGuid(), ItemExecutionId = Guid.NewGuid(), StageExecutionId = Guid.NewGuid(), AttemptId = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(), BoardId = Guid.NewGuid(), SprintId = Guid.NewGuid(), ItemId = Guid.NewGuid(),
            AssignmentRevision = 1, Traversal = 1, Attempt = 1, Deadline = DateTimeOffset.UtcNow.AddMinutes(5), StageKey = "specialist-execution",
            Input = JsonSerializer.SerializeToElement(input, new JsonSerializerOptions(camelCase ? JsonSerializerDefaults.Web : JsonSerializerDefaults.General))
        };
        var result = SpecialistAssignmentValidator.Validate(assignment, "game-quality-assurance");
        Assert.Equal(input.WorkstreamId, result.WorkstreamId);
        Assert.Equal(input.Planning!.ArtifactPackageDigest!.Sha256, result.Planning!.ArtifactPackageDigest!.Sha256);
        Assert.Throws<UnauthorizedAccessException>(() => SpecialistAssignmentValidator.Validate(assignment, "game-engineer"));
    }
}
