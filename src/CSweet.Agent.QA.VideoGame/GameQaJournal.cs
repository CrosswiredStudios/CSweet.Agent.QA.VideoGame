using System.Security.Cryptography;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame;

/// <summary>Observed results are durable evidence, not an automatic QA pass or permission grant.</summary>
internal sealed class GameQaJournal(WorkExecutionAssignment assignment, WorkExecutionInputV1 input, string sourceIdentity,
    PlatformCapabilityClient platform)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly RevisionSafeProjectState store = new(platform);
    internal string Identity { get; } = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
        assignment.OrganizationId, assignment.WorkstreamId, assignment.BoardId, assignment.ItemId, assignment.Scope,
        assignment.DeliveryPlanId, assignment.ScopeRevision, assignment.StageKey,
        AssignmentRevision = assignment.Scope is "Release" or "Epic" or "Story" ? 0 : assignment.AssignmentRevision,
        assignment.PlanningRevision, input.Planning,
        sourceIdentity = assignment.Scope is "Release" or "Epic" or "Story"
            ? sourceIdentity + ":delivery:" + assignment.ExecutionId.ToString("N") + ":" + assignment.AgentInstallationId + ":" + assignment.OrganizationUserId
            : sourceIdentity }, Json)));
    internal string Key => "game-qa-evidence:" + Identity;

    internal AIFunction Observe(AIFunction function) => new ObservedFunction(function, this);
    internal async Task<int> RecordAsync(string kind, string request, object? result, CancellationToken token)
    {
        var id=Guid.NewGuid().ToString("N"); var key=Key+":"+id;
        var entry=new Entry(Identity,assignment.AttemptId,DateTimeOffset.UtcNow,kind,request,JsonSerializer.SerializeToElement(result,Json));
        var data=JsonSerializer.Serialize(entry,Json);
        const int partSize=24000; var count=(data.Length+partSize-1)/partSize;
        if(count>256)throw new InvalidOperationException("A QA observation exceeds the bounded evidence journal capacity; retain its audit and split the output.");
        for(var i=0;i<count;i++) {
            var partKey=key+":part:"+i;
            var part=new Part(Identity,data.Substring(i*partSize,Math.Min(partSize,data.Length-i*partSize)));
            await store.MergeAsync<Part>(partKey,"video-game.qa-observation-part.v1",1,current=>current??part,
                new Dictionary<string,string>(),partKey,token);
        }
        var receipt=new Receipt(Identity,count,Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data))),kind);
        await store.MergeAsync<Receipt>(key,"video-game.qa-observation.v1",1,current=>current??receipt,
            new Dictionary<string,string>(),key,token);
        var index = await store.MergeAsync<Index>(Key,"video-game.qa-evidence-index.v1",1,current=> {
            if(current is not null && current.Identity!=Identity)throw new InvalidOperationException("QA evidence identity changed.");
            var keys=(current?.Entries??[]).Append(key).Distinct(StringComparer.Ordinal).ToArray();
            if(keys.Length>256)throw new InvalidOperationException("QA evidence reached 256 observations; preserve the existing journal and split further validation.");
            var sameReportAttempt=current?.ReportAttemptId==assignment.AttemptId;
            return new(Identity,keys,kind=="validated-report"?(sameReportAttempt?current?.ReportKey??key:key):current?.ReportKey,
                kind=="validated-report"?assignment.AttemptId:current?.ReportAttemptId);
        },new Dictionary<string,string>(),key+":index",token);
        return Array.IndexOf(index.Payload.Entries.ToArray(), key);
    }
    internal Task SaveReportAsync(GameQaOutcome report,CancellationToken token) => RecordAsync("validated-report","",report,token);
    internal async Task<IReadOnlyList<Entry>> ReadScriptDraftsAsync(CancellationToken token)
    {
        var index=await ReadIndexAsync(token);var drafts=new List<Entry>();
        foreach(var key in (index?.Entries??[]).TakeLast(32)) {
            var receipt=(await platform.ReadOperatingStateAsync<Receipt>(key,token))?.Payload;
            if(receipt is null||receipt.Identity!=Identity)throw new InvalidOperationException("QA draft receipt identity changed.");
            // Legacy receipts lack kind. Only small legacy command drafts are considered;
            // screenshots and binary traces are never materialized as executable tests.
            if(receipt.Kind is not (null or "command" or "browser-test-script") || receipt.Kind is null && receipt.PartCount>2)continue;
            var entry=await ReadEntryAsync(key,token);
            if(entry.Kind is "command" or "browser-test-script")drafts.Add(entry);
        }
        return drafts;
    }
    internal async Task<GameQaOutcome?> ReadReportAsync(CancellationToken token)
    {
        var index=await ReadIndexAsync(token);if(index?.ReportKey is not {} key)return null;
        var entry=await ReadEntryAsync(key,token);
        if(entry.Kind!="validated-report")throw new InvalidOperationException("The retained QA report has a different evidence kind.");
        return entry.Result.Deserialize<GameQaOutcome>(Json)??throw new InvalidOperationException("The retained QA report is malformed.");
    }
    internal async Task<GameQaOutcome?> ReadResumeReportAsync(CancellationToken token)
    {
        var index=await ReadIndexAsync(token);if(index?.ReportKey is not {} key)return null;
        var entry=await ReadEntryAsync(key,token);
        // Lost replies resume the same logical attempt. A manager-requested new attempt must reassess its current environment.
        if(entry.AttemptId!=assignment.AttemptId && !TransportInterruptedReport(entry.AttemptId))return null;
        if(entry.Kind!="validated-report")throw new InvalidOperationException("The retained QA report has a different evidence kind.");
        return entry.Result.Deserialize<GameQaOutcome>(Json)??throw new InvalidOperationException("The retained QA report is malformed.");
    }
    private bool TransportInterruptedReport(Guid reportAttempt)
    {
        // Only the immediately preceding platform-supplied outcome may explain this recovery.
        // The journal identity independently binds source files, planning, scope and assignee revision.
        var prior=assignment.PriorOutcomes?.LastOrDefault();
        return prior is {Disposition:WorkExecutionDispositions.Blocked,OutcomeCode:"blocked"} &&
            prior.StageExecutionId==assignment.StageExecutionId && prior.AttemptId==reportAttempt &&
            prior.Output.ValueKind==JsonValueKind.Object && prior.Output.TryGetProperty("qaProcessingError",out var reason) &&
            reason.ValueKind==JsonValueKind.String && reason.GetString()==
                "QA could not complete exact-source validation (HttpRequestException); no validation verdict was accepted.";
    }

    internal async Task<string> ReadEvidenceAsync(CancellationToken token)
    {
        var index=await ReadIndexAsync(token); if(index is null)return "No earlier command journal exists for this exact source and planning.";
        var entries=new List<object>(); var keys=ObservationKeys(index);
        foreach(var key in keys.TakeLast(24)) {
            var entry=await ReadEntryAsync(key,token); var data=JsonSerializer.Serialize(entry,Json);
            // Full observations remain in the journal. Do not replay script bodies,
            // dependency listings or old browser results into every recovery prompt.
            entries.Add(new { observationIndex=Array.IndexOf(keys,key),entry.Kind,entry.ObservedAt,
                request=entry.Request[..Math.Min(180,entry.Request.Length)], pages=(data.Length+19999)/20000,
                result=data.Length<=1500 ? entry.Result : (JsonElement?)null,
                note=data.Length>1500 ? "Full result retained; use read_qa_evidence only for a criterion's missing evidence." : null });
        }
        return "Retained exact-source observations (not a new test or verdict). Current browser receipts follow separately. " +
            "Reuse current exact-source evidence; inspect referenced pages only for unresolved criteria. " +
            JsonSerializer.Serialize(new { totalObservations=keys.Length, earlierObservations=Math.Max(0,keys.Length-24),entries },Json);
    }

    internal AIFunction CreateReadTool() => AIFunctionFactory.Create(async (int observationIndex,int page,CancellationToken token) => {
        var index=await ReadIndexAsync(token);
        var keys=index is null?[]:ObservationKeys(index);
        if(observationIndex<0 || observationIndex>=keys.Length || page<0)
            return new EvidencePage(false,0,0,"Choose an existing observation index and nonnegative page.");
        var entry=await ReadEntryAsync(keys[observationIndex],token);var data=JsonSerializer.Serialize(entry,Json);
        var count=(data.Length+19999)/20000;
        if(page>=count)return new EvidencePage(false,page,count,"Page is outside the retained observation.");
        return new EvidencePage(true,page,count,data.Substring(page*20000,Math.Min(20000,data.Length-page*20000)));
    },"read_qa_evidence","Read a preserved observation for this exact source/planning scope. Supply its zero-based observationIndex and page. This is historical evidence, not a new test execution or QA verdict.");
    // 4.0.4 kept the report separately from Entries; expose that retained draft to later attempts too.
    private static string[] ObservationKeys(Index index) => index.ReportKey is {} report
        ? index.Entries.Append(report).Distinct(StringComparer.Ordinal).ToArray() : index.Entries.ToArray();
    private async Task<Entry> ReadEntryAsync(string key,CancellationToken token)
    {
        if(!key.StartsWith(Key+":",StringComparison.Ordinal))throw new InvalidOperationException("QA observation belongs to another source/planning scope.");
        var receipt=(await platform.ReadOperatingStateAsync<Receipt>(key,token))?.Payload;
        if(receipt is null||receipt.Identity!=Identity||receipt.PartCount is <1 or >256)throw new InvalidOperationException("QA observation identity changed.");
        var text=new System.Text.StringBuilder();
        for(var i=0;i<receipt.PartCount;i++) {
            var part=(await platform.ReadOperatingStateAsync<Part>(key+":part:"+i,token))?.Payload;
            if(part is null||part.Identity!=Identity)throw new InvalidOperationException("QA observation part is missing or mismatched.");
            text.Append(part.Data);
        }
        var data=text.ToString();
        if(Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data)))!=receipt.Sha256)
            throw new InvalidOperationException("QA observation failed its content integrity check.");
        var entry=JsonSerializer.Deserialize<Entry>(data,Json);
        return entry is not null&&entry.Identity==Identity?entry:throw new InvalidOperationException("QA observation content identity changed.");
    }
    private async Task<Index?> ReadIndexAsync(CancellationToken token)
    {
        var state=await platform.ReadOperatingStateAsync<Index>(Key,token);
        if(state?.Payload is { } index && index.Identity!=Identity)throw new InvalidOperationException("QA journal identity changed.");
        return state?.Payload;
    }
    private sealed class ObservedFunction(AIFunction function,GameQaJournal journal):DelegatingAIFunction(function)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments,CancellationToken cancellationToken)
        {
            var result=await InnerFunction.InvokeAsync(arguments,cancellationToken);
            // Do not let cancellation after a completed command discard its observed output.
            using var save=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var index=await journal.RecordAsync("command",JsonSerializer.Serialize(arguments,Json),result,save.Token);
            var data=JsonSerializer.Serialize(result,Json);
            if(data.Length<=12000)return result;
            return new { retained=true,observationIndex=index,page=0,
                note="Large observed output is retained in full. Read the relevant evidence pages; this reference is not a pass.",
                preview=data[..Math.Min(1600,data.Length)] };
        }
    }
    internal sealed record Entry(string Identity,Guid AttemptId,DateTimeOffset ObservedAt,string Kind,string Request,JsonElement Result);
    internal sealed record Index(string Identity,IReadOnlyList<string> Entries,string? ReportKey=null,Guid? ReportAttemptId=null);
    private sealed record Part(string Identity,string Data);
    private sealed record Receipt(string Identity,int PartCount,string Sha256,string? Kind=null);
    private sealed record EvidencePage(bool Found,int Page,int Pages,string Json);
}
