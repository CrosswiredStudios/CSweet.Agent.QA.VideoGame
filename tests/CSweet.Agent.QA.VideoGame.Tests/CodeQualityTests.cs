using CSweet.Agent.SDK;
namespace CSweet.Agent.QA.VideoGame.Tests;
public sealed class CodeQualityTests
{
    [Fact]
    public void VerdictRequiresExactCommitAndExecutedTests()
    {
        var sha = new string('a', 40);
        var valid = new GameQaOutcome(sha, "Regression tested", true, [new("npm test", true, 0, "passed")], []);
        Assert.Equal("passed", GameQaExecution.ValidateOutcome(valid, sha));
        Assert.Throws<InvalidOperationException>(() => GameQaExecution.ValidateOutcome(valid, new string('b',40)));
        Assert.Throws<InvalidOperationException>(() => GameQaExecution.ValidateOutcome(valid with { Validations = [] }, sha));
        Assert.Throws<InvalidOperationException>(() => GameQaExecution.ValidateOutcome(valid with { Findings = ["Movement fails"] }, sha));
        Assert.Throws<InvalidOperationException>(() => GameQaExecution.ValidateOutcome(valid with { Validations = [new("npm test", false, 1, "failed")] }, sha));
        Assert.Equal("failed", GameQaExecution.ValidateOutcome(valid with { Passed = false, Findings = ["Movement fails"] }, sha));
    }
}
