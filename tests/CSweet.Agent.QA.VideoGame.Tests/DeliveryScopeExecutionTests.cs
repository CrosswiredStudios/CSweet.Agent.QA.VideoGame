using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class DeliveryScopeExecutionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    [Theory]
    [InlineData(true, false, false)] [InlineData(false, false, false)]
    [InlineData(true, true, false)] [InlineData(false, true, false)]
    [InlineData(true, false, true)] [InlineData(false, false, true)]
    public async Task Aggregate_harness_reserves_report_corrections_after_early_stop_or_iteration_limit(bool passed, bool exhaustIterations, bool exhaustReportReads)
    {
        var repository = Guid.NewGuid(); var commit = new string('a',40);
        var candidate = new WorkDeliveryCandidate(new string('d',64),1,[new(repository,"source","target",commit,commit,commit)],[]);
        var input = new WorkExecutionInputV1(Guid.NewGuid(),null,1,new(["Inspect immutable source"],["Observed criterion"]));
        var assignment = JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with { ExecutionId=Guid.NewGuid(),StageExecutionId=Guid.NewGuid(),
            AttemptId=Guid.NewGuid(),Scope="Release",StageKey="quality",DeliveryPlanId=Guid.NewGuid(),Candidate=candidate,
            AgentInstallationId=Guid.NewGuid(),Input=JsonSerializer.SerializeToElement(input,Json) };
        using var stream = new MemoryStream();
        using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
        using(var writer=new StreamWriter(zip.CreateEntry("game.js").Open())) writer.Write("original source");
        var bytes=stream.ToArray(); var states=new Dictionary<string,AgentOperatingStateResponse>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<ReadWorkDeliveryEvidenceRequest,WorkDeliveryEvidenceResponse>(WorkDeliveryCapabilities.Evidence,
                (request,_)=>Task.FromResult(request.RepositoryId is null ? new(candidate,[]) :
                    new WorkDeliveryEvidenceResponse(candidate,[],bytes,Convert.ToHexStringLower(SHA256.HashData(bytes)),commit)))
            .RegisterCapability<AgentOperatingStateReadRequest,AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (read,_)=>Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(read.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest,AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite,(write,_)=> {
                var response=new AgentOperatingStateResponse(Guid.NewGuid(),write.StateKey,write.SchemaId,write.SchemaVersion,write.Status,
                    write.SourceRevisions,write.ConditionCodes,write.DecisionFingerprint,write.OpenCommitmentCorrelations,write.AttentionReviewId,
                    write.Payload,(states.GetValueOrDefault(write.StateKey)?.Revision??0)+1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);
                states[write.StateKey]=response;return Task.FromResult(response);
            });
        var context=runtime.CreateContext();
        var journal=new GameQaJournal(assignment,input,candidate.Digest,context.Platform);
        // Simulate a retained, observed check before a model stops without submitting.
        await journal.RecordAsync("command","independent fixture check",new { output="Observed fixture check result" },default);
        using var client = new Client(repository,passed,exhaustIterations,exhaustReportReads);
        var result=await SpecialistAgent.RunDeliveryScopeAsync(assignment,context,client,default);
        var outcome=result.Value!.Value.Deserialize<WorkExecutionOutcomeV1>(Json)!;
        Assert.True(outcome.Disposition == "Completed", outcome.Summary);
        Assert.Equal(passed?"approved":"changes_requested",outcome.OutcomeCode);
        Assert.True(client.Finalized);
        Assert.NotNull(await journal.ReadResumeReportAsync(default));
        Assert.Contains("Observed fixture check result",await journal.ReadEvidenceAsync(default));
        var resumed=assignment with { StageExecutionId=Guid.NewGuid(),AttemptId=Guid.NewGuid(),AssignmentRevision=100 };
        Assert.Equal(journal.Identity,new GameQaJournal(resumed,input,candidate.Digest,context.Platform).Identity);
        Assert.Equal(GameQaInteractionTesting.StateKey(assignment,journal.Identity),GameQaInteractionTesting.StateKey(resumed,journal.Identity));
        Assert.NotEqual(journal.Identity,new GameQaJournal(resumed with {ExecutionId=Guid.NewGuid()},input,candidate.Digest,context.Platform).Identity);
        Assert.NotEqual(journal.Identity,new GameQaJournal(resumed with {AgentInstallationId=Guid.NewGuid()},input,candidate.Digest,context.Platform).Identity);
        Assert.NotEqual(journal.Identity,new GameQaJournal(resumed,input,"different-candidate",context.Platform).Identity);
        Assert.NotEqual(journal.Identity,new GameQaJournal(resumed,input with {Planning=new(["Inspect"],["Changed criterion"])},candidate.Digest,context.Platform).Identity);
    }
    private sealed class Client(Guid repository,bool passed,bool exhaustIterations,bool exhaustReportReads):IChatClient
    {
        public bool Finalized;
        private int submissions;
        public void Dispose(){} public object? GetService(Type type,object? key=null)=>null;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,CancellationToken cancellationToken=default)
        {
            if (options?.ResponseFormat is not null)
            {
                Assert.Empty(options.Tools!);Finalized=true;
                // The first reserved submission contradicts its failed command; the second
                // must receive feedback and preserve the negative verdict without more reads.
                var report = Arguments(exhaustReportReads && submissions++==0 ? true : passed);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,JsonSerializer.Serialize(report))));
            }
            return GetStreamingResponseAsync(messages,options,cancellationToken).ToChatResponseAsync(cancellationToken);
        }
        private Dictionary<string,object?> Arguments(bool verdict) => new() {
            ["summary"]="Observed fixture check result",["passed"]=verdict,
            ["validations"]=new[]{new GitValidationResult($"check repositories/{repository:N}/game.js",passed,passed?0:1,"Observed fixture check result")},
            ["findings"]=passed?Array.Empty<string>():new[]{"Observed defect"},
            ["criteria"]=new[]{new GameQaCriterion("Observed criterion",passed,"Observed fixture check result")}
        };
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,ChatOptions? options=null,
            [EnumeratorCancellation] CancellationToken cancellationToken=default)
        {
            await Task.Yield(); Assert.Equal(128000, options!.MaxOutputTokens);
            var names=(options.Tools ?? []).OfType<AIFunction>().Select(t=>t.Name).ToArray();
            if(names.Contains("execute_workspace_command")) {
                Assert.Contains("test_web_build",names);Assert.Contains("test_web_interactions",names);
                Assert.Contains("save_browser_test_script",names);Assert.Contains("read_qa_evidence",names);
                if (exhaustIterations)
                    yield return new(ChatRole.Assistant,[new FunctionCallContent(Guid.NewGuid().ToString(),"read_qa_evidence",
                        new Dictionary<string,object?> { ["observationIndex"]=0, ["page"]=0 })]);
                else yield return new(ChatRole.Assistant,"I inspected the observed result but stopped before submitting.");
                yield break;
            }
            if (!names.Contains("submit_qa_report")) { yield return new(ChatRole.Assistant,"Evaluation budget reached."); yield break; }
            if (exhaustReportReads && names.Contains("read_qa_evidence"))
            {
                yield return new(ChatRole.Assistant,[new FunctionCallContent(Guid.NewGuid().ToString(),"read_qa_evidence",
                    new Dictionary<string,object?> { ["observationIndex"]=0, ["page"]=0 })]);
                yield break;
            }
            Assert.DoesNotContain("execute_workspace_command",names);Assert.DoesNotContain("test_web_build",names);
            Assert.DoesNotContain("file_access_write",names);
            if (exhaustReportReads) Assert.Equal("submit_qa_report", Assert.Single(names));
            else Assert.Contains("read_qa_evidence",names);
            Assert.All(names, name => Assert.Contains(name, new[] {"submit_qa_report", "read_qa_evidence", "read_repository_qa_evidence"}));
            Finalized=true;
            yield return new(ChatRole.Assistant,[new FunctionCallContent("report","submit_qa_report",new Dictionary<string,object?> {
                ["summary"]="Observed fixture check result",["passed"]=exhaustReportReads && submissions++==0 ? true : passed,
                ["validations"]=new[]{new GitValidationResult($"check repositories/{repository:N}/game.js",passed,passed?0:1,"Observed fixture check result")},
                ["findings"]=passed?Array.Empty<string>():new[]{"Observed defect"},
                ["criteria"]=new[]{new GameQaCriterion("Observed criterion",passed,"Observed fixture check result")} })]);
        }
    }
}
