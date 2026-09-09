using System.Text.Json;
using CSweet.Agent.SDK;
using CrosswiredStudios.VideoGame.Contracts;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class OwnEstimateReviewTests
{
    private static readonly AgentCoordinationParticipant Producer = new(Guid.NewGuid(), Guid.NewGuid(), "Producer", "Producer");
    private static readonly AgentCoordinationParticipant Qa = new(Guid.NewGuid(), Guid.NewGuid(), "QA", "QA");
    private static GameRoleEstimateCapacityRequestV1 Scope() => new(Guid.NewGuid(), VideoGameRoleKeys.Producer,
        3, "planning", [new(Guid.NewGuid(), "Decision log", VideoGameRoleKeys.Producer, ["Record decisions"],
            ["Accepted decisions have evidence"], [], [], "package", "assignment", null, null)], "fingerprint");
    private static AgentCoordinationTurn Turn(AgentCoordinationParticipant who, string type, object payload) =>
        new(Guid.NewGuid(), 0, who.OrganizationUserId, "Continue", "Estimate", DateTimeOffset.UtcNow,
            new(type, "1.0", "fingerprint", 1, true, JsonSerializer.SerializeToElement(payload), "digest"));
    private static AgentCoordinationTurnRequest Request(bool qa, params AgentCoordinationTurn[] turns) =>
        new(Guid.NewGuid(), turns.Length, turns.Length, "Own estimates", "Review scope", [], qa ? Qa : Producer,
            qa ? Producer : Qa, false, turns) { SourceKind = "Board",
            WorkContext = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, null, null, Guid.NewGuid(), null, null) };
    [Fact]
    public async Task QaInvitesProducerToAuthorItsOwnEstimates()
    {
        var result = await new SpecialistAgent().HandleCoordinationTurnAsync(
            Request(true, Turn(Producer, "video-game.production.role-estimate-request.v1", Scope())),
            new AgentTestRuntime().CreateContext(), default);
        Assert.Equal("Continue", result.Disposition);
        Assert.Equal("video-game.production.role-estimate-request.v1", result.Artifact!.Type);
    }

    [Theory]
    [InlineData("valid", "Completed")]
    [InlineData("stale", "Blocked")]
    [InlineData("missing", "Blocked")]
    [InlineData("zero", "Blocked")]
    [InlineData("wrong-role", "Blocked")]
    public async Task QaRequiresExactProducerOwnedScope(string scenario, string expected)
    {
        var scope = Scope();
        var proposal = new GameRoleEstimateCapacityProposalV1(scope.BoardId,
            scenario == "wrong-role" ? VideoGameRoleKeys.Engineer : scope.RoleKey,
            scenario == "stale" ? 2 : scope.PlanningRevision, scope.PlanningDigest,
            scenario == "missing" ? [] : [new(scope.WorkItems[0].WorkItemId, scenario == "zero" ? 0 : 3, "medium")],
            3, [], [], "proposal");
        var result = await new SpecialistAgent().HandleCoordinationTurnAsync(Request(true,
            Turn(Producer, "video-game.production.role-estimate-request.v1", scope),
            Turn(Qa, "video-game.production.role-estimate-request.v1", scope),
            Turn(Producer, "video-game.production.role-estimate-capacity-proposal.v1", proposal)),
            new AgentTestRuntime().CreateContext(), default);
        Assert.Equal(expected, result.Disposition);
    }
}
