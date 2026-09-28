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
    [InlineData("nested")]
    [InlineData("flat")]
    [InlineData("encoded")]
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
            if (calls == 1 || format == "repair" && calls == 2)
            {
                var arguments = WireArguments(format == "repair" ? "encoded" : format);
                if (format == "repair" && calls == 1) arguments["criteria"] = "[]";
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
        var arguments = WireArguments("encoded");
        arguments["passed"] = invalid;
        var result = Assert.IsType<GameQaReport.Submission>(await GameQaReport.CreateTool(root, Sha, ["Record desktop fps"]).InvokeAsync(arguments));
        Assert.False(result.Saved);
        Assert.Equal("report.invalid-arguments", result.Code);
        Assert.False(File.Exists(Path.Combine(root, ".csweet", "qa-outcome.json")));
    }

    [Fact]
    public async Task EncodedFailedReportPreservesEveryFindingAndCommand()
    {
        Directory.CreateDirectory(root);
        await GameQaReport.CreateTool(root, Sha, ["Record desktop fps"]).InvokeAsync(WireArguments("encoded"));
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
        await tool.InvokeAsync(new AIFunctionArguments { ["report"] = System.Text.Json.JsonSerializer.SerializeToElement(
            report, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) });
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
            .InvokeAsync(new AIFunctionArguments { ["report"] = report });
        var error = Assert.IsType<GameQaReport.Submission>(result);
        Assert.False(error.Saved);
        Assert.Equal("report.invalid-evidence", error.Code);
        Assert.Equal(new[] { "Record desktop fps" }, error.RequiredCriteria);
        Assert.False(File.Exists(Path.Combine(root, ".csweet", "qa-outcome.json")));
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
#pragma warning restore MAAI001
        Assert.True(options.DisableToolAutoApproval);
        Assert.Contains(options.ChatOptions!.Tools!, x => x is AIFunction f && f.Name == "submit_qa_report");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
