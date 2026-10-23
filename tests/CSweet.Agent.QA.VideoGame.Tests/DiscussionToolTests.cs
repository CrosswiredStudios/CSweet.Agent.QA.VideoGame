using System.Text.Json;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class DiscussionToolTests
{
    [Fact]
    public void Reader_confines_items_and_uses_paged_full_comment_reads()
    {
        var assignment = JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with { ItemId = Guid.NewGuid(), BoardId = Guid.NewGuid() };
        var dependency = Guid.NewGuid();
        var delivery = new WorkItemDeliverySpecification(Guid.NewGuid(), [], []) { DependencyItemIds = [dependency] };
        var own = GameQaDiscussion.Request(assignment, delivery, "", 1);
        Assert.Equal(assignment.ItemId, own.ItemId); Assert.Equal(assignment.BoardId, own.BoardId); Assert.Equal(20, own.PageSize);
        Assert.Equal(dependency, GameQaDiscussion.Request(assignment, delivery, dependency.ToString(), 2).ItemId);
        Assert.Throws<UnauthorizedAccessException>(() => GameQaDiscussion.Request(assignment, delivery, Guid.NewGuid().ToString(), 1));
        Assert.Throws<ArgumentException>(() => GameQaDiscussion.Request(assignment, delivery, "bad", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GameQaDiscussion.Request(assignment, delivery, "", 0));
    }

    [Fact]
    public async Task Tool_reads_fresh_authorized_state_on_every_call_and_preserves_long_bodies()
    {
        var assignment = JsonSerializer.Deserialize<WorkExecutionAssignment>("{}")! with { ItemId = Guid.NewGuid(), BoardId = Guid.NewGuid() };
        var body = new string('x', 16000); var calls = 0;
        var tool = GameQaDiscussion.Tool(assignment, new(Guid.Empty, [], []), (request, token) => {
            calls++;
            var comment = JsonSerializer.Deserialize<WorkItemComment>("{}")! with { Body = body + calls };
            return Task.FromResult(new WorkItemCommentPage([comment], request.Page, request.PageSize, false, calls));
        });
        var first = JsonSerializer.Serialize(await tool.InvokeAsync(new AIFunctionArguments { ["itemId"] = "", ["page"] = 1 }));
        var second = JsonSerializer.Serialize(await tool.InvokeAsync(new AIFunctionArguments { ["itemId"] = "", ["page"] = 1 }));
        Assert.Equal(2, calls); Assert.Contains(body + "1", first); Assert.Contains(body + "2", second);
    }
}
