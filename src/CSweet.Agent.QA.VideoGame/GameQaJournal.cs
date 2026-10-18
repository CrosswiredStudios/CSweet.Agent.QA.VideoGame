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
        assignment.DeliveryPlanId, assignment.ScopeRevision, assignment.StageKey, assignment.AssignmentRevision,
        assignment.PlanningRevision, input.Planning, sourceIdentity }, Json)));
    internal string Key => "game-qa-evidence:" + Identity;

    internal AIFunction Observe(AIFunction function) => new ObservedFunction(function, this);
    internal async Task RecordAsync(string kind, string request, object? result, CancellationToken token)
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
        var receipt=new Receipt(Identity,count,Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(data))));
        await store.MergeAsync<Receipt>(key,"video-game.qa-observation.v1",1,current=>current??receipt,
            new Dictionary<string,string>(),key,token);
        await store.MergeAsync<Index>(Key,"video-game.qa-evidence-index.v1",1,current=> {
            if(current is not null && current.Identity!=Identity)throw new InvalidOperationException("QA evidence identity changed.");
            var keys=(current?.Entries??[]).Append(key).Distinct(StringComparer.Ordinal).ToArray();
            if(keys.Length>256)throw new InvalidOperationException("QA evidence reached 256 observations; preserve the existing journal and split further validation.");
            var sameReportAttempt=current?.ReportAttemptId==assignment.AttemptId;
            return new(Identity,keys,kind=="validated-report"?(sameReportAttempt?current?.ReportKey??key:key):current?.ReportKey,
                kind=="validated-report"?assignment.AttemptId:current?.ReportAttemptId);
        },new Dictionary<string,string>(),key+":index",token);
    }
    internal Task SaveReportAsync(GameQaOutcome report,CancellationToken token) => RecordAsync("validated-report","",report,token);
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
        if(entry.AttemptId!=assignment.AttemptId)return null;
        if(entry.Kind!="validated-report")throw new InvalidOperationException("The retained QA report has a different evidence kind.");
        return entry.Result.Deserialize<GameQaOutcome>(Json)??throw new InvalidOperationException("The retained QA report is malformed.");
    }
    internal async Task<string> ReadEvidenceAsync(CancellationToken token)
    {
        var index=await ReadIndexAsync(token); if(index is null)return "No earlier command journal exists for this exact source and planning.";
        var entries=new List<object>(); var characters=0;
        var keys=ObservationKeys(index);
        foreach(var key in keys.TakeLast(32)) {
            var entry=await ReadEntryAsync(key,token);var length=JsonSerializer.Serialize(entry,Json).Length;
            if(characters+length>120000) {
                entries.Add(new {observationIndex=Array.IndexOf(keys,key),entry.Kind,entry.ObservedAt,pages=(length+19999)/20000,
                    note="Full output is preserved. Use read_qa_evidence with this observationIndex and zero-based page."});continue;
            }
            entries.Add(entry);characters+=length;
        }
        return "Historical observed commands for the identical source/planning (not a pass and not fresh browser/device evidence):\n"+JsonSerializer.Serialize(entries,Json);
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
            await journal.RecordAsync("command",JsonSerializer.Serialize(arguments,Json),result,save.Token);
            return result;
        }
    }
    internal sealed record Entry(string Identity,Guid AttemptId,DateTimeOffset ObservedAt,string Kind,string Request,JsonElement Result);
    internal sealed record Index(string Identity,IReadOnlyList<string> Entries,string? ReportKey=null,Guid? ReportAttemptId=null);
    private sealed record Part(string Identity,string Data);
    private sealed record Receipt(string Identity,int PartCount,string Sha256);
    private sealed record EvidencePage(bool Found,int Page,int Pages,string Json);
}
