using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class StandaloneQaTests
{
    private static T Empty<T>() => JsonSerializer.Deserialize<T>("{}")!;
    private static readonly string Sha = new('a', 40);
    private static GameQaOutcome Passed() => new(Sha, "Observed movement and touch input", true,
        [new("npm test", true, 0, "Movement and touch checks passed")], [])
        { Criteria = [new("Paddle moves", true, "Movement regression passed"), new("Touch works", true, "Touch simulation passed")] };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FinalizedRepositoryTicketsUseExecutionAndPlanningOnlyTicketsRemainDocuments(bool camelCase)
    {
        var delivery = new WorkItemDeliverySpecification(Guid.NewGuid(), ["Test input"], ["Input works"]) { BaseBranch = "main" };
        var options = new JsonSerializerOptions(camelCase ? JsonSerializerDefaults.Web : JsonSerializerDefaults.General);
        Assert.True(GameQaExecution.HasFinalizedDelivery(JsonSerializer.SerializeToElement(new { DeliverySpecificationJson = JsonSerializer.Serialize(delivery, options) }, options)));
        Assert.False(GameQaExecution.HasFinalizedDelivery(JsonSerializer.SerializeToElement(new { })));
        Assert.False(GameQaExecution.HasFinalizedDelivery(JsonSerializer.SerializeToElement(new { deliverySpecificationJson = (string?)null })));
        Assert.Throws<InvalidOperationException>(() => GameQaExecution.HasFinalizedDelivery(JsonSerializer.SerializeToElement(new { deliverySpecificationJson = "{}" })));
        Assert.Throws<JsonException>(() => GameQaExecution.HasFinalizedDelivery(JsonSerializer.SerializeToElement(new { deliverySpecificationJson = "{" })));
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("paraphrased")]
    [InlineData("duplicate")]
    [InlineData("no-evidence")]
    [InlineData("failed-criterion")]
    public void PassingCommandDoesNotExcuseMissingOrFailedAcceptanceEvidence(string fault)
    {
        var outcome = Passed();
        outcome = outcome with { Criteria = fault switch
        {
            "omitted" => outcome.Criteria.Take(1).ToArray(),
            "paraphrased" => [outcome.Criteria[0], outcome.Criteria[1] with { Criterion = "Touch is good" }],
            "duplicate" => [outcome.Criteria[0], outcome.Criteria[0]],
            "no-evidence" => [outcome.Criteria[0], outcome.Criteria[1] with { Evidence = " " }],
            _ => [outcome.Criteria[0], outcome.Criteria[1] with { Satisfied = false }]
        }};
        Assert.Throws<InvalidOperationException>(() => GameQaExecution.ValidateCriterionCoverage(outcome, ["Paddle moves", "Touch works"]));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("submit")]
    public async Task LostArtifactRepliesRecoverExactPersistedEvidenceWithoutDuplicatingDelivery(string loseAt)
    {
        var input = new WorkExecutionInputV1(Guid.NewGuid(), Guid.NewGuid(), 1,
            new WorkItemPlanningSpecification(["Test controls"], ["Paddle moves", "Touch works"])) { AllowedOutcomeCodes = ["completed"] };
        var assignment = Empty<WorkExecutionAssignmentV1>() with { StageExecutionId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), ItemId = Guid.NewGuid() };
        AgentOperatingStateResponse? state = null;
        ArtifactDocument? artifact = null;
        var lost = false;
        var creates = new List<CreateArtifactDocument>();
        var submits = new List<SubmitArtifactRevision>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (_, _) => Task.FromResult(new AgentOperatingStateReadResponse(state)))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (write, _) =>
            {
                state = new(Guid.NewGuid(), write.StateKey, "test", 1, "Active", new Dictionary<string,string>(), [], write.StateKey,
                    [], Guid.NewGuid(), write.Payload, (state?.Revision ?? 0) + 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                return Task.FromResult(state);
            })
            .RegisterCapability<CreateArtifactDocument, ArtifactDocument>(PlatformCapabilities.ArtifactCreate, (create, _) =>
            {
                Assert.NotNull(state);
                Assert.Contains("Observed movement and touch input", state.Payload.GetRawText());
                Assert.Equal(assignment.ItemId, create.OriginWorkItemId);
                Assert.Equal(input.WorkstreamId, create.WorkstreamId);
                creates.Add(create);
                if (artifact is null)
                {
                    var id = Guid.NewGuid();
                    var revisionId = Guid.NewGuid();
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(create.Content))).ToLowerInvariant();
                    artifact = new(id, create.Title, create.DocumentType, "Draft", revisionId, null, null, null, assignment.ItemId,
                        [new(revisionId, 1, null, create.Content, hash, "Draft", DateTimeOffset.UtcNow, null, null)]);
                }
                if (!lost && loseAt == "create") { lost = true; throw new IOException("Create reply lost"); }
                return Task.FromResult(artifact);
            })
            .RegisterCapability<SubmitArtifactRevision, ArtifactDocument>(PlatformCapabilities.ArtifactSubmit, (submit, _) =>
            {
                submits.Add(submit);
                Assert.Equal(artifact!.LatestRevisionId, submit.RevisionId);
                if (!lost && loseAt == "submit") { lost = true; throw new IOException("Submit reply lost"); }
                return Task.FromResult(artifact);
            });
        Task<WorkExecutionOutcomeV1> Run(GameQaOutcome report) => SpecialistAgent.SaveStandaloneQaAsync(assignment, input, report, "qa-state", runtime.CreateContext(), default);
        await Assert.ThrowsAsync<IOException>(() => Run(Passed()));
        var completed = await Run(Passed() with { Summary = "A regenerated report must not replace saved observations" });
        Assert.Equal("completed", completed.OutcomeCode);
        Assert.Equal("Completed", completed.Disposition);
        Assert.Equal(Sha, completed.Evidence.Single(x => x.Kind == "commit").Value);
        Assert.Equal(artifact!.Id, completed.Output.GetProperty("ArtifactId").GetGuid());
        Assert.Equal(artifact.LatestRevisionId, completed.Output.GetProperty("RevisionId").GetGuid());
        Assert.Equal(artifact.Revisions[0].ContentSha256, completed.Output.GetProperty("Sha256").GetString());
        Assert.Single(creates.Select(x => x.IdempotencyKey).Distinct());
        Assert.Single(creates.Select(x => x.Content).Distinct());
        Assert.Single(submits.Select(x => x.IdempotencyKey).Distinct());
        var creationCount = creates.Count;
        Assert.Equal(JsonSerializer.Serialize(completed), JsonSerializer.Serialize(await Run(Passed())));
        Assert.Equal(creationCount, creates.Count);
    }

    [Fact]
    public async Task FailedIndependentChecksBlockWithoutSubmittingACompletionDocument()
    {
        var input = new WorkExecutionInputV1(Guid.NewGuid(), Guid.NewGuid(), 1,
            new WorkItemPlanningSpecification([], ["Paddle moves", "Touch works"])) { AllowedOutcomeCodes = ["completed"] };
        var assignment = Empty<WorkExecutionAssignmentV1>() with { StageExecutionId = Guid.NewGuid(), AttemptId = Guid.NewGuid() };
        var report = Passed() with { Passed = false, Findings = ["Touch failed"],
            Criteria = [Passed().Criteria[0], Passed().Criteria[1] with { Satisfied = false }] };
        var blocked = await SpecialistAgent.CompleteStandaloneQaAsync(assignment, input, report, new AgentTestRuntime().CreateContext(), default);
        Assert.Equal("blocked", blocked.OutcomeCode);
        Assert.Equal("Blocked", blocked.Disposition);
        Assert.Contains("Touch failed", blocked.Diagnostics);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SpecialistAgent.CompleteStandaloneQaAsync(assignment,
            input with { AllowedOutcomeCodes = ["passed"] }, Passed(), new AgentTestRuntime().CreateContext(), default));
    }
}