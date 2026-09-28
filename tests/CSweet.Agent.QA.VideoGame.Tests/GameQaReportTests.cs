using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class GameQaReportTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "qa-report-" + Guid.NewGuid().ToString("N"));
    private static readonly string Sha = new('a', 40);
    private static GameQaOutcome Report(bool passed = false) => new(Sha, "Static checks pass; desktop measurement is unavailable", passed,
        [new("python3 tools/static_validate.py", true, 0, "24 checks passed")], passed ? [] : ["Desktop fps measurement is missing"])
        { Criteria = [new("Record desktop fps", passed, "No browser available; no fps number claimed")] };

    [Fact]
    public async Task ToolSavesNegativeVerdictAndLiteralShellTextWithoutExecutingIt()
    {
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.txt");
        await File.WriteAllTextAsync(source, "original source");
        var report = Report() with { Findings = ["Do not run sudo; git push; $(touch sentinel); /proc is unavailable"] };
        var tool = GameQaReport.CreateTool(root, Sha, ["Record desktop fps"]);
        Assert.Contains("report", tool.JsonSchema.ToString());
        await tool.InvokeAsync(new AIFunctionArguments { ["report"] = report });
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
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await GameQaReport.CreateTool(root, Sha, ["Record desktop fps"])
                .InvokeAsync(new AIFunctionArguments { ["report"] = report }));
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
