using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.QA.VideoGame;

/// <summary>
/// Missing browser compute is infrastructure, not a product defect. Super Awesome Games (2026-11-10): Prism Break #20
/// was failed back to engineering three times because QA's browser compute was not prepared; no defect was found.
/// QA now waits without a verdict and the platform resumes it, never sending the ticket back to the engineer.
/// </summary>
internal static class BrowserComputeReadiness
{
    internal const string OutcomeCode = "awaiting-compute-preparation";
    internal const string Feature = "browser-testing-v1";
    internal static readonly TimeSpan Recheck = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Null when the project's browser compute can run a test now; otherwise the owner-actionable reason.</summary>
    internal static async Task<string?> UnavailableReasonAsync(Guid workstreamId, AgentRuntimeContext context, CancellationToken token)
    {
        try
        {
            var options = await context.Platform.Compute.GetProjectDefaultsAsync(workstreamId, token);
            return Reason(options?.State, options?.ErrorCode, options is { State: "Ready", TemplateId: not null } &&
                options.WorkstreamId == workstreamId && options.Features.Contains(Feature));
        }
        catch (PlatformCapabilityException error) when (error.Code is not PlatformCapabilityErrorCode.Denied)
        {
            return Reason("unavailable", error.Message, false);
        }
    }

    internal static string? Reason(string? state, string? errorCode, bool ready) => ready ? null :
        $"Browser compute preparation is not ready ({state ?? "unavailable"}; {errorCode ?? Feature + " unavailable"}). " +
        "Complete Linux preparation on the project's Compute page; QA resumes automatically and the ticket does not return to engineering.";

    internal static WorkExecutionOutcomeV1 WaitOutcome(WorkExecutionAssignment assignment, string reason) =>
        new(assignment.StageExecutionId, assignment.AttemptId, WorkExecutionDispositions.Completed, OutcomeCode,
            "Waiting for browser compute: " + reason,
            JsonSerializer.SerializeToElement(new { reason, retryAt = DateTimeOffset.UtcNow.Add(Recheck) }, Json), [], []);
}
