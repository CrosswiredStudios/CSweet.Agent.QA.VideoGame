using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class GameQaUnattendedTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualHarnessCanSavePlanAndReportWithoutInteractiveWrites(bool staleToolChoice)
    {
        var root=Path.Combine(Path.GetTempPath(),"qa-unattended-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            await using var shell=GameQaHarness.CreateShell(root);using var client=new Client(staleToolChoice);
            var harness=client.AsHarnessAgent(GameQaHarness.CreateOptions("QA",root,shell,null,
                reportTool:GameQaReport.CreateTool(root,new string('a',40),["Observed"])));
            var session=await harness.CreateSessionAsync();var response=await harness.RunAsync("Save the test plan then submit the observed result.",session);
            Assert.DoesNotContain(response.Messages.SelectMany(m=>m.Contents),c=>c is ToolApprovalRequestContent);
            Assert.True(File.Exists(Path.Combine(root,".csweet","qa-test-plan.md")));
            Assert.True((await GameQaReport.ReadAsync(root,default)).Passed);
            Assert.False(File.Exists(Path.Combine(root,"unsafe.md")));
        } finally {Directory.Delete(root,true);}
    }

    [Fact]
    public async Task ObservedCommandsAndValidatedReportSurviveAnotherAttemptButNotAnotherSourceOrPlanning()
    {
        var states=new Dictionary<string,AgentOperatingStateResponse>();
        var runtime=new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (read,_)=>Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(read.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,(write,_)=>{
                Assert.True(write.Payload.GetRawText().Length<=128000);Assert.True(write.StateKey.Length<=160);Assert.True(write.IdempotencyKey.Length<=160);
                var response=new AgentOperatingStateResponse(Guid.NewGuid(),write.StateKey,write.SchemaId,write.SchemaVersion,write.Status,
                    write.SourceRevisions,write.ConditionCodes,write.DecisionFingerprint,write.OpenCommitmentCorrelations,write.AttentionReviewId,
                    write.Payload,(states.GetValueOrDefault(write.StateKey)?.Revision??0)+1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);
                states[write.StateKey]=response;return Task.FromResult(response);
            });
        var assignment=JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with {ItemId=Guid.NewGuid(),StageKey="specialist-execution",AttemptId=Guid.NewGuid(),PlanningRevision=2};
        var input=new WorkExecutionInputV1(Guid.NewGuid(),Guid.NewGuid(),2,new(["Validate"],["Observed"]));
        var context=runtime.CreateContext();var journal=new GameQaJournal(assignment,input,"exact-files",context.Platform);
        var command=journal.Observe(AIFunctionFactory.Create(()=>"exit=0; 12 actual checks passed","execute_workspace_command"));
        await command.InvokeAsync(new AIFunctionArguments());
        await journal.RecordAsync("command","large retained output",new string('\u00f6',128000),default);
        Assert.True(states.Keys.Count(x=>x.Contains(":part:",StringComparison.Ordinal))>30);
        var page=Assert.IsType<JsonElement>(await journal.CreateReadTool().InvokeAsync(new AIFunctionArguments { ["observationIndex"]=1,["page"]=1 }));
        Assert.True(page.GetProperty("found").GetBoolean());Assert.True(page.GetProperty("pages").GetInt32()>1);
        var report=new GameQaOutcome(new string('a',40),"Observed",true,[new("actual check",true,0,"12 passed")],[]) {Criteria=[new("Observed",true,"Observed actual output")]};
        await journal.SaveReportAsync(report,default);
        var oldIndex=(await context.Platform.ReadOperatingStateAsync<GameQaJournal.Index>(journal.Key,default))!;
        var oldPayload=oldIndex.Payload with {Entries=oldIndex.Payload.Entries.Take(oldIndex.Payload.Entries.Count-1).ToArray(),ReportAttemptId=null};
        states[journal.Key]=states[journal.Key] with {Payload=JsonSerializer.SerializeToElement(oldPayload)};
        var next=new GameQaJournal(assignment with {AttemptId=Guid.NewGuid()},input,"exact-files",context.Platform);
        Assert.Contains("12 actual checks passed",await next.ReadEvidenceAsync(default));Assert.Equal(report.SourceCommitSha,(await next.ReadReportAsync(default))!.SourceCommitSha);
        Assert.NotNull(await journal.ReadResumeReportAsync(default));
        Assert.Null(await next.ReadResumeReportAsync(default));
        Assert.Contains("Observed actual output",await next.ReadEvidenceAsync(default));
        var changedReport=report with {Passed=false,Summary="This retry found a missing browser check",Findings=["New observation"]};
        await next.SaveReportAsync(changedReport,default);
        Assert.False((await next.ReadResumeReportAsync(default))!.Passed);
        Assert.Null(await journal.ReadResumeReportAsync(default));
        Assert.Contains("Observed actual output",await next.ReadEvidenceAsync(default));
        Assert.Null(await new GameQaJournal(assignment,input,"different-files",context.Platform).ReadReportAsync(default));
        Assert.Null(await new GameQaJournal(assignment,input with {Planning=new(["Validate"],["Changed criterion"])},"exact-files",context.Platform).ReadReportAsync(default));
    }

    private sealed class Client(bool staleToolChoice):IChatClient
    {
        private int calls;
        public void Dispose(){} public object? GetService(Type serviceType,object? serviceKey=null)=>null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)=>
            GetStreamingResponseAsync(messages,options,cancellationToken).ToChatResponseAsync(cancellationToken);
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,
            [EnumeratorCancellation]CancellationToken cancellationToken=default)
        {
            await Task.Yield();var names=options!.Tools!.OfType<AIFunction>().Select(f=>f.Name).ToArray();
            Assert.Contains("file_access_read",names);Assert.DoesNotContain("file_access_write",names);Assert.DoesNotContain("file_access_edit",names);
            var step=calls++;
            if(staleToolChoice && step==0)yield return new(ChatRole.Assistant,[new FunctionCallContent("old-tool","file_access_write",new Dictionary<string,object?> { ["fileName"]="unsafe.md",["content"]="Forbidden generic write" })]);
            else if(step==(staleToolChoice?1:0))yield return new(ChatRole.Assistant,[new FunctionCallContent("plan","save_qa_test_plan",new Dictionary<string,object?> { ["markdown"]="# Test plan\nExecute the observed checks for the assigned source." })]);
            else if(step==(staleToolChoice?2:1))yield return new(ChatRole.Assistant,[new FunctionCallContent("report","submit_qa_report",new Dictionary<string,object?> {
                ["sourceCommitSha"]=new string('a',40),["summary"]="Actual result observed",["passed"]=true,
                ["validations"]=new[]{new GitValidationResult("actual check",true,0,"Observed pass")},["findings"]=Array.Empty<string>(),
                ["criteria"]=new[]{new GameQaCriterion("Observed",true,"Actual result observed")} })]);
            else yield return new(ChatRole.Assistant,"Evidence saved.");
        }
    }
}
