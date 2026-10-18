using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class GameQaReportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "qa-report-" + Guid.NewGuid().ToString("N"));
    private static readonly string Sha = new('a', 40);
    private static GameQaOutcome Report(bool passed = false) => new(Sha, "Static checks pass; desktop measurement is unavailable", passed,
        [new("python3 tools/static_validate.py", true, 0, "24 checks passed")], passed ? [] : ["Desktop fps measurement is missing"])
        { Criteria = [new("Record desktop fps", passed, "No browser available; no fps number claimed")] };

    [Theory]
    [InlineData(4096)]
    [InlineData(16000)]
    public async Task FullQaSummaryIsRetainedWhileStageStatusFitsStorage(int length)
    {
        var full = new string('s', length);
        await GameQaReport.CreateTool(root, Sha, ["Record desktop fps"]).InvokeAsync(Flat(Report(true) with { Summary = full }));
        var saved = await GameQaReport.ReadAsync(root, default);
        Assert.Equal(full, saved.Summary);
        Assert.True(GameQaExecution.StatusSummary(saved.Summary).Length <= 4096);
        if (length > 4096) Assert.Contains("full QA report", GameQaExecution.StatusSummary(saved.Summary));
    }

    [Fact]
    public async Task PassingReportGetsCombinedActionableBlockerAndCoverageFeedback()
    {
        var report = Report(true) with { Findings = ["[Non-blocking] Optional audible check"],
            Criteria = [new("Extra verification step", true, "Observed")] };
        var result = Assert.IsType<GameQaReport.Submission>(await GameQaReport.CreateTool(root, Sha, ["Record desktop fps"]).InvokeAsync(Flat(report)));
        Assert.False(result.Saved);
        Assert.Contains("passed=true requires findings=[]", result.Message);
        Assert.Contains("nonblocking observations", result.Message);
        Assert.Contains("criterion", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new[] { "Record desktop fps" }, result.RequiredCriteria);
        Assert.False(File.Exists(Path.Combine(root, ".csweet", "qa-outcome.json")));
    }

    [Fact]
    public async Task NonblockingNotesRemainInSummaryWithoutInventingOrRemovingACriterion()
    {
        var report = Report(true) with { Summary = "Required criterion verified. Optional browser playback was not executed; follow-up remains documented." };
        var result = Assert.IsType<JsonElement>(await GameQaReport.CreateTool(root, Sha, ["Record desktop fps"]).InvokeAsync(Flat(report)));
        Assert.True(result.GetProperty("saved").GetBoolean());
        Assert.Equal(report.Summary, (await GameQaReport.ReadAsync(root, default)).Summary);
    }

    [Fact]
    public async Task RepeatedInvalidReportsCancelTheActualHarnessAfterThreeCorrections()
    {
        Directory.CreateDirectory(root);
        using var budget = new GameQaReport.CorrectionBudget(default);
        await using var shell = GameQaHarness.CreateShell(root);
        using var client = new ReportClient("exhaust");
        var harness = client.AsHarnessAgent(GameQaHarness.CreateOptions("QA", root, shell, null,
            reportTool: GameQaReport.CreateTool(root, Sha, ["Record desktop fps"], budget)));
        var session = await harness.CreateSessionAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.RunAsync("Submit report", session, cancellationToken: budget.Token));
        Assert.True(budget.Exhausted);
        Assert.Contains("No QA verdict was accepted", budget.FailureMessage);
        Assert.Equal(4, client.Calls);
        Assert.False(File.Exists(Path.Combine(root, ".csweet", "qa-outcome.json")));
    }

    [Theory]
    [InlineData("flat")]
    [InlineData("repair")]
    public async Task ReportSubmissionWorksThroughTheActualHarness(string format)
    {
        Directory.CreateDirectory(root);
        await using var shell = GameQaHarness.CreateShell(root);
        using var client = new ReportClient(format);
        var harness = client.AsHarnessAgent(GameQaHarness.CreateOptions("QA", root, shell, null,
            reportTool: GameQaReport.CreateTool(root, Sha, ["Record desktop fps"])));
        var session = await harness.CreateSessionAsync();
        var response = await harness.RunAsync("Submit the actual QA evidence.", session);
        Assert.DoesNotContain(response.Messages.SelectMany(x => x.Contents), x => x is ToolApprovalRequestContent);
        Assert.False((await GameQaReport.ReadAsync(root, default)).Passed);
    }

    private sealed class ReportClient(string format) : IChatClient
    {
        private int calls;
        public int Calls => calls;
        public void Dispose() { }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var result in messages.SelectMany(x => x.Contents).OfType<FunctionResultContent>())
                Assert.DoesNotContain("Error", result.Result?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
            calls++;
            if (format == "exhaust" || calls == 1 || format == "repair" && calls == 2)
            {
                var arguments = WireArguments("flat");
                if (format == "exhaust") arguments["passed"] = true;
                if (format == "repair" && calls == 1) arguments["criteria"] = JsonSerializer.SerializeToElement(Array.Empty<object>());
                if (format == "repair" && calls == 2)
                {
                    var feedback = messages.SelectMany(x => x.Contents).OfType<FunctionResultContent>().Last();
                    Assert.Contains("report.invalid-evidence", JsonSerializer.Serialize(feedback.Result));
                    Assert.Contains("Record desktop fps", JsonSerializer.Serialize(feedback.Result));
                }
                yield return new(ChatRole.Assistant, [new FunctionCallContent("report-" + calls, "submit_qa_report", arguments)]);
            }
            else yield return new(ChatRole.Assistant, "QA report submitted.");
        }
    }

    private static AIFunctionArguments Flat(GameQaOutcome report) => new(JsonSerializer.SerializeToElement(report,
        new JsonSerializerOptions(JsonSerializerDefaults.Web)).EnumerateObject().ToDictionary(x => x.Name, x => (object?)x.Value.Clone()));
    private static AIFunctionArguments WireArguments(string format)
    {
        var report = JsonSerializer.SerializeToElement(Report(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (format == "nested") return new() { ["report"] = report };
        return new(report.EnumerateObject().ToDictionary(x => x.Name, x => format == "encoded"
            ? (object?)(x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() : x.Value.GetRawText())
            : x.Value.Clone()));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("\"false\"")]
    public async Task InvalidBooleanEncodingCannotBecomeAPassingVerdict(string invalid)
    {
        Directory.CreateDirectory(root);
        var arguments = WireArguments("flat");
        arguments["passed"] = invalid;
        var result = Assert.IsType<GameQaReport.Submission>(await GameQaReport.CreateTool(root, Sha, ["Record desktop fps"]).InvokeAsync(arguments));
        Assert.False(result.Saved);
        Assert.Equal("report.invalid-arguments", result.Code);
        Assert.False(File.Exists(Path.Combine(root, ".csweet", "qa-outcome.json")));
    }

    [Fact]
    public async Task FlatFailedReportPreservesEveryFindingAndCommand()
    {
        Directory.CreateDirectory(root);
        await GameQaReport.CreateTool(root, Sha, ["Record desktop fps"]).InvokeAsync(WireArguments("flat"));
        var saved = await GameQaReport.ReadAsync(root, default);
        Assert.Equal(JsonSerializer.Serialize(Report()), JsonSerializer.Serialize(saved));
    }

    [Fact]
    public async Task ToolSavesNegativeVerdictAndLiteralShellTextWithoutExecutingIt()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.txt");
        await File.WriteAllTextAsync(source, "original source");
        var report = Report() with { Findings = ["Do not run sudo; git push; $(touch sentinel); /proc is unavailable"] };
        var tool = GameQaReport.CreateTool(root, Sha, ["Record desktop fps"]);
        Assert.True(tool.JsonSchema.GetProperty("properties").TryGetProperty("sourceCommitSha", out _));
        Assert.False(tool.JsonSchema.GetProperty("properties").TryGetProperty("report", out _));
        // Model tool arguments arrive as JSON, not preconstructed CLR records.
        await tool.InvokeAsync(Flat(report));
        var saved = await GameQaReport.ReadAsync(root, default);
        Assert.False(saved.Passed);
        Assert.Equal(report.Findings, saved.Findings);
        Assert.Equal("original source", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(Path.Combine(root, "sentinel")));
        Assert.Equal("failed", GameQaExecution.ValidateOutcome(saved, Sha));
    }

    [Theory]
    [InlineData("commit")]
    [InlineData("coverage")]
    [InlineData("false-pass")]
    public async Task ToolRejectsInvalidEvidenceBeforeWriting(string fault)
    {
        Directory.CreateDirectory(root);
        var report = fault switch
        {
            "commit" => Report() with { SourceCommitSha = new string('b', 40) },
            "coverage" => Report() with { Criteria = [] },
            _ => Report() with { Passed = true }
        };
        var result = await GameQaReport.CreateTool(root, Sha, ["Record desktop fps"])
            .InvokeAsync(Flat(report));
        var error = Assert.IsType<GameQaReport.Submission>(result);
        Assert.False(error.Saved);
        Assert.Equal("report.invalid-evidence", error.Code);
        Assert.Equal(new[] { "Record desktop fps" }, error.RequiredCriteria);
        Assert.False(File.Exists(Path.Combine(root, ".csweet", "qa-outcome.json")));
    }

    [Fact]
    public async Task ASecondSubmissionCannotOverwriteTheFirstValidatedEvidence()
    {
        var tool=GameQaReport.CreateTool(root,Sha,["Record desktop fps"]);
        await tool.InvokeAsync(Flat(Report(false)));
        var second=Assert.IsType<GameQaReport.Submission>(await tool.InvokeAsync(Flat(Report(true))));
        Assert.Equal("already-saved",second.Code);
        Assert.False((await GameQaReport.ReadAsync(root,default)).Passed);
    }

    [Fact]
    public async Task MissingAndMalformedReportsHaveSpecificDiagnosticsAndOldEvidenceIsRemoved()
    {
        Directory.CreateDirectory(Path.Combine(root, ".csweet"));
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => GameQaReport.ReadAsync(root, default));
        Assert.Contains("without submitting", missing.Message);
        var path = Path.Combine(root, ".csweet", "qa-outcome.json");
        await File.WriteAllTextAsync(path, "{invalid");
        var malformed = await Assert.ThrowsAsync<InvalidOperationException>(() => GameQaReport.ReadAsync(root, default));
        Assert.Contains("malformed", malformed.Message);
        GameQaReport.Reset(root);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task HarnessAllowsConfinedReadsAndReportSubmissionButNotGeneralWrites()
    {
        Directory.CreateDirectory(root);
        await using var shell = GameQaHarness.CreateShell(root);
        var options = GameQaHarness.CreateOptions("QA", root, shell, null,
            reportTool: GameQaReport.CreateTool(root, Sha, ["Record desktop fps"]));
#pragma warning disable MAAI001
        Assert.True(options.FileAccessProviderOptions!.DisableReadOnlyToolApproval);
        Assert.False(options.FileAccessProviderOptions.DisableWriteToolApproval);
        Assert.True(options.FileAccessProviderOptions.DisableWriteTools);
#pragma warning restore MAAI001
        Assert.True(options.DisableToolAutoApproval);
        Assert.Contains(options.ChatOptions!.Tools!, x => x is AIFunction f && f.Name == "submit_qa_report");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
