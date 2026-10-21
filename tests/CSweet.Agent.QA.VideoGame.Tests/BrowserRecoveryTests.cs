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
    public async Task Lost_browser_submission_reuses_the_exact_operation_and_retains_evidence_before_qa_resumes()
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var states = new Dictionary<string, AgentOperatingStateResponse>();
        var workspaceId = Guid.NewGuid(); var itemId = Guid.NewGuid(); var environmentId = Guid.NewGuid();
        var commit = new string('a', 40); var project = Guid.NewGuid();
        var environment = new ComputeEnvironment(environmentId, 1, 1, "running", "ready", "ephemeral",
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, null);
        var operations = new Dictionary<Guid, ComputeOperation>();
        var submissions = new Dictionary<string, (JsonElement Input, Guid Id)>();
        var browserLaunches = 0; var lostReply = false;
        var evidence = Encoding.UTF8.GetBytes("fixture screenshots and trace bytes");
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
                (_, _) => Task.FromResult(new GitWorkspaceResult(workspaceId, itemId, "/remote", Guid.NewGuid(), "InternalGit", "Code", commit, "Ready", false)))
            .RegisterCapability<GitWorkspaceSyncRequest, GitWorkspaceSyncResult>(PlatformGitWorkspaceClient.SyncCapability,
                (_, _) => Task.FromResult(new GitWorkspaceSyncResult(SourceArchive())))
            .RegisterCapability<InspectGitWorkspaceRequest, GitWorkspaceInspection>(GitWorkspaceCapabilities.Inspect,
                (_, _) => Task.FromResult(new GitWorkspaceInspection(workspaceId, "Ready", false, [], [])))
            .RegisterCapability<ProvisionComputeRequest, ComputeEnvironment>(ComputeCapabilities.Provision, (_, _) => Task.FromResult(environment))
            .RegisterCapability<JsonElement, JsonElement>(ComputeCapabilities.Read, (request, cancellation) => {
                object result = request.TryGetProperty("defaults", out _) ? new ComputeDefaults("Ready", project, "browser-fixture", null) { Features = ["browser-testing-v1"] } :
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
                var requestId = browser ? specification.GetProperty("requestId").GetGuid() : command!.RequestId;
                string output = "";
                if (browser) {
                    browserLaunches++;
                    output = JsonSerializer.Serialize(new GameQaBrowserTesting.BrowserReport(requestId, commit,
                        specification.GetProperty("buildSha256").GetString()!, "Chromium fixture", true,
                        [new(375, 720, true, true, true, []), new(1280, 720, true, true, true, [])],
                        Convert.ToHexStringLower(SHA256.HashData(evidence)), evidence.Length), json);
                }
                else if (command!.Arguments.Last().Contains("evidence.zip", StringComparison.Ordinal)) output = Convert.ToBase64String(evidence);
                environment = environment with { Generation = environment.Generation + 1 };
                var operation = new ComputeOperation(Guid.NewGuid(), environmentId, environment.Generation, "Pending", null,
                    new(Command: new(requestId, 0, false, Encoding.UTF8.GetBytes(output), [])));
                operations[operation.Id] = operation; submissions[key] = (request.Clone(), operation.Id);
                if (browser && !lostReply) { lostReply = true; throw new IOException("Fixture lost submission reply after acceptance"); }
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
            var testing = new GameQaBrowserTesting(assignment, source, journal, context);
            await testing.Tool().InvokeAsync(new AIFunctionArguments { ["buildDirectory"] = "dist", ["viewportWidths"] = new[] { 375, 1280 }, ["requireWebGl"] = true });
            Assert.True(testing.Pending);
            Assert.Equal("awaiting-compute", testing.WaitOutcome().OutcomeCode);
            for (var turn = 0; turn < 12 && testing.Pending; turn++) {
                testing = new GameQaBrowserTesting(assignment with { AttemptId = Guid.NewGuid() }, source, journal, context);
                try { await testing.ResumeAsync(default); }
                catch (IOException) when (lostReply) { continue; }
            }
            Assert.False(testing.Pending);
            Assert.Equal(1, browserLaunches);
            Assert.Contains("Observed browser compute evidence", testing.Evidence);
            Assert.Equal(3, testing.ExecutionEvidence.Count);
            Assert.Contains("browser-artifact", await journal.ReadEvidenceAsync(default));
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
