using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Agent.SDK.Compute;
using CSweet.WorkManagement.Contracts;
using CrosswiredStudios.VideoGame.AgentKit;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame;

// Browser container commands go through the approved compute callback. The agent
// never opens a Docker connection or executes Docker in its own runtime.
internal sealed class GameQaInteractionTesting(WorkExecutionAssignment assignment, GameQaInputWorkspace source,
    GameQaJournal journal, GameQaBrowserTesting browser, AgentRuntimeContext context)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string key = "qa-checks:" + assignment.StageExecutionId.ToString("N") + ":" + journal.Identity;
    private State? state;
    internal bool Pending { get; private set; }
    internal bool Failed => state?.Report is { Passed: false };
    internal string Evidence => state?.Report is {} report ?
        "Observed independent Chromium behavior checks (not other browsers/devices):\n" + JsonSerializer.Serialize(report,Json) +
        "\nThe exact authored test, screenshot, trace and result are retained in read_qa_evidence." : "";

    internal async Task ResumeAsync(CancellationToken token)
    {
        state = (await context.Platform.ReadOperatingStateAsync<State>(key,token))?.Payload;
        if(state is null)return;
        ValidateBinding();
        await AdvanceAsync(token);
    }
    internal AIFunction Tool(Action? onPending=null) => AIFunctionFactory.Create(async (string testScriptPath,CancellationToken token) => {
        var build = browser.CompletedBuild ?? throw new InvalidOperationException("First complete test_web_build for this source's production output.");
        await source.VerifyAsync(assignment,context,token);
        var script = await ReadScriptAsync(source.Path,testScriptPath,token);
        var hash = Hash(Encoding.UTF8.GetBytes(script));
        if(state is null || state.ScriptSha256!=hash) {
            if(state is {Complete:false})throw new InvalidOperationException("Resume the existing behavioral test before starting another.");
            state = new(journal.Identity,source.Commit,build.BuildSha256,build.EnvironmentId!.Value,hash,script,Run:(state?.Run??0)+1,Cursor:state?.Cursor??0);
            await SaveAsync(token);
            await journal.RecordAsync("browser-check-script",testScriptPath,new {state.ScriptSha256,script},token);
        }
        ValidateBinding();
        await AdvanceAsync(token);
        if(Pending)onPending?.Invoke();
        return new {pending=Pending,evidence=Evidence,instruction=Pending ?
            "Finish the turn without a verdict; compute events resume QA. Do not poll." :
            "Assess each actual assertion against its exact criterion. Static checks and mocks do not establish browser behavior."};
    },"test_web_interactions","Run independent Playwright assertions against the exact production build already tested by test_web_build. Use save_browser_test_script to retain a focused script without shell quoting or policy false matches, then supply its returned testScriptPath. Existing untracked UTF-8 scripts inside this workspace also work. The script is the BODY of an async function receiving page, context, origin and check(name, satisfiedBoolean, concreteEvidenceString). Use await page.goto(origin), Playwright input/viewport operations and page.evaluate for real browser measurements. page.evaluate accepts one serialized argument: pass an object for multiple values. Phaser lifecycle events belong to each scene.sys.events, not game.scene.on; inspect actual version APIs. Do not assume a native OscillatorNode has a state property; measure actual waveform output. You may install page.addInitScript hooks before navigation, then exercise real application APIs/UI. Call check for each observed assertion (up to 24 unique names). Do not mock browser APIs or hard-code success. Node module access is inside the isolated, network-disabled compute container only. Runtime budget is 22 seconds; split longer checks into focused scripts. Correcting a script does not require a new stage. Screenshots, trace, exact test script and result are retained. No QA verdict while pending. Unavailable or untested behavior is satisfied=false, unverifiable=true, never a pass.");

    internal WorkExecutionOutcomeV1 WaitOutcome() => new(assignment.StageExecutionId,assignment.AttemptId,
        WorkExecutionDispositions.Completed,"awaiting-compute","Independent QA is executing or retaining its exact-build Chromium behavioral checks.",
        JsonSerializer.SerializeToElement(new {sourceCommitSha=state!.Commit,buildSha256=state.BuildSha256,environmentId=state.EnvironmentId,
            operationIds=state.OperationId is {} id?new[]{id}:[],retryAt=DateTimeOffset.UtcNow.AddMinutes(5)},Json),[],[]);

    private void ValidateBinding()
    {
        var build=browser.CompletedBuild;
        if(state is null || build is null || state.Identity!=journal.Identity || state.Commit!=source.Commit ||
            state.BuildSha256!=build.BuildSha256 || state.EnvironmentId!=build.EnvironmentId ||
            state.ScriptSha256!=Hash(Encoding.UTF8.GetBytes(state.Script)) || Encoding.UTF8.GetByteCount(state.Script)>12000 ||
            state.Run <1 || state.Step is <0 or >3 || state.EvidenceOffset is <0 or >4*1024*1024)
            throw new InvalidOperationException("Behavioral checks no longer bind the exact source, build, script and compute environment.");
    }
    private async Task SaveAsync(CancellationToken token)
    {
        var desired=state! with {Cursor=state!.Cursor+1};
        var saved=await new RevisionSafeProjectState(context.Platform).MergeAsync<State>(key,"video-game.qa-interactions.v1",1,
            current=>(current?.Cursor??0)==desired.Cursor-1?desired:
                JsonSerializer.Serialize(current,Json)==JsonSerializer.Serialize(desired,Json)?current!:
                throw new InvalidOperationException("Behavioral checks advanced elsewhere; resume current state."),
            new Dictionary<string,string>(),$"{key}:{desired.Cursor}",token);
        state=saved.Payload;
    }
    private string Root => "/var/lib/csweet-compute/work/browser-"+state!.BuildSha256;
    private string ChecksRoot => Root+"/checks-"+state!.ScriptSha256;

    private async Task AdvanceAsync(CancellationToken token)
    {
        Pending=!state!.Complete;
        if(!Pending)return;
        await source.VerifyAsync(assignment,context,token);
        var environment=await context.Platform.Compute.ReadAsync(state.EnvironmentId,token);
        if(environment.DesiredState!="running" || environment.State is "failed" or "destroyed" or "stopped")
            throw new InvalidOperationException("The assigned browser compute environment needs recovery.");
        if(state.Request is {} request) {
            var operation=state.OperationId is {} id?await context.Platform.Compute.ReadOperationAsync(id,token):await SubmitAsync(request,token);
            if(state.OperationId is null){state=state with{OperationId=operation.Id};await SaveAsync(token);environment=await context.Platform.Compute.ReadAsync(state.EnvironmentId,token);}
            if(operation.EnvironmentId!=state.EnvironmentId || operation.Generation!=request.Generation+1 || environment.Generation!=operation.Generation)
                throw new InvalidOperationException("Behavioral compute operation authority changed.");
            if(operation.Status is "Pending" or "Dispatching" or "Reconciling")return;
            if(operation.Status!="Completed" || operation.Result is not {ErrorCode:null,Command:{ExitCode:0,TimedOut:false,Truncated:false,ErrorCode:null} output} || output.RequestId!=request.Command.RequestId)
                throw new InvalidOperationException("Behavioral compute did not complete; no pass or blind test replay is permitted: "+(operation.Result?.ErrorCode??operation.FailureCode??"command failed"));
            if(state.Step==1) {
                var report=JsonSerializer.Deserialize<Report>(output.StandardOutputText,Json)??throw new InvalidOperationException("Missing behavioral result.");
                ValidateReport(report,state,request.Command.RequestId);
                await journal.RecordAsync("browser-check-result","Independent exact-build Playwright assertions",new{operation.Id,report},token);
                state=state with {Report=report};
            } else if(state.Step==2) {
                var bytes=Convert.FromBase64String(output.StandardOutputText.Trim());
                if(bytes.Length!=Math.Min(4500,state.Report!.EvidenceBytes-state.EvidenceOffset))throw new InvalidOperationException("Behavioral evidence transfer was truncated.");
                var partKey=$"{key}:{state.Run}:e:{state.EvidenceOffset}";
                await new RevisionSafeProjectState(context.Platform).MergeAsync<GameQaBrowserTesting.ArchivePart>(partKey,"video-game.qa-interaction-part.v1",1,
                    current=>current??new(Convert.ToBase64String(bytes)),new Dictionary<string,string>(),partKey,token);
                state=state with{EvidenceOffset=state.EvidenceOffset+bytes.Length};
            }
            state=state with{Step=state.Step==2?2:state.Step+1,Request=null,OperationId=null};await SaveAsync(token);
            environment=await context.Platform.Compute.ReadAsync(state.EnvironmentId,token);
        }
        if(environment.State!="ready")return;
        if(state.Step==2 && state.EvidenceOffset==state.Report!.EvidenceBytes) {
            using var evidence=new MemoryStream();
            for(var offset=0;offset<state.EvidenceOffset;offset+=4500){
                var part=await context.Platform.ReadOperatingStateAsync<GameQaBrowserTesting.ArchivePart>($"{key}:{state.Run}:e:{offset}",token)
                    ??throw new InvalidOperationException("Behavioral evidence part is missing.");
                evidence.Write(Convert.FromBase64String(part.Payload.Base64));
            }
            if(Hash(evidence.ToArray())!=state.Report.EvidenceSha256)throw new InvalidOperationException("Behavioral evidence digest changed.");
            await journal.RecordAsync("browser-check-artifact","evidence.zip (base64; authored test, screenshot, trace, result)",
                new{state.Report.EvidenceSha256,Base64=Convert.ToBase64String(evidence.ToArray())},token);
            state=state with{Complete=true,Step=3};await SaveAsync(token);Pending=false;return;
        }
        var command=state.Step switch {
            0=>PrepareCommand(),
            1=>RunCommand(),
            2=>new ComputeCommand(Guid.NewGuid(),"python3","/var/lib/csweet-compute/work",["-c",
                "import base64; f=open('"+ChecksRoot+"/evidence/evidence.zip','rb'); f.seek("+state.EvidenceOffset+"); print(base64.b64encode(f.read(4500)).decode())"],30,8192),
            _=>throw new InvalidOperationException("Unknown behavioral compute step.")};
        var next=new Request(environment.Generation,$"{key}:{state.Run}:step:{state.Step}:{state.EvidenceOffset}",command);
        state=state with{Request=next};await SaveAsync(token);
        var submitted=await SubmitAsync(next,token);state=state with{OperationId=submitted.Id};await SaveAsync(token);
    }
    private Task<ComputeOperation> SubmitAsync(Request request,CancellationToken token)=>context.Platform.InvokeAsync<object,ComputeOperation>(ComputeCapabilities.Execute,
        new{environmentId=state!.EnvironmentId,expectedGeneration=request.Generation,idempotencyKey=request.Key,workload=new{command=request.Command}},token);

    private ComputeCommand PrepareCommand()
    {
        var files=new Dictionary<string,string>{{"runner.cjs",Resource("qa-interactions.cjs")},{"run.py",Resource("qa-interactions.py")},{"test.cjs",state!.Script}};
        var script="set -eu\nmkdir -p "+ChecksRoot+"/evidence\n"+string.Join("\n",files.Select(file=>
            "printf '%s' '"+Convert.ToBase64String(Encoding.UTF8.GetBytes(file.Value))+"' | base64 -d > "+ChecksRoot+"/"+file.Key));
        if(Encoding.UTF8.GetByteCount(script)>32000)throw new InvalidOperationException("The behavioral script exceeds the compute transfer budget; split the checks.");
        return new(Guid.NewGuid(),"/bin/sh","/var/lib/csweet-compute/work",["-c",script],30,8192);
    }
    internal ComputeCommand RunCommand()
    {
        var requestId=Guid.NewGuid();
        var name="csweet-qa-checks-"+requestId.ToString("N");
        var arguments=new List<string>{"run","--rm","--name",name,"--init","--network=none","--read-only","--cap-drop=ALL","--security-opt=no-new-privileges",
            "--memory=1536m","--cpus=2","--pids-limit=256","--shm-size=256m","--tmpfs=/tmp:rw,size=256m",
            "--mount","type=bind,src="+Root+",dst=/input,readonly","--mount","type=bind,src="+ChecksRoot+",dst=/checks,readonly",
            "--mount","type=bind,src="+ChecksRoot+"/evidence,dst=/evidence","--entrypoint","timeout","csweet/browser-tests:1.64.0",
            "--signal=KILL","26s","python3","/checks/run.py",JsonSerializer.Serialize(new{requestId,sourceCommitSha=source.Commit,
                buildSha256=state!.BuildSha256,scriptSha256=state.ScriptSha256},Json)};
        return new(requestId,"/usr/bin/docker","/var/lib/csweet-compute/work",arguments,30,32768);
    }
    internal static string Resource(string name)
    {
        using var stream=typeof(GameQaInteractionTesting).Assembly.GetManifestResourceStream("CSweet.Agent.QA.VideoGame."+name)
            ??throw new InvalidOperationException("Missing behavioral runner resource.");
        using var reader=new StreamReader(stream);return reader.ReadToEnd();
    }
    internal static async Task<string> ReadScriptAsync(string workspace,string relative,CancellationToken token)
    {
        if(string.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative))throw new ArgumentException("Use a workspace-relative independent test script.");
        var root=Path.GetFullPath(workspace);var path=Path.GetFullPath(Path.Combine(root,relative));
        if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.Ordinal)||!File.Exists(path))throw new ArgumentException("The test must exist inside this QA workspace.");
        for(var part=path;part is not null;part=Path.GetDirectoryName(part))if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Test script paths cannot be links.");
        if(new FileInfo(path).Length is <1 or >12000)throw new ArgumentException("Split the behavioral script into focused checks of at most 12,000 UTF-8 bytes.");
        var bytes=await File.ReadAllBytesAsync(path,token);return new UTF8Encoding(false,true).GetString(bytes);
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static void ValidateReport(Report report,State expected,Guid requestId)
    {
        if(report.RequestId!=requestId||report.SourceCommitSha!=expected.Commit||report.BuildSha256!=expected.BuildSha256||
            report.ScriptSha256!=expected.ScriptSha256||string.IsNullOrWhiteSpace(report.BrowserVersion)||report.Checks is null||report.Errors is null||
            report.Checks.Count>24||report.Checks.Any(c=>string.IsNullOrWhiteSpace(c.Name)||string.IsNullOrWhiteSpace(c.Evidence))||
            report.Checks.Select(c=>c.Name).Distinct(StringComparer.Ordinal).Count()!=report.Checks.Count||
            report.Passed!=(report.Checks.Count>0&&report.Checks.All(c=>c.Satisfied)&&report.Errors.Count==0)||
            report.EvidenceBytes is <1 or >4*1024*1024||report.EvidenceSha256 is not {Length:64}||report.EvidenceSha256.Any(c=>!Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Behavioral results do not bind the exact source, build, script and executed assertions.");
    }
    internal sealed record State(string Identity,string Commit,string BuildSha256,Guid EnvironmentId,string ScriptSha256,string Script,
        int Run=1,int Cursor=0,int Step=0,Request? Request=null,Guid? OperationId=null,Report? Report=null,int EvidenceOffset=0,bool Complete=false);
    internal sealed record Request(long Generation,string Key,ComputeCommand Command);
    internal sealed record Report(Guid RequestId,string SourceCommitSha,string BuildSha256,string ScriptSha256,string BrowserVersion,bool Passed,
        IReadOnlyList<Check> Checks,IReadOnlyList<string> Errors,string EvidenceSha256,int EvidenceBytes);
    internal sealed record Check(string Name,bool Satisfied,string Evidence);
}
