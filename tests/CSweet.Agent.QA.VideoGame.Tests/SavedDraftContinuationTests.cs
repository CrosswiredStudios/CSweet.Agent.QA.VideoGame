using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.Compute;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class SavedDraftContinuationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Saved_source_bound_test_runs_before_model_reporting_and_pending_or_unknown_execution_never_replays(bool unknown)
    {
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var repository=Guid.NewGuid();var commit=new string('a',40);var project=Guid.NewGuid();
        var candidate=new WorkDeliveryCandidate(new string('d',64),1,[new(repository,"source","target",commit,commit,commit)],[]);
        var input=new WorkExecutionInputV1(project,null,1,new(["Validate"],["Chromium canvas renders"]));
        var assignment=JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with {ExecutionId=Guid.NewGuid(),StageExecutionId=Guid.NewGuid(),
            AttemptId=Guid.NewGuid(),Scope="Release",StageKey="quality",DeliveryPlanId=Guid.NewGuid(),WorkstreamId=project,
            Candidate=candidate,AgentInstallationId=Guid.NewGuid(),Input=JsonSerializer.SerializeToElement(input,json)};
        using var buffer=new MemoryStream();
        using(var zip=new ZipArchive(buffer,ZipArchiveMode.Create,true))
        using(var writer=new StreamWriter(zip.CreateEntry("game.js").Open()))writer.Write("immutable source");
        var bytes=buffer.ToArray();var states=new Dictionary<string,AgentOperatingStateResponse>();
        var environment=new ComputeEnvironment(Guid.NewGuid(),1,1,"running","ready","ephemeral",DateTimeOffset.UtcNow,DateTimeOffset.MaxValue,null);
        ComputeOperation? operation=null;var submissions=0;
        var runtime=new AgentTestRuntime()
            .RegisterCapability<ReadWorkDeliveryEvidenceRequest,WorkDeliveryEvidenceResponse>(WorkDeliveryCapabilities.Evidence,
                (request,_)=>Task.FromResult(request.RepositoryId is null?new(candidate,[]):
                    new WorkDeliveryEvidenceResponse(candidate,[],bytes,Convert.ToHexStringLower(SHA256.HashData(bytes)),commit)))
            .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (request,_)=>Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(request.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,(write,_)=> {
                var response=new AgentOperatingStateResponse(Guid.NewGuid(),write.StateKey,write.SchemaId,write.SchemaVersion,write.Status,
                    write.SourceRevisions,write.ConditionCodes,write.DecisionFingerprint,write.OpenCommitmentCorrelations,write.AttentionReviewId,
                    write.Payload,(states.GetValueOrDefault(write.StateKey)?.Revision??0)+1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);
                states[write.StateKey]=response;return Task.FromResult(response);
            })
            .RegisterCapability<JsonElement,JsonElement>(ComputeCapabilities.Read,(request,cancellation)=>Task.FromResult(
                JsonSerializer.SerializeToElement(request.TryGetProperty("operationId",out _) ? (object)operation! : environment,json)))
            .RegisterCapability<JsonElement,ComputeOperation>(ComputeCapabilities.Execute,(request,_)=> {
                Assert.Equal(environment.Generation,request.GetProperty("expectedGeneration").GetInt64());submissions++;
                environment=environment with {Generation=environment.Generation+1};
                operation=new(Guid.NewGuid(),environment.Id,environment.Generation,"Pending",null,null);
                return Task.FromResult(operation);
            });
        var context=runtime.CreateContext();
        await using var workspace=await DeliveryCandidateWorkspace.MaterializeAsync(assignment,context,default);
        await using var source=await GameQaInputWorkspace.FromCandidateAsync(workspace,repository,commit,default);
        var journal=new GameQaJournal(assignment,input,source.SourceIdentity,context.Platform);
        var browser=new GameQaBrowserTesting.BrowserState(commit,source.SourceIdentity,new string('b',64),100,[1280],false,
            EnvironmentId:environment.Id,Report:new(Guid.NewGuid(),commit,new string('b',64),"Chromium fixture",true,
                [new(1280,720,true,true,true,[])],new string('c',64),100),Complete:true);
        await new RevisionSafeProjectState(context.Platform).MergeAsync<GameQaBrowserTesting.BrowserState>("qa-browser:"+journal.Identity,
            "video-game.qa-browser-state.v1",1,_=>browser,new Dictionary<string,string>(),"saved-browser-fixture",default);
        await new GameQaBrowserScripts(assignment,source,journal,context).Tool().InvokeAsync(new AIFunctionArguments {
            ["script"]="await page.goto(origin); check('canvas', await page.locator('canvas').count() === 1, 'observed canvas count');" });
        // Each real callback receives a newly materialized immutable snapshot, without old QA files.
        await using var replacement=await DeliveryCandidateWorkspace.MaterializeAsync(assignment,context,default);
        var aggregate=new DeliveryScopeBrowserTools(assignment,input,context);
        await aggregate.InitializeAsync(replacement,default);
        Assert.True(aggregate.Pending);Assert.Equal(1,submissions);
        var wait=aggregate.WaitOutcome();Assert.Equal(candidate.Digest,wait.Output.GetProperty("candidateDigest").GetString());
        Assert.Equal(operation!.Id,Assert.Single(wait.Output.GetProperty("operationIds").Deserialize<Guid[]>()!));
        var next=assignment with {StageExecutionId=Guid.NewGuid(),AttemptId=Guid.NewGuid(),AssignmentRevision=100};
        if(unknown)operation=operation with {Status="Completed",Result=new(ErrorCode:"outcome-unknown")};
        await using var reconnect=await DeliveryCandidateWorkspace.MaterializeAsync(next,context,default);
        var resumed=new DeliveryScopeBrowserTools(next,input,context);
        await resumed.InitializeAsync(reconnect,default);
        // A lost result is a harness error the model sees; the same script is never replayed.
        if(unknown){Assert.False(resumed.Pending);Assert.Contains("not replayed",resumed.ReportEvidence());}
        else Assert.True(resumed.Pending);
        Assert.Equal(1,submissions);
        var foreign=new DeliveryScopeBrowserTools(next with {ExecutionId=Guid.NewGuid()},input,context);
        await using var other=await DeliveryCandidateWorkspace.MaterializeAsync(next with {ExecutionId=Guid.NewGuid()},context,default);
        await foreign.InitializeAsync(other,default);Assert.False(foreign.Pending);Assert.Equal(1,submissions);
    }
}
