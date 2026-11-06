using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class ModelCapacityTests
{
#pragma warning disable MAAI001
    [Theory]
    [InlineData(256000, 128000, false)]
    [InlineData(32000, 8000, true)]
    public async Task Tool_history_compacts_at_configured_input_capacity_rather_than_early_task_thresholds(
        int contextTokens, int outputTokens, bool shouldCompact)
    {
        var root = Path.Combine(Path.GetTempPath(), "qa-capacity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var shell = GameQaHarness.CreateShell(root);
            var options = GameQaHarness.CreateOptions("QA", root, shell, null, contextTokens, outputTokens);
            Assert.Equal(contextTokens, options.MaxContextWindowTokens);
            Assert.Equal(outputTokens, options.MaxOutputTokens);
            var history = new CompactionMessageIndex([], null);
            const string scope = "Every accepted criterion remains mandatory.";
            history.AddGroup(CompactionGroupKind.User, [new(ChatRole.User, scope)], 0);
            for (var index = 0; index < 14; index++)
                history.AddGroup(CompactionGroupKind.ToolCall, [
                    new(ChatRole.Assistant, [new FunctionCallContent("call-" + index, "inspect", new Dictionary<string, object?>())]),
                    new(ChatRole.Tool, [new FunctionResultContent("call-" + index, new string('x', 14_000))])], 0);
            var before = history.IncludedTokenCount;
            Assert.InRange(before, 24_001, 127_999);
            Assert.Equal(shouldCompact, await options.CompactionStrategy!.CompactAsync(history));
            Assert.Contains(history.GetIncludedMessages(), x => x.Role == ChatRole.User && x.Text == scope);
            if (!shouldCompact) Assert.Equal(before, history.IncludedTokenCount);
            else Assert.True(history.IncludedTokenCount < before);
        }
        finally { Directory.Delete(root, true); }
    }
#pragma warning restore MAAI001
}
