using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class DeliveryScopeEvidenceTests
{
    private readonly Guid first = Guid.NewGuid(), second = Guid.NewGuid();
    private readonly string digest = new('d', 64), commit = new('a', 40), otherCommit = new('b', 40);
    private WorkDeliveryCandidate Candidate() => new(digest, 1,
        [new(first, "source", "target", commit, commit, commit), new(second, "source", "target", otherCommit, otherCommit, otherCommit)], []);
    private GameQaOutcome Report(bool passed = true) => new(digest, "Observed checks", passed,
        [new($"npm --prefix repositories/{first:N} test", true, 0, "12 tests passed")], passed ? [] : ["A test failed"])
        { Criteria = [new("Test both repositories", passed, "Observed command output")] };

    [Fact]
    public async Task Missing_repository_gets_tool_feedback_before_save_and_corrected_evidence_binds_exact_commits()
    {
        var root = Path.Combine(Path.GetTempPath(), "qa-scope-" + Guid.NewGuid().ToString("N"));
        try
        {
            var tool = GameQaReport.CreateTool(root, digest, ["Test both repositories"],
                validateEvidence: report => { _ = DeliveryScopeEvidence.Bind(report, Candidate()); });
            AIFunctionArguments Arguments(GameQaOutcome report) => new(JsonSerializer.SerializeToElement(report,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)).EnumerateObject().Where(x => x.Name != "sourceCommitSha")
                .ToDictionary(x => x.Name, x => (object?)x.Value));
            var report = Report();
            var feedback = Assert.IsType<GameQaReport.Submission>(await tool.InvokeAsync(Arguments(report)));
            Assert.False(feedback.Saved);
            Assert.Contains("EVERY candidate repository", feedback.Message);
            Assert.False(File.Exists(Path.Combine(root, ".csweet", "qa-outcome.json")));
            report = report with { Validations = [..report.Validations, new($"npm --prefix repositories/{second:N} test", true, 0, "7 tests passed")] };
            await tool.InvokeAsync(Arguments(report));
            var evidence = DeliveryScopeEvidence.Bind(await GameQaReport.ReadAsync(root, default), Candidate());
            Assert.Equal(commit, evidence.Single(x => x.RepositoryId == first).CommitSha);
            Assert.Equal(otherCommit, evidence.Single(x => x.RepositoryId == second).CommitSha);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unbound_and_ambiguous_commands_cannot_be_relabelled_as_repository_evidence(bool ambiguous)
    {
        var command = ambiguous ? $"diff repositories/{first:N} repositories/{second:N}" : "npm test";
        var report = Report(false) with { Validations = [new(command, false, 1, "Actual failure")] };
        Assert.Throws<InvalidOperationException>(() => DeliveryScopeEvidence.Bind(report, Candidate()));
    }

    [Fact]
    public void Genuine_rejection_preserves_observed_failure_without_inventing_unexecuted_repository_checks()
    {
        var report = Report(false) with { Validations = [new($"npm --prefix repositories/{first:N} test", false, 1, "Movement assertion failed")] };
        var evidence = Assert.Single(DeliveryScopeEvidence.Bind(report, Candidate()));
        Assert.False(evidence.Succeeded);
        Assert.Equal(1, evidence.ExitCode);
        Assert.Equal("Movement assertion failed", evidence.Output);
    }
}
