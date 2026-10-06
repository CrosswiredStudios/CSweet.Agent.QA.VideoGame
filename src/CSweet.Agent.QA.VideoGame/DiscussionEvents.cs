using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;
using TicketConversations;

namespace CSweet.Agent.QA.VideoGame;

public sealed partial class SpecialistAgent
{
    private Task<IChatClient> DiscussionClientAsync(AgentRuntimeContext context, CancellationToken token) =>
        Task.FromResult(context.CreateChatClient(new AgentLlmSelection(
            Settings.GetGuid("llmProviderId") ?? throw new InvalidOperationException("Configure a discussion provider."), Settings.GetString("llmModel"))));

    private Task RecoverDiscussionAsync(AgentRuntimeContext context, CancellationToken token) =>
        Discussion.RecoverAsync(context, ct => DiscussionClientAsync(context, ct), token);
    public override async Task HandleEventAsync(AgentEventEnvelope message, AgentRuntimeContext context, CancellationToken token)
    {
        if (message.EventType == Discussion.Changed)
        {
            await Discussion.HandleAsync(message, context, ct => DiscussionClientAsync(context, ct), token);
            return;
        }
        await base.HandleEventAsync(message, context, token);
    }
    public override async Task HandleAttentionReviewAsync(AgentAttentionReviewContext review, AgentRuntimeContext context, CancellationToken token)
    {
        await RecoverDiscussionAsync(context, token);
        await base.HandleAttentionReviewAsync(review, context, token);
    }
}

