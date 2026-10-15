using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class HierarchicalQaContractTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Fact]
    public void RawDispatchUsesAuthorizedCurrentDeliveryAndProducesExactPlatformReport()
    {
        var id = Guid.NewGuid(); var reviewer = Guid.NewGuid(); var author = Guid.NewGuid();
        var delivery = new WorkItemDeliverySpecification(Guid.NewGuid(), ["Build"], ["Bundle loads"])
            { DeliveryPlanId = Guid.NewGuid(), BaseBranch = "story" };
        var assignment = JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with
        { Scope="Task", SprintId=Guid.NewGuid(), SprintExecutionId=Guid.NewGuid(), PolicyRevisionId=Guid.NewGuid(),
            ItemId=id, PlanningRevision=2, DeliveryPlanId=delivery.DeliveryPlanId, StageKey="quality",
            Item=JsonSerializer.SerializeToElement(new { id, deliverySpecificationJson=JsonSerializer.Serialize(delivery,Json) }) };
        Assert.Null(assignment.Item.Deserialize<WorkItem>(Json)!.Delivery);
        var item = JsonSerializer.Deserialize<WorkItem>("{}")! with
        { Id=id, PlanningRevision=2, Planning=new(["Build"],["Bundle loads"]), Delivery=delivery,
            StageAssignments=[new("quality","AgentInstallation",AgentInstallationId:reviewer),new("specialist-execution","AgentInstallation",AgentInstallationId:author)] };
        Assert.Equal(delivery,GameQaExecution.RequireCurrentDelivery(assignment,item,reviewer.ToString(),false));
        var sha=new string('a',40);
        var report=new GameQaOutcome(sha,"Observed bundle validation",true,[new("npm test",true,0,"12 tests passed")],[])
            {Criteria=[new("Bundle loads",true,"Browser loaded the exact built bundle")]};
        var result=GameQaExecution.DeliveryReport(report,delivery,sha);
        var roundtrip=JsonSerializer.SerializeToElement(result,Json).Deserialize<WorkDeliveryReviewResult>(Json)!;
        Assert.Equal(sha,roundtrip.CandidateDigest); Assert.True(roundtrip.Approved);
        Assert.Equal(delivery.RepositoryId,Assert.Single(roundtrip.Validations).RepositoryId);
        Assert.Equal(sha,Assert.Single(roundtrip.Validations).CommitSha);
        Assert.Single(roundtrip.Criteria);
        Assert.Throws<InvalidOperationException>(()=>GameQaExecution.RequireCurrentDelivery(assignment,item with {PlanningRevision=3},reviewer.ToString(),false));
        Assert.Throws<InvalidOperationException>(()=>GameQaExecution.RequireCurrentDelivery(assignment,item,author.ToString(),false));
        Assert.Throws<InvalidOperationException>(()=>GameQaExecution.DeliveryReport(report,delivery,new string('b',40)));
    }

    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task NoncanonicalNestedOrEncodedArgumentsAreRejected(bool nested)
    {
        var root=Path.Combine(Path.GetTempPath(),"qa-flat-"+Guid.NewGuid().ToString("N"));
        var args=new Microsoft.Extensions.AI.AIFunctionArguments();
        if(nested) args["report"]=new { passed=true };
        else {args["sourceCommitSha"]=new string('a',40);args["summary"]="Claim";args["passed"]="true";args["validations"]="[]";args["findings"]="[]";args["criteria"]="[]";}
        var result=Assert.IsType<GameQaReport.Submission>(await GameQaReport.CreateTool(root,new string('a',40),["Bundle loads"]).InvokeAsync(args));
        Assert.False(result.Saved); Assert.Equal("report.invalid-arguments",result.Code); Assert.False(Directory.Exists(root));
    }
}
