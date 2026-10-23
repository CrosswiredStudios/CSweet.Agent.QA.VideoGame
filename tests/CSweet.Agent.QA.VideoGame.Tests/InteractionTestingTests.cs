using System.Text;
using System.Text.Json;
using CSweet.WorkManagement.Contracts;
using CSweet.Agent.SDK.Compute;
namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class InteractionTestingTests
{
    [Fact]
    public void Rejected_output_recovery_requires_exact_platform_proof_and_no_accepted_operation()
    {
        var stage=Guid.NewGuid();
        const string message="QA could not invoke compute.execute.v1 (ValidationFailed): JSON Schema validation failed: $.workload.command.maximumOutputBytes is above the maximum.";
        var outcome=new WorkExecutionOutcomeV1(stage,Guid.NewGuid(),WorkExecutionDispositions.Blocked,"blocked",message,
            JsonSerializer.SerializeToElement(new{qaProcessingError=message}),[],[]);
        var assignment=JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with{StageExecutionId=stage,PriorOutcomes=[outcome]};
        var request=new GameQaInteractionTesting.Request(3,"old-unaccepted",new ComputeCommand(Guid.NewGuid(),"/usr/bin/docker","/var/lib/csweet-compute/work",[],30,32768));
        var state=new GameQaInteractionTesting.State("identity",new('a',40),new('b',64),Guid.NewGuid(),new('c',64),"script",Step:1,Request:request);
        Assert.True(GameQaInteractionTesting.CanRecoverRejectedOutput(state,assignment));
        Assert.False(GameQaInteractionTesting.CanRecoverRejectedOutput(state with{OperationId=Guid.NewGuid()},assignment));
        Assert.False(GameQaInteractionTesting.CanRecoverRejectedOutput(state with{Step=0},assignment));
        Assert.False(GameQaInteractionTesting.CanRecoverRejectedOutput(state,assignment with{StageExecutionId=Guid.NewGuid()}));
        Assert.False(GameQaInteractionTesting.CanRecoverRejectedOutput(state,assignment with{PriorOutcomes=[]}));
        Assert.False(GameQaInteractionTesting.CanRecoverRejectedOutput(state,assignment with{PriorOutcomes=[outcome with{Output=JsonSerializer.SerializeToElement(new{qaProcessingError="outcome-unknown"})}]}));
        Assert.False(GameQaInteractionTesting.CanRecoverRejectedOutput(state,assignment with{PriorOutcomes=[outcome with{Output=JsonSerializer.SerializeToElement(new{qaProcessingError=42})}]}));
    }
    [Fact]
    public void Observations_bind_source_build_script_request_and_nonempty_assertions()
    {
        var state=new GameQaInteractionTesting.State("identity",new('a',40),new('b',64),Guid.NewGuid(),new('c',64),"script");
        var request=Guid.NewGuid();
        var report=new GameQaInteractionTesting.Report(request,state.Commit,state.BuildSha256,state.ScriptSha256,"Chromium fixture",true,
            [new("audio",true,"Observed waveform")],[],new('d',64),100);
        GameQaInteractionTesting.ValidateReport(report,state,request);
        Assert.Throws<InvalidOperationException>(()=>GameQaInteractionTesting.ValidateReport(report,state,Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(()=>GameQaInteractionTesting.ValidateReport(report with{SourceCommitSha=new('e',40)},state,request));
        Assert.Throws<InvalidOperationException>(()=>GameQaInteractionTesting.ValidateReport(report with{BuildSha256=new('e',64)},state,request));
        Assert.Throws<InvalidOperationException>(()=>GameQaInteractionTesting.ValidateReport(report with{ScriptSha256=new('e',64)},state,request));
        Assert.Throws<InvalidOperationException>(()=>GameQaInteractionTesting.ValidateReport(report with{Checks=[]},state,request));
        Assert.Throws<InvalidOperationException>(()=>GameQaInteractionTesting.ValidateReport(report with{Checks=[report.Checks[0] with{Satisfied=false}]},state,request));
        Assert.Throws<InvalidOperationException>(()=>GameQaInteractionTesting.ValidateReport(report with{Errors=["page error"]},state,request));
        Assert.Throws<InvalidOperationException>(()=>GameQaInteractionTesting.ValidateReport(report with{Checks=[report.Checks[0],report.Checks[0]]},state,request));
        GameQaInteractionTesting.ValidateReport(report with{Passed=false,Checks=[report.Checks[0] with{Satisfied=false}]},state,request);
    }
    [Fact]
    public async Task Test_script_paths_and_bytes_are_confined()
    {
        var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            await File.WriteAllTextAsync(Path.Combine(root,"test.cjs"),"await page.goto(origin);",new UTF8Encoding(false));
            Assert.Equal("await page.goto(origin);",await GameQaInteractionTesting.ReadScriptAsync(root,"test.cjs",default));
            await Assert.ThrowsAsync<ArgumentException>(()=>GameQaInteractionTesting.ReadScriptAsync(root,"../test.cjs",default));
            await Assert.ThrowsAsync<ArgumentException>(()=>GameQaInteractionTesting.ReadScriptAsync(root,Path.Combine(root,"test.cjs"),default));
            await File.WriteAllTextAsync(Path.Combine(root,"test.cjs"),new string('x',12001));
            await Assert.ThrowsAsync<ArgumentException>(()=>GameQaInteractionTesting.ReadScriptAsync(root,"test.cjs",default));
        } finally {Directory.Delete(root,true);}
    }
    [Fact]
    public void Runner_resources_are_packaged_and_retain_observations()
    {
        Assert.Contains("context.tracing.stop",GameQaInteractionTesting.Resource("qa-interactions.cjs"));
        Assert.Contains("extract('/input/build.zip'",GameQaInteractionTesting.Resource("qa-interactions.py"));
        Assert.Contains("test_web_interactions",GameQaExecution.Instructions);
        Assert.Contains("never hide it as a nonblocking limitation",GameQaExecution.Instructions);
    }
}
