using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class GameQaArtifactInputTests
{
    [Theory]
    [InlineData("report")]
    [InlineData("changed")]
    [InlineData("revoked")]
    [InlineData("archive-hash")]
    public async Task ArtifactInputUsesExactEvidenceWithoutGitWriteCapabilities(string condition)
    {
        var repository=Guid.NewGuid(); var plan=Guid.NewGuid(); var execution=Guid.NewGuid(); var commit=new string('a',40);
        var candidate=new WorkDeliveryCandidate("input-digest",1,[new(repository,"story","story",commit,commit,commit)],[]);
        var assignment=JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with {
            Scope="Task",ItemId=Guid.NewGuid(),SprintId=Guid.NewGuid(),SprintExecutionId=Guid.NewGuid(),PolicyRevisionId=Guid.NewGuid(),
            DeliveryPlanId=plan,ExecutionId=execution,Candidate=candidate };
        using var stream=new MemoryStream();
        using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
        using(var writer=new StreamWriter(zip.CreateEntry("src/game.js").Open())) writer.Write("original");
        var archive=stream.ToArray(); var calls=0; var revoked=false;
        // No Git capabilities are registered: this must never prepare, inspect, upload or publish a code workspace.
        var runtime=new AgentTestRuntime().RegisterCapability<ReadWorkDeliveryEvidenceRequest,WorkDeliveryEvidenceResponse>(
            WorkDeliveryCapabilities.Evidence,(request,_)=> {
                Assert.Equal(plan,request.PlanId);Assert.Equal(execution,request.ExecutionId);calls++;
                if(revoked) throw new InvalidOperationException("Repository authority revoked");
                return Task.FromResult(request.RepositoryId.HasValue
                    ?new WorkDeliveryEvidenceResponse(candidate,[],archive,condition=="archive-hash"?"wrong":Convert.ToHexStringLower(SHA256.HashData(archive)),commit)
                    :new(candidate,[]));
            });
        var context=runtime.CreateContext();
        var delivery=new WorkItemDeliverySpecification(Guid.Empty,["Inspect"],["Observed"]) {DeliveryKind="Artifact",DeliveryPlanId=plan};
        if(condition=="archive-hash") {
            await Assert.ThrowsAsync<InvalidOperationException>(()=>GameQaInputWorkspace.PrepareAsync(assignment,delivery,context,default));return;
        }
        await using var input=await GameQaInputWorkspace.PrepareAsync(assignment,delivery,context,default);
        Assert.Equal(commit,input.Commit);Assert.Equal(1,input.SourceCount);
        if(condition=="changed") await File.WriteAllTextAsync(Path.Combine(input.Path,"src","game.js"),"changed");
        if(condition=="revoked") revoked=true;
        if(condition=="report") {
            Directory.CreateDirectory(Path.Combine(input.Path,".csweet"));
            await File.WriteAllTextAsync(Path.Combine(input.Path,".csweet","qa-outcome.json"),"{}");
            await input.VerifyAsync(assignment,context,default);await input.CleanupAsync(assignment,context,default);
            Assert.Equal(3,calls);
        }
        else await Assert.ThrowsAsync<InvalidOperationException>(()=>input.VerifyAsync(assignment,context,default));
    }
}
