using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.Compute;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class BrowserRecoveryTests
{
    [Fact]
    public async Task Export_actual_behavior_requests_for_platform_schema_acceptance()
    {
        var requested=Environment.GetEnvironmentVariable("CSWEET_QA_REQUEST_EXPORT");
        var path=requested??Path.GetTempFileName();
        try {
            await File.WriteAllTextAsync(path,"");
            await RunRecoveryAsync(false,"interaction-export",path);
            Assert.Equal(3,File.ReadAllLines(path).Length);
        }finally{if(requested is null)File.Delete(path);}
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Lost_browser_submission_reuses_the_exact_operation_and_retains_evidence_before_qa_resumes(bool lostBeforeAcceptance)
        => RunRecoveryAsync(lostBeforeAcceptance,"none");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Lost_behavioral_submission_reuses_exact_terms_and_retains_script_and_evidence(bool lostBeforeAcceptance)
        => RunRecoveryAsync(lostBeforeAcceptance,"interaction");

    [Fact]
    public Task Unknown_behavioral_execution_is_not_replayed() =>
        Assert.ThrowsAsync<InvalidOperationException>(()=>RunRecoveryAsync(false,"interaction-unknown"));

    [Theory]
    [InlineData("interaction-mismatch")]
    [InlineData("interaction-corrupt-evidence")]
    [InlineData("interaction-wrong-script")]
    public Task Behavioral_receipt_and_full_evidence_must_agree(string scenario)=>
        Assert.ThrowsAsync<InvalidOperationException>(()=>RunRecoveryAsync(false,scenario));

    [Theory]
    [InlineData("uploaded")]
    [InlineData("missing")]
    public Task Unknown_upload_requires_exact_read_back_before_continuing(string scenario) => RunRecoveryAsync(true,scenario);

    [Theory]
    [InlineData("conflicting")]
    [InlineData("browser")]
    public Task Unknown_browser_execution_or_conflicting_upload_never_replays(string scenario) =>
        Assert.ThrowsAsync<InvalidOperationException>(()=>RunRecoveryAsync(true,scenario));

    [Theory]
    [InlineData("unready-pending")]
    [InlineData("unready-failed")]
    [InlineData("unready-feature")]
    [InlineData("unready-provision")]
    [InlineData("unready-empty-identity")]
    public Task Preparation_failures_never_claim_queued_compute_and_recover_after_readiness(string scenario)
        => RunRecoveryAsync(false, scenario);

    private async Task RunRecoveryAsync(bool lostBeforeAcceptance,string scenario,string? export=null)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var states = new Dictionary<string, AgentOperatingStateResponse>();
        var workspaceId = Guid.NewGuid(); var itemId = Guid.NewGuid(); var environmentId = Guid.NewGuid();
        var commit = new string('a', 40); var project = Guid.NewGuid(); var repositoryId = Guid.NewGuid();
        var wrongHistory = false;
        var preparationReady = !scenario.StartsWith("unready-", StringComparison.Ordinal);
        var environment = new ComputeEnvironment(environmentId, 1, 1, "running", "ready", "ephemeral",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, null);
        var operations = new Dictionary<Guid, ComputeOperation>();
        var submissions = new Dictionary<string, (JsonElement Input, Guid Id)>();
        var browserLaunches = 0; var interactionLaunches = 0; var lostReply = false;
        var interactions = scenario.StartsWith("interaction",StringComparison.Ordinal);
        var unknownUpload = false; byte[] remotePart = []; var probes=0; var repairs=0;
        var evidence = Encoding.UTF8.GetBytes("fixture screenshots and trace bytes");
        byte[] interactionEvidence=[];
        var exported=new HashSet<string>();
        var runtime = new AgentTestRuntime()
            .RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                (read, _) => Task.FromResult(new AgentOperatingStateReadResponse(states.GetValueOrDefault(read.StateKey))))
            .RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (write, _) => {
                Assert.True(write.StateKey.Length <= 160); Assert.True(write.IdempotencyKey.Length <= 160);
                var response = new AgentOperatingStateResponse(Guid.NewGuid(), write.StateKey, write.SchemaId, write.SchemaVersion, write.Status,
                    write.SourceRevisions, write.ConditionCodes, write.DecisionFingerprint, write.OpenCommitmentCorrelations, write.AttentionReviewId,
                    write.Payload, (states.GetValueOrDefault(write.StateKey)?.Revision ?? 0) + 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                states[write.StateKey] = response; return Task.FromResult(response);
            })
            .RegisterCapability<PrepareGitWorkspaceRequest, GitWorkspaceResult>(GitWorkspaceCapabilities.Prepare,
                (_, _) => Task.FromResult(new GitWorkspaceResult(workspaceId, itemId, "/remote", repositoryId, "InternalGit", "Code", commit, "Ready", false)))
            .RegisterCapability<GitWorkspaceSyncRequest, GitWorkspaceSyncResult>(PlatformGitWorkspaceClient.SyncCapability,
                (_, _) => Task.FromResult(new GitWorkspaceSyncResult(SourceArchive())))
            .RegisterCapability<InspectGitWorkspaceRequest, GitWorkspaceInspection>(GitWorkspaceCapabilities.Inspect,
                (read, _) => Task.FromResult(new GitWorkspaceInspection(workspaceId, "Ready", false, [], []) {
                    History = read.IncludeHistory ? new(repositoryId, wrongHistory ? new string('c', 40) : commit, commit,
                        new('b', 40), "work/assigned", "main", new('b', 40), true,
                        [new(commit, [new string('b', 40)], "feat(task): source fixture", false)], null) : null }))
            .RegisterCapability<ProvisionComputeRequest, ComputeEnvironment>(ComputeCapabilities.Provision, (_, _) => {
                if (!preparationReady && scenario == "unready-provision") throw new UnauthorizedAccessException("Compute admission denied.");
                return Task.FromResult(!preparationReady && scenario == "unready-empty-identity" ? environment with { Id = Guid.Empty } : environment);
            })
            .RegisterCapability<JsonElement, JsonElement>(ComputeCapabilities.Read, (request, cancellation) => {
                object result = request.TryGetProperty("defaults", out _) ? new ComputeDefaults(!preparationReady && scenario == "unready-pending" ? "Pending" :
                    !preparationReady && scenario == "unready-failed" ? "Failed" : "Ready", project, "browser-fixture", null)
                    { Features = !preparationReady && scenario == "unready-feature" ? [] : ["browser-testing-v1"] } :
                    request.TryGetProperty("operationId", out var identity) ? operations[identity.GetGuid()] with { Status = "Completed" } : environment;
                return Task.FromResult(JsonSerializer.SerializeToElement(result, json));
            })
            .RegisterCapability<JsonElement, ComputeOperation>(ComputeCapabilities.Execute, (request, _) => {
                var key = request.GetProperty("idempotencyKey").GetString()!;
                if (submissions.TryGetValue(key, out var retained)) {
                    Assert.Equal(retained.Input.GetRawText(), request.GetRawText());
                    return Task.FromResult(operations[retained.Id]);
                }
                var workload = request.GetProperty("workload");
                var browser = workload.TryGetProperty("browserTest", out var specification);
                var command = browser ? null : workload.GetProperty("command").Deserialize<ComputeCommand>(json)!;
                if(export is not null && key.StartsWith("qai:",StringComparison.Ordinal) && command is not null && exported.Add(command.Executable))
                    File.AppendAllText(export,request.GetRawText()+Environment.NewLine);
                var behavior = command?.Executable == "/usr/bin/docker";
                if ((interactions?behavior:browser) && lostBeforeAcceptance && !lostReply) { lostReply = true; throw new IOException("Fixture lost submission before acceptance"); }
                var requestId = browser ? specification.GetProperty("requestId").GetGuid() : command!.RequestId;
                string output = "";
                if (browser) {
                    browserLaunches++;
                    output = JsonSerializer.Serialize(new GameQaBrowserTesting.BrowserReport(requestId, commit,
                        specification.GetProperty("buildSha256").GetString()!, "Chromium fixture", true,
                        [new(375, 720, true, true, true, []), new(1280, 720, true, true, true, [])],
                        Convert.ToHexStringLower(SHA256.HashData(evidence)), evidence.Length), json);
                }
                else if(behavior) {
                    interactionLaunches++;
                    Assert.Equal(8192,command!.MaximumOutputBytes);
                    Assert.Contains("--network=none",command!.Arguments);Assert.Contains("--read-only",command.Arguments);
                    Assert.Contains("--rm",command.Arguments);Assert.Contains("26s",command.Arguments);
                    Assert.Contains("--cap-drop=ALL",command.Arguments);Assert.Contains("--security-opt=no-new-privileges",command.Arguments);
                    var identity=JsonDocument.Parse(command.Arguments.Last()).RootElement;
                    var fullReport=new GameQaInteractionTesting.Report(requestId,commit,
                        identity.GetProperty("buildSha256").GetString()!,identity.GetProperty("scriptSha256").GetString()!,"Chromium fixture",true,
                        [new("audio",true,"Real fixture browser observation")],[],new('d',64),100);
                    using(var buffer=new MemoryStream()) {
                        using(var archive=new ZipArchive(buffer,ZipArchiveMode.Create,true)){
                            using var writer=new StreamWriter(archive.CreateEntry("result.json").Open());writer.Write(JsonSerializer.Serialize(fullReport,json));
                        }
                        interactionEvidence=buffer.ToArray();
                    }
                    var receipt=new GameQaInteractionTesting.Receipt(requestId,commit,fullReport.BuildSha256,fullReport.ScriptSha256,
                        fullReport.BrowserVersion,true,1,0,Convert.ToHexStringLower(SHA256.HashData(interactionEvidence)),interactionEvidence.Length);
                    if(scenario=="interaction-mismatch")receipt=receipt with{CheckCount=2};
                    if(scenario=="interaction-corrupt-evidence")receipt=receipt with{EvidenceSha256=new('e',64)};
                    if(scenario=="interaction-wrong-script")receipt=receipt with{ScriptSha256=new('e',64)};
                    output=JsonSerializer.Serialize(receipt,json);
                }
                else if (command!.Arguments.Last().Contains("evidence.zip", StringComparison.Ordinal)) output = Convert.ToBase64String(key.StartsWith("qai:",StringComparison.Ordinal)?interactionEvidence:evidence);
                else if(key.Contains(":verify-upload:",StringComparison.Ordinal))
                {
                    probes++;
                    output=JsonSerializer.Serialize(new GameQaBrowserTesting.UploadProbe(remotePart.Length,
                        Convert.ToHexStringLower(SHA256.HashData(remotePart))),json);
                }
                else if(key.Contains(":repair-upload:",StringComparison.Ordinal))
                {
                    repairs++;
                    var match=System.Text.RegularExpressions.Regex.Match(command!.Arguments.Last(),"b64decode\\('([^']+)'\\)");
                    remotePart=Convert.FromBase64String(match.Groups[1].Value);
                }
                else if(command!.Arguments.Last().Contains("base64 -d",StringComparison.Ordinal))
                {
                    var match=System.Text.RegularExpressions.Regex.Match(command.Arguments.Last(),"printf '%s' '([^']+)'");
                    remotePart=Convert.FromBase64String(match.Groups[1].Value);
                    if(!unknownUpload && scenario is "uploaded" or "missing" or "conflicting")
                    {
                        unknownUpload=true;
                        if(scenario == "missing") remotePart=[];
                        if(scenario == "conflicting") remotePart=[1,2,3];
                    }
                }
                environment = environment with { Generation = environment.Generation + 1 };
                var operation = new ComputeOperation(Guid.NewGuid(), environmentId, environment.Generation, "Pending", null,
                    new(Command: new(requestId, 0, false, Encoding.UTF8.GetBytes(output), [])));
                if ((unknownUpload && probes == 0 && !browser && command!.Arguments.Last().Contains("base64 -d",StringComparison.Ordinal)) ||
                    browser && scenario == "browser" || behavior && scenario=="interaction-unknown") operation=operation with { Result=new(ErrorCode:"outcome-unknown") };
                operations[operation.Id] = operation; submissions[key] = (request.Clone(), operation.Id);
                if ((interactions?behavior:browser) && !lostBeforeAcceptance && !lostReply) { lostReply = true; throw new IOException("Fixture lost submission reply after acceptance"); }
                return Task.FromResult(operation);
            });
        var context = runtime.CreateContext();
        var assignment = JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with { ItemId = itemId, WorkstreamId = project,
            Scope = WorkExecutionScopes.Task, SprintId = Guid.NewGuid(), SprintExecutionId = Guid.NewGuid(), PolicyRevisionId = Guid.NewGuid(),
            StageKey = "quality", StageExecutionId = Guid.NewGuid(), AttemptId = Guid.NewGuid(), AssignmentRevision = 1 };
        var input = new WorkExecutionInputV1(Guid.NewGuid(), itemId, 1, new(["Validate"], ["Boots in browser"]));
        var delivery = JsonSerializer.Deserialize<WorkItemDeliverySpecification>("{}")! with { DeliveryKind = "Code" };
        var root = PlatformGitWorkspaceClient.LocalWorkspacePath(workspaceId);
        try {
            await using var source = await GameQaInputWorkspace.PrepareAsync(assignment, delivery, context, default);
            Directory.CreateDirectory(Path.Combine(source.Path, "dist"));
            await File.WriteAllTextAsync(Path.Combine(source.Path, "dist", "index.html"), "<canvas></canvas>");
            var journal = new GameQaJournal(assignment, input, source.SourceIdentity, context.Platform);
            var historyTool = source.HistoryTool(assignment, context, journal)!;
            Assert.Equal("read_assigned_git_history", historyTool.Name);
            await historyTool.InvokeAsync(new AIFunctionArguments { ["offset"] = 0, ["limit"] = 20 });
            Assert.Contains("git-history", await journal.ReadEvidenceAsync(default));
            wrongHistory = true;
            await Assert.ThrowsAnyAsync<Exception>(async () => await historyTool.InvokeAsync(new AIFunctionArguments { ["offset"] = 0, ["limit"] = 20 }));
            wrongHistory = false;
            await Assert.ThrowsAnyAsync<Exception>(async () => await historyTool.InvokeAsync(new AIFunctionArguments { ["offset"] = -1, ["limit"] = 20 }));
            var testing = new GameQaBrowserTesting(assignment, source, journal, context);
            Assert.Throws<InvalidOperationException>(() => testing.WaitOutcome());
            if (!preparationReady)
            {
                await Assert.ThrowsAnyAsync<Exception>(async () => await testing.Tool().InvokeAsync(
                    new AIFunctionArguments { ["buildDirectory"] = "dist", ["viewportWidths"] = new[] { 375, 1280 }, ["requireWebGl"] = true }));
                Assert.False(testing.Pending);
                Assert.Throws<InvalidOperationException>(() => testing.WaitOutcome());
                Assert.Empty(submissions);
                Assert.Equal(0, browserLaunches);
                preparationReady = true;
                testing = new GameQaBrowserTesting(assignment with { AttemptId = Guid.NewGuid() }, source, journal, context);
                await testing.ResumeAsync(default);
            }
            else
            await testing.Tool().InvokeAsync(new AIFunctionArguments { ["buildDirectory"] = "dist", ["viewportWidths"] = new[] { 375, 1280 }, ["requireWebGl"] = true });
            Assert.True(testing.Pending);
            Assert.Equal("awaiting-compute", testing.WaitOutcome().OutcomeCode);
            for (var turn = 0; turn < 18 && testing.Pending; turn++) {
                testing = new GameQaBrowserTesting(assignment with { AttemptId = Guid.NewGuid() }, source, journal, context);
                try { await testing.ResumeAsync(default); }
                catch (IOException) when (lostReply) { continue; }
            }
            Assert.False(testing.Pending);
            Assert.Equal(1, browserLaunches);
            Assert.Equal(scenario == "none" || interactions || scenario.StartsWith("unready-", StringComparison.Ordinal) ? 0 : 1,probes);
            Assert.Equal(scenario == "missing" ? 1 : 0,repairs);
            Assert.Contains("Observed browser compute evidence", testing.Evidence);
            Assert.Equal(3, testing.ExecutionEvidence.Count);
            Assert.Contains("browser-artifact", await journal.ReadEvidenceAsync(default));
            if(interactions) {
                await File.WriteAllTextAsync(Path.Combine(source.Path,"checks.cjs"),"await page.goto(origin); check('audio', true, 'fixture browser measurement');"+
                    (scenario=="interaction-export"?"\n// "+new string('x',11600):""));
                var behavior=new GameQaInteractionTesting(assignment,source,journal,testing,context);
                await behavior.Tool().InvokeAsync(new AIFunctionArguments{["testScriptPath"]="checks.cjs"});
                for(var turn=0;turn<10 && behavior.Pending;turn++) {
                    behavior=new GameQaInteractionTesting(assignment with{AttemptId=Guid.NewGuid()},source,journal,testing,context);
                    try{await behavior.ResumeAsync(default);}catch(IOException) when(lostReply){continue;}
                }
                Assert.False(behavior.Pending);Assert.False(behavior.Failed);Assert.Equal(1,interactionLaunches);
                Assert.Contains("Observed independent Chromium behavior",behavior.Evidence);
                Assert.Contains("browser-check-artifact",await journal.ReadEvidenceAsync(default));
                for(var plan=2;plan<=5;plan++) {
                    await File.AppendAllTextAsync(Path.Combine(source.Path,"checks.cjs"),"\n// A separate focused check plan "+plan);
                    await behavior.Tool().InvokeAsync(new AIFunctionArguments{["testScriptPath"]="checks.cjs"});
                    for(var turn=0;turn<10 && behavior.Pending;turn++) {
                        behavior=new GameQaInteractionTesting(assignment with{AttemptId=Guid.NewGuid()},source,journal,testing,context);
                        await behavior.ResumeAsync(default);
                    }
                    Assert.False(behavior.Pending);Assert.Equal(plan,interactionLaunches);
                }
                var scripts=new GameQaBrowserScripts(assignment,source,journal,context);
                var body="// A shutdown event name is script data, not a shell command.\nawait page.goto(origin);";
                var saved=await scripts.Tool().InvokeAsync(new AIFunctionArguments{["script"]=body});
                var savedPath=JsonSerializer.SerializeToElement(saved).GetProperty("testScriptPath").GetString()!;
                Assert.Equal(body,await File.ReadAllTextAsync(Path.Combine(source.Path,savedPath)));
                File.Delete(Path.Combine(source.Path,savedPath));
                await journal.RecordAsync("command",JsonSerializer.Serialize(new{command="cd /old/workspace && cat > qa/legacy.mjs <<'EOF'\nawait page.goto(origin);\nEOF\necho written"}),"exit_code: 0",default);
                await scripts.RestoreAsync(default);
                Assert.Equal(body,await File.ReadAllTextAsync(Path.Combine(source.Path,savedPath)));
                Assert.Equal("await page.goto(origin);",await File.ReadAllTextAsync(Path.Combine(source.Path,"qa","legacy.mjs")));
                await File.AppendAllTextAsync(Path.Combine(source.Path,"src","game.js"),"changed");
                await Assert.ThrowsAsync<InvalidOperationException>(()=>behavior.Tool().InvokeAsync(new AIFunctionArguments{["testScriptPath"]="checks.cjs"}).AsTask());
            }
            Assert.DoesNotContain(states.Values, state => state.Payload.TryGetProperty("Outcome", out _));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

        static byte[] SourceArchive()
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true)) {
                using var writer = new StreamWriter(archive.CreateEntry("src/game.js").Open()); writer.Write("source fixture");
            }
            return output.ToArray();
        }
    }
}
