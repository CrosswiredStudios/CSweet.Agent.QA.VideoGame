using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.Compute;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame;

internal sealed class GameQaBrowserTesting(WorkExecutionAssignment assignment, GameQaInputWorkspace source,
    GameQaJournal journal, AgentRuntimeContext context)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string key = "qa-browser:" + journal.Identity;
    private BrowserState? state;
    private int cursor;
    internal bool Pending { get; private set; }
    internal string Evidence { get; private set; } = "";
    internal bool Failed => state?.Report is { Passed: false };
    internal Guid? OperationId => state?.OperationId;
    internal Guid? EnvironmentId => state?.EnvironmentId;
    internal string? BuildSha256 => state?.BuildSha256;
    internal IReadOnlyList<WorkExecutionEvidence> ExecutionEvidence => state?.TestOperationId is { } operation
        ? [new("browser-compute-operation", "Playwright browser test operation", operation.ToString("D")),
           new("browser-build-sha256", "Exact tested static build", state.BuildSha256),
           new("browser-evidence-sha256", "Retained screenshots and traces", state.Report!.EvidenceSha256)] : [];

    internal async Task ResumeAsync(CancellationToken token)
    {
        state = (await context.Platform.ReadOperatingStateAsync<BrowserState>(key, token))?.Payload;
        if (state is null) return;
        cursor = state.Cursor;
        if (state.Commit != source.Commit || state.SourceIdentity != source.SourceIdentity)
            throw new InvalidOperationException("Retained browser testing belongs to different source files.");
        if (state.BuildSha256 is not { Length: 64 } || state.BuildSha256.Any(character => character is not (>= 'a' and <= 'f' or >= '0' and <= '9')) ||
            state.BuildBytes is < 1 or > 16 * 1024 * 1024 || state.UploadOffset < 0 || state.UploadOffset > state.BuildBytes ||
            state.EvidenceOffset < 0 || state.EvidenceOffset > 4 * 1024 * 1024)
            throw new InvalidOperationException("Retained browser build identity or transfer bounds are invalid.");
        ValidateWidths(state.Widths);
        await AdvanceAsync(token);
    }

    internal AIFunction Tool() => AIFunctionFactory.Create(async (string buildDirectory, IReadOnlyList<int> viewportWidths,
        bool requireWebGl, CancellationToken token) =>
    {
        if (state is null)
        {
            ValidateWidths(viewportWidths);
            await source.VerifyAsync(assignment, context, token);
            var bytes = await ArchiveAsync(source.Path, buildDirectory, token);
            await source.VerifyAsync(assignment, context, token);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            for (var offset = 0; offset < bytes.Length; offset += 18000)
            {
                var part = Convert.ToBase64String(bytes, offset, Math.Min(18000, bytes.Length - offset));
                await new RevisionSafeProjectState(context.Platform).MergeAsync<ArchivePart>($"qbb:{hash}:{offset}",
                    "video-game.qa-browser-build-part.v1", 1, current => current is null || current.Base64 == part
                        ? new(part) : throw new InvalidOperationException("Retained browser build bytes changed."),
                    new Dictionary<string, string>(), $"qbb:{hash}:{offset}", token);
            }
            state = new(source.Commit, source.SourceIdentity, hash, bytes.Length, viewportWidths.ToArray(), requireWebGl);
            await SaveAsync(token);
            await journal.RecordAsync("browser-build", buildDirectory,
                new { source.Commit, source.SourceIdentity, BuildSha256 = hash, BuildBytes = bytes.Length }, token);
        }
        else if (!state.Widths.SequenceEqual(viewportWidths) || state.RequireWebGl != requireWebGl)
            throw new InvalidOperationException("Resume the retained browser test with its original viewport and WebGL settings.");
        await AdvanceAsync(token);
        return new { pending = Pending, evidence = Evidence, operationId = OperationId,
            instruction = Pending ? "Compute is running. Finish this turn; the platform resumes QA automatically. Do not poll or claim a browser pass." : "Assess the observed browser results against the exact acceptance criteria." };
    }, "test_web_build", "Test the actual static web build from this assigned source in isolated compute with Playwright and Chromium. First build the project, then supply its workspace-relative output directory (for example dist), viewportWidths (normally [375,1280]) and requireWebGl. Source and build hashes are bound by code. Returns pending while transferring/testing; finish the turn without a verdict and QA resumes automatically. Screenshots and traces are retained in the QA evidence journal. This smoke test does not establish gameplay, FPS or device compatibility.");

    internal WorkExecutionOutcomeV1 WaitOutcome() => new(assignment.StageExecutionId, assignment.AttemptId,
        WorkExecutionDispositions.Completed, "awaiting-compute", "Independent QA is transferring or testing its exact web build in browser compute.",
        JsonSerializer.SerializeToElement(new { sourceCommitSha = source.Commit, buildSha256 = state!.BuildSha256,
            environmentId = state.EnvironmentId, operationIds = state.OperationId is { } operation ? new[] { operation } : [],
            retryAt = DateTimeOffset.UtcNow.AddMinutes(5) }, Json), [], []);

    private async Task SaveAsync(CancellationToken token)
    {
        var desired = state! with { Cursor = checked(cursor + 1) };
        var saved = await new RevisionSafeProjectState(context.Platform).MergeAsync<BrowserState>(key,
            "video-game.qa-browser-state.v1", 1, current => (current?.Cursor ?? 0) == cursor ? desired :
                JsonSerializer.Serialize(current, Json) == JsonSerializer.Serialize(desired, Json) ? current! :
                throw new InvalidOperationException("Browser testing advanced in another callback; resume its current state."),
            new Dictionary<string, string>(), $"{key}:state:{desired.Cursor}", token);
        state = saved.Payload; cursor = state.Cursor;
    }

    private async Task AdvanceAsync(CancellationToken token)
    {
        Pending = true;
        if (state!.Complete)
        {
            Evidence = "Observed browser compute evidence for this exact source and build:\n" + JsonSerializer.Serialize(state.Report, Json)
                + "\nScreenshots and traces are retained as browser-artifact observations in read_qa_evidence.";
            Pending = false;
            return;
        }
        var options = await context.Platform.Compute.GetProjectDefaultsAsync(assignment.WorkstreamId, token);
        if (options is not { State: "Ready", TemplateId: { } template } || options.WorkstreamId != assignment.WorkstreamId ||
            !options.Features.Contains("browser-testing-v1"))
            throw new InvalidOperationException("Browser testing requires the browser-testing-v1 compute image and approved project compute grants. Upgrade compute preparation before retrying QA.");
        if (state.EnvironmentId is null)
        {
            var desired = "qa-browser:" + assignment.WorkstreamId.ToString("N");
            var environment = await context.Platform.Compute.ProvisionAsync(new(assignment.WorkstreamId, desired, desired,
                new("linux", "x64", template, new(2, 2048, 20480), options.MaximumLifetimeSeconds)), token);
            state = state with { EnvironmentId = environment.Id };
            await SaveAsync(token);
        }
        var current = await context.Platform.Compute.ReadAsync(state.EnvironmentId.Value, token);
        if (current.DesiredState != "running" || current.State is "failed" or "destroyed" or "stopped")
            throw new InvalidOperationException("QA browser compute needs recovery: " + (current.FailureCode ?? current.State));
        if (state.Pending is { } request)
        {
            var operation = state.OperationId is { } operationId
                ? await context.Platform.Compute.ReadOperationAsync(operationId, token)
                : await SubmitAsync(request, token);
            if (state.OperationId is null) { state = state with { OperationId = operation.Id }; await SaveAsync(token); }
            if (operation.EnvironmentId != current.Id || operation.Generation != request.ExpectedGeneration + 1 ||
                current.Generation != operation.Generation)
                throw new InvalidOperationException("Browser compute returned a different operation identity.");
            if (operation.Status is "Pending" or "Dispatching" or "Reconciling") return;
            if (operation.Status != "Completed" || operation.Result is not
                { ErrorCode: null, Command: { ExitCode: 0, TimedOut: false, Truncated: false, ErrorCode: null } output } ||
                output.RequestId != request.RequestId)
                throw new InvalidOperationException("Browser testing could not complete; no pass was recorded: " +
                    (operation.Result?.Command?.StandardErrorText ?? operation.Result?.ErrorCode ?? operation.FailureCode));
            if (request.Kind == "test")
            {
                var report = JsonSerializer.Deserialize<BrowserReport>(output.StandardOutputText, Json) ?? throw new InvalidOperationException("Missing browser result.");
                ValidateReport(report, state, request.RequestId);
                await journal.RecordAsync("browser-result", "Playwright/Chromium exact-build smoke test", new { operation.Id, report }, token);
                state = state with { Report = report, TestOperationId = operation.Id };
            }
            if (request.Kind == "artifact")
            {
                var bytes = Convert.FromBase64String(output.StandardOutputText.Trim());
                var expected = Math.Min(4500, state.Report!.EvidenceBytes - state.EvidenceOffset);
                if (bytes.Length != expected) throw new InvalidOperationException("Browser evidence transfer was truncated.");
                var partKey = $"{key}:evidence:{state.EvidenceOffset}";
                await new RevisionSafeProjectState(context.Platform).MergeAsync<ArchivePart>(partKey,
                    "video-game.qa-browser-evidence-part.v1", 1, _ => new(Convert.ToBase64String(bytes)),
                    new Dictionary<string, string>(), partKey, token);
                state = state with { EvidenceOffset = state.EvidenceOffset + bytes.Length };
            }
            state = state with { Step = state.Step + 1, UploadOffset = request.NextOffset,
                Pending = null, OperationId = null };
            await SaveAsync(token);
            current = await context.Platform.Compute.ReadAsync(current.Id, token);
        }
        if (current.State != "ready") return;
        var root = "/var/lib/csweet-compute/work/browser-" + state.BuildSha256;
        BrowserRequest next;
        if (state.Step == 0)
            next = Command("prepare", $"mkdir -p {root}/evidence\n: > {root}/build.zip", state.UploadOffset);
        else if (state.UploadOffset < state.BuildBytes)
        {
            var part = await context.Platform.ReadOperatingStateAsync<ArchivePart>($"qbb:{state.BuildSha256}:{state.UploadOffset}", token)
                ?? throw new InvalidOperationException("The pinned browser build part is missing.");
            var count = Convert.FromBase64String(part.Payload.Base64).Length;
            next = Command("upload", $"printf '%s' '{part.Payload.Base64}' | base64 -d | dd of={root}/build.zip bs=1 seek={state.UploadOffset} conv=notrunc status=none",
                state.UploadOffset + count);
        }
        else if (state.Report is null)
        {
            var requestId = Guid.NewGuid();
            next = new(requestId, "test", current.Generation, $"{key}:test", JsonSerializer.SerializeToElement(new {
                browserTest = new { requestId, sourceCommitSha = state.Commit, buildSha256 = state.BuildSha256,
                    viewportWidths = state.Widths, requireWebGl = state.RequireWebGl } }, Json), state.UploadOffset);
        }
        else if (state.EvidenceOffset < state.Report.EvidenceBytes)
            next = Command("artifact", "python3 -c \"import base64; stream=open('" + root + "/evidence/evidence.zip','rb'); stream.seek(" +
                state.EvidenceOffset + "); print(base64.b64encode(stream.read(4500)).decode())\"", state.UploadOffset);
        else
        {
            using var evidence = new MemoryStream();
            for (var offset = 0; offset < state.Report.EvidenceBytes; offset += 4500)
            {
                var part = await context.Platform.ReadOperatingStateAsync<ArchivePart>($"{key}:evidence:{offset}", token)
                    ?? throw new InvalidOperationException("Browser evidence is missing.");
                evidence.Write(Convert.FromBase64String(part.Payload.Base64));
            }
            if (Convert.ToHexStringLower(SHA256.HashData(evidence.ToArray())) != state.Report.EvidenceSha256)
                throw new InvalidOperationException("Browser evidence digest changed during transfer.");
            await journal.RecordAsync("browser-artifact", "evidence.zip (base64; screenshots, traces and result.json)",
                new { state.Report.EvidenceSha256, Base64 = Convert.ToBase64String(evidence.ToArray()) }, token);
            state = state with { Complete = true }; await SaveAsync(token);
            await AdvanceAsync(token);
            return;
        }
        state = state with { Pending = next }; await SaveAsync(token);
        var submitted = await SubmitAsync(next, token);
        state = state with { OperationId = submitted.Id }; await SaveAsync(token);

        BrowserRequest Command(string kind, string script, int offset)
        {
            var requestId = Guid.NewGuid();
            return new(requestId, kind, current.Generation, $"{key}:step:{state.Step}", JsonSerializer.SerializeToElement(new {
                command = new ComputeCommand(requestId, "/bin/sh", "/var/lib/csweet-compute/work", ["-c", "set -eu\n" + script], 30, 8192) }, Json), offset);
        }
    }

    private Task<ComputeOperation> SubmitAsync(BrowserRequest request, CancellationToken token) =>
        context.Platform.InvokeAsync<object, ComputeOperation>(ComputeCapabilities.Execute,
            new { environmentId = state!.EnvironmentId, request.ExpectedGeneration, request.IdempotencyKey, workload = request.Workload }, token);

    internal static void ValidateWidths(IReadOnlyList<int> widths)
    {
        if (widths is not { Count: >= 1 and <= 4 } || widths.Any(width => width is < 240 or > 3840) || widths.Distinct().Count() != widths.Count)
            throw new ArgumentException("Use one to four distinct viewport widths between 240 and 3840.");
    }

    internal static async Task<byte[]> ArchiveAsync(string workspace, string relative, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative)) throw new ArgumentException("Use a workspace-relative build directory.");
        var root = System.IO.Path.GetFullPath(workspace);
        var directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (!directory.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) || !Directory.Exists(directory))
            throw new ArgumentException("The build directory must be inside this QA workspace.");
        for (var ancestor = directory; ancestor is not null; ancestor = System.IO.Path.GetDirectoryName(ancestor))
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Build paths cannot be links.");
        var files = new List<string>();
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.TryPop(out var next))
            foreach (var entry in Directory.EnumerateFileSystemEntries(next))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Build entries cannot be links.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry); else files.Add(entry);
                if (files.Count + pending.Count > 10000) throw new InvalidOperationException("Build entry limit exceeded.");
            }
        if (!File.Exists(System.IO.Path.Combine(directory, "index.html"))) throw new InvalidOperationException("Build output needs index.html.");
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            long total = 0;
            foreach (var file in files.Order(StringComparer.Ordinal))
            {
                total += new FileInfo(file).Length;
                if (total > 64 * 1024 * 1024) throw new InvalidOperationException("Expanded build exceeds 64 MiB.");
                var entry = archive.CreateEntry(System.IO.Path.GetRelativePath(directory, file).Replace('\\', '/'), CompressionLevel.Fastest);
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                await using var stream = entry.Open();
                await using var input = File.OpenRead(file); await input.CopyToAsync(stream, token);
            }
        }
        if (output.Length > 16 * 1024 * 1024) throw new InvalidOperationException("Compressed build exceeds 16 MiB.");
        return output.ToArray();
    }

    internal static void ValidateReport(BrowserReport report, BrowserState expected, Guid requestId)
    {
        if (report.RequestId != requestId || report.SourceCommitSha != expected.Commit || report.BuildSha256 != expected.BuildSha256 ||
            string.IsNullOrWhiteSpace(report.BrowserVersion) || report.Viewports is null ||
            !report.Viewports.Select(viewport => viewport.Width).SequenceEqual(expected.Widths) ||
            report.Viewports.Any(viewport => viewport.Height != 720 || viewport.Errors is null ||
                viewport.Passed != (viewport.Errors.Count == 0 && (!expected.RequireWebGl || viewport.Canvas && viewport.WebGl))) ||
            report.Passed != report.Viewports.All(viewport => viewport.Passed) || report.EvidenceBytes is < 1 or > 4 * 1024 * 1024 ||
            report.EvidenceSha256 is not { Length: 64 } || report.EvidenceSha256.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("Browser results do not bind the exact build, viewports and executed checks.");
    }

    internal sealed record ArchivePart(string Base64);
    internal sealed record BrowserState(string Commit, string SourceIdentity, string BuildSha256, int BuildBytes,
        IReadOnlyList<int> Widths, bool RequireWebGl, Guid? EnvironmentId = null, int Step = 0, int UploadOffset = 0,
        BrowserRequest? Pending = null, Guid? OperationId = null, BrowserReport? Report = null, int EvidenceOffset = 0, bool Complete = false,
        Guid? TestOperationId = null, int Cursor = 0);
    internal sealed record BrowserRequest(Guid RequestId, string Kind, long ExpectedGeneration, string IdempotencyKey, JsonElement Workload, int NextOffset);
    internal sealed record BrowserReport(Guid RequestId, string SourceCommitSha, string BuildSha256, string BrowserVersion,
        bool Passed, IReadOnlyList<ViewportResult> Viewports, string EvidenceSha256, int EvidenceBytes);
    internal sealed record ViewportResult(int Width, int Height, bool Passed, bool Canvas, bool WebGl, IReadOnlyList<string> Errors);
}
