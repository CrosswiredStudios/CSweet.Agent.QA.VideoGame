using System.Security.Cryptography;
using System.IO.Compression;
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
    GameQaJournal journal, GameQaBrowserTesting browser, AgentRuntimeContext context, GameQaBehavioralPolicy? policy = null)
{
    private readonly GameQaBehavioralPolicy policy = policy ?? GameQaBehavioralPolicy.Unbounded;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string key = StateKey(assignment, journal.Identity);
    internal static string StateKey(WorkExecutionAssignment assignment, string identity) =>
        "qa-checks:" + (assignment.Scope is "Release" or "Epic" or "Story"
            ? "delivery:" + assignment.ExecutionId.ToString("N") : assignment.StageExecutionId.ToString("N")) + ":" + identity;
    private readonly GameQaEvidenceTransfer transfer = new(context, journal.Identity);
    private State? state;
    internal bool Pending { get; private set; }
    /// <summary>The latest run observed a failed assertion or a page error: product evidence, not a harness problem.</summary>
    internal bool Failed => state?.Report is { Passed: false, ScriptError: null };
    /// <summary>Why the latest run produced no usable result (QA's own test or its tooling), if it did not.</summary>
    internal string? HarnessError => state?.HarnessError ?? state?.Report?.ScriptError;
    internal int RunsUsed => state?.Run ?? 0;
    internal int MaximumRuns => policy.MaximumRuns;
    /// <summary>Acceptance criteria (zero-based) whose most recent assertion in this stage was satisfied.</summary>
    internal IReadOnlySet<int> SatisfiedCriteria => (state?.CriterionResults ?? []).Where(x => x.Satisfied).Select(x => x.Index).ToHashSet();
    internal bool HasScript(string script) => state?.ScriptSha256 == Hash(Encoding.UTF8.GetBytes(script));
    /// <summary>The shared browser scenario script (CSweet.Agent.SDK); QA runs it unchanged as its standard first run.</summary>
    internal static string ScenarioScript => BrowserScenarios.PlaywrightScript;
    /// <summary>The latest completed shared scenario run, compared with the author's generated observation records.</summary>
    internal string? ScenarioNote => state?.ScenarioNote;
    internal string Evidence => state is null ? "" :
        (state.HarnessError is {} harness ? "Your latest behavioral run produced no result. This is a test-harness problem, not a product defect: " + harness :
         state.Report is {} report ?
            "Observed independent Chromium behavior checks (not other browsers/devices):\n" + JsonSerializer.Serialize(report,Json) +
            (report.ScriptError is {} error ? "\nYour test script stopped early (harness problem, not a product defect): " + error : "") +
            "\nThe exact authored test" + ((report.OmittedEvidence?.Count ?? 0) > 0 ? $" and result are retained (omitted to fit the evidence budget: {string.Join(", ", report.OmittedEvidence!)})" :
                ", screenshot, trace and result are retained") + " in read_qa_evidence." : "") +
        (state.ScenarioNote is {} scenarios ? "\n" + scenarios : "") +
        (state.Complete ? $"\nBehavioral runs used in this stage: {state.Run} of {policy.MaximumRuns}." : "");

    internal async Task ResumeAsync(CancellationToken token)
    {
        state = (await context.Platform.ReadOperatingStateAsync<State>(key,token))?.Payload;
        if(state is null)return;
        var recovered=false;
        if(CanRecoverRejectedOutput(state,assignment)||CanRecoverRejectedKey(state,assignment))recovered=true;
        if(!recovered && !state.Complete && FollowsBlockedAttempt(assignment)) {
            // A person retried after this stage stopped. Never re-read the same terminal failure: settle the in-flight
            // run (a pending or successful operation still completes normally) or abandon it without replaying it.
            try { ValidateBinding(); await AdvanceAsync(token); }
            catch (InvalidOperationException error) {
                await AbandonAsync("A retry followed a stopped attempt, and the in-flight behavioral run could not be completed (" +
                    Excerpt(error.Message,400) + "). It was abandoned, not replayed.",token);
            }
            return;
        }
        ValidateBinding();
        if(CanRecoverRejectedOutput(state,assignment)) {
            await source.VerifyAsync(assignment,context,token);
            await journal.RecordAsync("browser-check-rejected-request","Unaccepted request failed callback output schema; corrected request receives a new identity",state.Request,token);
            // The platform's exact previous failure proves admission rejected this
            // request before a compute effect. Never mutate/retry accepted terms.
            var command=PrepareCommand();
            state=state with{Step=0,RunnerVersion="3",Request=new(state.Request!.Generation,
                OperationKey(key,state.Run,0,state.EvidenceOffset,"output-repair:"+state.Request.Command.RequestId.ToString("N")),command)};
            await SaveAsync(token);
        }
        if(CanRecoverRejectedKey(state!,assignment)) {
            await source.VerifyAsync(assignment,context,token);
            await journal.RecordAsync("browser-check-rejected-request","Unaccepted request failed callback key schema; corrected request receives a bounded new identity",state!.Request,token);
            var old=state.Request!;
            state=state with{Request=new(old.Generation,OperationKey(key,state.Run,state.Step,state.EvidenceOffset,
                "key-repair:"+old.Command.RequestId.ToString("N")),old.Command with{RequestId=Guid.NewGuid()})};
            await SaveAsync(token);
        }
        await AdvanceAsync(token);
    }
    internal AIFunction Tool(Action? onPending=null) => AIFunctionFactory.Create(async (string testScriptPath,CancellationToken token) => {
        var build = browser.CompletedBuild ?? throw new InvalidOperationException("First complete test_web_build for this source's production output.");
        await source.VerifyAsync(assignment,context,token);
        var script = await ReadScriptAsync(source.Path,testScriptPath,token);
        var hash = Hash(Encoding.UTF8.GetBytes(script));
        if(state is null || state.ScriptSha256!=hash) {
            if(state is {Complete:false})throw new InvalidOperationException("Resume the existing behavioral test before starting another.");
            if((state?.Run??0)>=policy.MaximumRuns)
                return new {pending=false,budgetSpent=true,evidence=Evidence,instruction=
                    $"The behavioral run budget for this stage ({policy.MaximumRuns}) is spent. Do not start another script. "+
                    "Call submit_qa_report now from the evidence you have: pass the criteria your evidence supports, report observed defects as findings, "+
                    "and mark a criterion unverifiable (with missingEvidence) only if its evidence truly cannot be observed here."};
            state = new(journal.Identity,source.Commit,build.BuildSha256,build.EnvironmentId!.Value,hash,script,Run:checked((state?.Run??0)+1),Cursor:state?.Cursor??0,RunnerVersion:"3",
                CriterionResults:state?.CriterionResults);
            await SaveAsync(token);
            await journal.RecordAsync("browser-check-script",testScriptPath,new {state.ScriptSha256,script},token);
        }
        ValidateBinding();
        await AdvanceAsync(token);
        if(Pending)onPending?.Invoke();
        return new {pending=Pending,budgetSpent=false,evidence=Evidence,instruction=Pending ?
            "Finish the turn without a verdict; compute events resume QA. Do not poll." : CompletedInstruction()};
    },"test_web_interactions","Run independent Playwright checks against the exact production build already tested by test_web_build. Use save_browser_test_script to retain a focused script without shell quoting or policy false matches, then supply its returned testScriptPath. Existing untracked UTF-8 scripts inside this workspace also work. The script is the BODY of an async function receiving page, context, origin and check(name, satisfiedBoolean, concreteEvidenceString, { criterion, kind }). criterion is the zero-based index of the acceptance criterion the check serves; kind is 'assertion' (default: satisfied means the criterion holds as required) or 'observation' (records a measured value and never fails the run). Name checks for what they verify; satisfied=true must always mean 'as required'. Use await page.goto(origin) for server delivery or file:///tmp/build/<relative-output-file> for local-file delivery of this same extracted build. File access outside /tmp/build and external origins remain denied. Use Playwright input/viewport operations and page.evaluate for real browser measurements. page.evaluate runs in a separate browser realm: outer variables are unavailable. Pass required values in its single serialized object argument. Prefer the application's own observable hooks and UI over its internals. You may install page.addInitScript hooks before navigation, then exercise real application APIs/UI. Up to 24 uniquely named checks. Do not mock browser APIs or hard-code success. Node module access is inside the isolated, network-disabled compute container only. Runtime budget is 22 seconds; split longer checks into focused scripts. Runs are budgeted per stage. Correcting a script does not require a new stage. The result, exact test script, screenshot and trace are retained. No QA verdict while pending. Unavailable or untested behavior is satisfied=false, unverifiable=true, never a pass.");

    /// <summary>
    /// run_scenario_contract: saves the shared scenario script as an ordinary source-bound draft (so continuation restores
    /// it) and runs it like any behavioral script on the same production build the author ran.
    /// </summary>
    internal AIFunction ScenarioTool(GameQaBrowserScripts scripts, Action? onPending = null) => AIFunctionFactory.Create(async (CancellationToken token) =>
    {
        var saved = JsonSerializer.SerializeToElement(await scripts.Tool().InvokeAsync(new AIFunctionArguments { ["script"] = ScenarioScript }, token), Json);
        var path = saved.GetProperty("testScriptPath").GetString()!;
        return await Tool(onPending).InvokeAsync(new AIFunctionArguments { ["testScriptPath"] = path }, token);
    }, "run_scenario_contract",
        "Run the shared browser scenario contract on the exact build test_web_build tested: it opens the build with ?csweet-scenarios, " +
        "calls window.csweetScenarios.run(), judges every check from its expected and observed values (a check whose observation cites no " +
        "emitted event fails), reports one check per acceptance criterion, and compares the run with every observation record's generated " +
        "csweet:observed section in this source. It is the same runner the author's receipt used. Run it first when the ticket declares " +
        "browser interaction; it counts as one behavioral run.");

    /// <summary>
    /// Summarizes a shared scenario run and compares it with every generated observation record in the source, so each
    /// failed check and each difference from the record reaches the verdict in one round.
    /// </summary>
    internal static string DescribeScenarios(ScenarioCapture capture, string sourceRoot)
    {
        var text = new StringBuilder("Shared browser scenario run on this exact build (judged by the runner, not the page): ")
            .Append(BrowserScenarios.Summarize(capture, 5000));
        var records = ScenarioRecords(sourceRoot);
        if (records.Count == 0)
            text.Append("\nNo observation record in this source has a generated csweet:observed section. If a criterion asks for a record of a run, " +
                "its measured values were written by hand: compare them with this run.");
        foreach (var (path, recorded) in records)
        {
            var differences = BrowserScenarios.Compare(capture, recorded, 20);
            text.Append($"\nRecord {path}: ").Append(differences.Count == 0
                ? "its generated section matches this run."
                : "its generated section differs from this run: " + string.Join(" ", differences));
        }
        text.Append("\nReport every failed scenario check and every record difference as findings in this verdict; do not spend another run re-asking them. " +
            "Also judge whether each check really measures its criterion from emitted events (not from the plan or configuration), and " +
            "whether hand-written prose in a record contradicts its generated section.");
        var value = text.ToString();
        return value.Length <= 9000 ? value : value[..8997] + "...";
    }

    /// <summary>Observation records with a generated scenario section, bounded and skipping dependency and output trees.</summary>
    internal static IReadOnlyList<(string Path, ScenarioCapture Capture)> ScenarioRecords(string sourceRoot)
    {
        var found = new List<(string, ScenarioCapture)>();
        var skipped = new HashSet<string>(StringComparer.Ordinal) { ".git", "node_modules", "dist", "build", "test-results", ".csweet", "qa" };
        var pending = new Stack<string>([sourceRoot]);
        var visited = 0;
        while (pending.Count > 0 && visited < 4000 && found.Count < 8)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
                if (!skipped.Contains(Path.GetFileName(child)) && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
            foreach (var file in Directory.EnumerateFiles(directory, "*.md"))
            {
                if (++visited > 4000 || new FileInfo(file).Length > 1024 * 1024) continue;
                var markdown = File.ReadAllText(file);
                if (BrowserScenarios.ReadRecord(markdown) is { } capture)
                    found.Add((Path.GetRelativePath(sourceRoot, file).Replace('\\', '/'), capture));
            }
        }
        return found.OrderBy(x => x.Item1, StringComparer.Ordinal).ToArray();
    }

    private string CompletedInstruction() =>
        (state?.HarnessError is not null || state?.Report?.ScriptError is not null
            ? "Your test did not run to completion. Fix the script only if a criterion still needs this evidence; otherwise decide from the evidence you have. "
            : "Assess each actual assertion against its exact criterion. Static checks and mocks do not establish browser behavior. " +
              "A failed assertion is a product defect unless your own expectation or script was wrong: decide which, report defects as findings, and do not re-run the same question for a different answer. ") +
        $"Behavioral runs used: {RunsUsed} of {policy.MaximumRuns}.";

    internal WorkExecutionOutcomeV1 WaitOutcome() => new(assignment.StageExecutionId,assignment.AttemptId,
        WorkExecutionDispositions.Completed,"awaiting-compute","Independent QA is executing or retaining its exact-build Chromium behavioral checks.",
        JsonSerializer.SerializeToElement(new {sourceCommitSha=state!.Commit,buildSha256=state.BuildSha256,environmentId=state.EnvironmentId,
            // A prior completed export is evidence, never the wait for a newly submitted script.
            operationIds=(state.Request is not null ? state.OperationId : transfer.PendingOperationId) is {} id?new[]{id}:[],
            retryAt=DateTimeOffset.UtcNow.AddMinutes(5)},Json),[],[]);

    private void ValidateBinding()
    {
        var build=browser.CompletedBuild;
        if(state is null || build is null || state.Identity!=journal.Identity || state.Commit!=source.Commit ||
            state.BuildSha256!=build.BuildSha256 || state.EnvironmentId!=build.EnvironmentId ||
            state.ScriptSha256!=Hash(Encoding.UTF8.GetBytes(state.Script)) || Encoding.UTF8.GetByteCount(state.Script)>12000 ||
            state.Run <1 || state.Step is <0 or >3 || state.EvidenceOffset < 0)
            throw GameQaDefects.Agent("Behavioral checks no longer bind the exact source, build, script and compute environment.");
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
                throw GameQaDefects.Agent("Behavioral compute operation authority changed.");
            if(operation.Status is "Pending" or "Dispatching" or "Reconciling")return;
            if(operation.Status=="Completed" && operation.Result?.ErrorCode=="outcome-unknown" && state.Step==2) {
                if(state.EvidenceReadRecoveries>=2)
                    throw new InvalidOperationException("Behavioral evidence read recovery exceeded its bounded budget.");
                var replacement=new Request(environment.Generation,
                    OperationKey(key,state.Run,state.Step,state.EvidenceOffset,$"evidence-read-recovery:{operation.Id:N}"),
                    request.Command with{RequestId=Guid.NewGuid()});
                state=state with{Request=replacement,OperationId=null,EvidenceReadRecoveries=state.EvidenceReadRecoveries+1};
                await SaveAsync(token);
                var recoveredRead=await SubmitAsync(replacement,token);
                state=state with{OperationId=recoveredRead.Id};await SaveAsync(token);
                return;
            }
            if(operation.Status!="Completed" || operation.Result is not {ErrorCode:null,Command:{ExitCode:0,TimedOut:false,Truncated:false,ErrorCode:null} output} || output.RequestId!=request.Command.RequestId)
            {
                if(operation.Result?.Command is {} wrong && wrong.RequestId!=request.Command.RequestId)
                    throw GameQaDefects.Agent("Behavioral compute returned a result for a different request; no verdict or replay is permitted.");
                var failure="Behavioral compute did not complete; no pass or blind test replay is permitted: "+Describe(operation);
                // Preparing or running the authored test is QA's own harness: the model gets the reason and may
                // correct its script (a new script is not a replay). Reading back evidence keeps its bounded recovery.
                if(state.Step is 0 or 1) {
                    await journal.RecordAsync("browser-check-harness-error",failure,new{operation.Id,state.Step,state.ScriptSha256},token);
                    state=state with{Complete=true,Step=3,Request=null,OperationId=null,HarnessError=failure};
                    await SaveAsync(token);Pending=false;return;
                }
                throw new InvalidOperationException(failure);
            }
            if(state.Step==1) {
                var receipt=JsonSerializer.Deserialize<Receipt>(output.StandardOutputText,Json)??throw new InvalidOperationException("Missing behavioral receipt.");
                ValidateReceipt(receipt,state,request.Command.RequestId);
                await journal.RecordAsync("browser-check-receipt","Exact-build Playwright control receipt; full assertions pending evidence transfer",new{operation.Id,receipt},token);
                state=state with {Manifest=receipt};
            } else if(state.Step==2) {
                var bytes=Convert.FromBase64String(output.StandardOutputText.Trim());
                if(bytes.Length!=Math.Min(4500,state.Manifest!.EvidenceBytes-state.EvidenceOffset))throw new InvalidOperationException("Behavioral evidence transfer was truncated.");
                var partKey=$"{key}:{state.Run}:e:{state.EvidenceOffset}";
                await new RevisionSafeProjectState(context.Platform).MergeAsync<GameQaBrowserTesting.ArchivePart>(partKey,"video-game.qa-interaction-part.v1",1,
                    current=>current??new(Convert.ToBase64String(bytes)),new Dictionary<string,string>(),partKey,token);
                state=state with{EvidenceOffset=state.EvidenceOffset+bytes.Length,EvidenceReadRecoveries=0};
            }
            state=state with{Step=state.Step==2?2:state.Step+1,Request=null,OperationId=null};await SaveAsync(token);
            environment=await context.Platform.Compute.ReadAsync(state.EnvironmentId,token);
        }
        if(environment.State!="ready" && state.Step!=2)return;
        if(state.Step==1 && state.RunnerVersion!="3") {
            // An older prepare may have completed just before the package update.
            // Refresh only wrapper files before submitting any test, with new keys.
            state=state with{Step=0,RunnerVersion="3"};await SaveAsync(token);
        }
        if(state.Step==2) {
            var bytes = await transfer.ReadAsync(environment, new(state.Commit, state.BuildSha256, state.Manifest!.EvidenceSha256,
                state.Manifest.EvidenceBytes, state.ScriptSha256), token);
            if (bytes is null) return;
            using var evidence = new MemoryStream(bytes);
            evidence.Position=0;
            using var zip=new ZipArchive(evidence,ZipArchiveMode.Read,leaveOpen:true);
            var result=zip.Entries.SingleOrDefault(e=>e.FullName=="result.json")??throw new InvalidOperationException("Behavioral archive lacks one exact result.json.");
            if(result.Length is <1 or >131072)throw new InvalidOperationException("Behavioral assertion report exceeds its verified bound.");
            await using var resultStream=result.Open();
            var report=await JsonSerializer.DeserializeAsync<Report>(resultStream,Json,token)??throw new InvalidOperationException("Missing full behavioral report.");
            report=report with{EvidenceSha256=state.Manifest.EvidenceSha256,EvidenceBytes=state.Manifest.EvidenceBytes};
            ValidateReport(report,state,state.Manifest.RequestId);
            var results=(state.CriterionResults??[]).ToDictionary(x=>x.Index);
            foreach(var check in report.Checks.Where(c=>c.Criterion is not null && c.Kind!="observation"))
                results[check.Criterion!.Value]=new(check.Criterion.Value,check.Satisfied && report.ScriptError is null,state.Run);
            if(report.Passed!=state.Manifest.Passed||report.Checks.Count!=state.Manifest.CheckCount||report.Errors.Count!=state.Manifest.ErrorCount||
                report.BrowserVersion!=state.Manifest.BrowserVersion)throw GameQaDefects.Agent("Full behavioral assertions differ from their compute receipt.");
            string? scenarioNote=null;
            if(zip.Entries.SingleOrDefault(e=>e.FullName=="scenarios.json") is {} scenarios) {
                if(scenarios.Length is <1 or >2*1024*1024)throw new InvalidOperationException("The scenario evaluation exceeds its verified bound.");
                await using var scenarioStream=scenarios.Open();
                using var scenarioReader=new StreamReader(scenarioStream);
                var capture=BrowserScenarios.ReadEvaluation(await scenarioReader.ReadToEndAsync(token));
                scenarioNote=capture is null?"The shared scenario run attached an evaluation QA could not read.":DescribeScenarios(capture,source.Path);
                await journal.RecordAsync("browser-scenario-comparison","Shared scenario run compared with generated observation records",new{scenarioNote},token);
            }
            await journal.RecordAsync("browser-check-result","Independent exact-build Playwright assertions",report,token);
            await journal.RecordAsync("browser-check-artifact","evidence.zip (retained artifact; authored test, screenshot, trace, result)",
                new{ArtifactId=transfer.OperationId,state.Manifest.EvidenceSha256,state.Manifest.EvidenceBytes},token);
            state=state with{Report=report,Complete=true,Step=3,CriterionResults=results.Values.OrderBy(x=>x.Index).ToArray(),ScenarioNote=scenarioNote};await SaveAsync(token);Pending=false;return;
        }
        var command=state.Step switch {
            0=>PrepareCommand(),
            1=>RunCommand(),
            _=>throw new InvalidOperationException("Unknown behavioral compute step.")};
        var next=new Request(environment.Generation,OperationKey(key,state.Run,state.Step,state.EvidenceOffset,"v3"),command);
        state=state with{Request=next};await SaveAsync(token);
        var submitted=await SubmitAsync(next,token);state=state with{OperationId=submitted.Id};await SaveAsync(token);
    }
    private Task<ComputeOperation> SubmitAsync(Request request,CancellationToken token)=>context.Platform.InvokeAsync<object,ComputeOperation>(ComputeCapabilities.Execute,
        new{environmentId=state!.EnvironmentId,expectedGeneration=request.Generation,idempotencyKey=request.Key,workload=new{command=request.Command}},token);

    private ComputeCommand PrepareCommand()
    {
        var files=new Dictionary<string,string>{{"runner.cjs",Resource("qa-interactions.cjs")},{"run.py",Resource("qa-interactions.py")},
            {"qa-request-policy.cjs",Resource("qa-request-policy.cjs")},{"test.cjs",state!.Script}};
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
                buildSha256=state!.BuildSha256,scriptSha256=state.ScriptSha256,criteriaCount=policy.CriterionCount},Json)};
        return new(requestId,"/usr/bin/docker","/var/lib/csweet-compute/work",arguments,30,8192);
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
    internal static string OperationKey(string scope,int run,int step,int offset,string purpose)=>
        "qai:"+Hash(JsonSerializer.SerializeToUtf8Bytes(new{scope,run,step,offset,purpose},Json));
    internal static void ValidateReport(Report report,State expected,Guid requestId)
    {
        if(report.RequestId!=requestId||report.SourceCommitSha!=expected.Commit||report.BuildSha256!=expected.BuildSha256||
            report.ScriptSha256!=expected.ScriptSha256||string.IsNullOrWhiteSpace(report.BrowserVersion)||report.Checks is null||report.Errors is null||
            report.Checks.Count>24||report.Checks.Any(c=>string.IsNullOrWhiteSpace(c.Name)||string.IsNullOrWhiteSpace(c.Evidence))||
            report.Checks.Select(c=>c.Name).Distinct(StringComparer.Ordinal).Count()!=report.Checks.Count||
            report.Checks.Any(c=>c.Kind is not (null or "assertion" or "observation")||c.Criterion is <0)||
            report.Passed!=(report.Checks.Count>0&&report.ScriptError is null&&report.Checks.All(c=>c.Kind=="observation"||c.Satisfied)&&report.Errors.Count==0)||
            report.EvidenceBytes < 1||report.EvidenceSha256 is not {Length:64}||report.EvidenceSha256.Any(c=>!Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Behavioral results do not bind the exact source, build, script and executed assertions.");
    }
    internal static void ValidateReceipt(Receipt receipt,State expected,Guid requestId)
    {
        if(receipt.RequestId!=requestId||receipt.SourceCommitSha!=expected.Commit||receipt.BuildSha256!=expected.BuildSha256||
            receipt.ScriptSha256!=expected.ScriptSha256||string.IsNullOrWhiteSpace(receipt.BrowserVersion)||receipt.CheckCount is <0 or >24||
            receipt.ErrorCount is <0 or >12||receipt.Passed&&(receipt.CheckCount==0||receipt.ErrorCount!=0)||
            receipt.EvidenceBytes < 1||receipt.EvidenceSha256 is not{Length:64}||receipt.EvidenceSha256.Any(c=>!Uri.IsHexDigit(c)))
            throw new InvalidOperationException("Behavioral receipt does not bind the exact source, build, script and evidence archive.");
    }
    /// <summary>
    /// This attempt follows a Blocked attempt of the same stage: a person or manager decided to retry. A transport
    /// interruption is not such a decision; the same logical attempt resumes instead.
    /// </summary>
    internal static bool FollowsBlockedAttempt(WorkExecutionAssignment assignment)
    {
        var prior=assignment.PriorOutcomes?.LastOrDefault();
        if(prior is not {Disposition:WorkExecutionDispositions.Blocked}||prior.StageExecutionId!=assignment.StageExecutionId||prior.AttemptId==assignment.AttemptId)return false;
        return !(prior.Output.ValueKind==JsonValueKind.Object&&prior.Output.TryGetProperty("qaProcessingError",out var error)&&
            error.ValueKind==JsonValueKind.String&&error.GetString()!.Contains("(HttpRequestException)",StringComparison.Ordinal));
    }
    private async Task AbandonAsync(string reason,CancellationToken token)
    {
        await journal.RecordAsync("browser-check-harness-error",reason,new{state!.OperationId,state.Step,state.ScriptSha256},token);
        state=state with{Complete=true,Step=3,Request=null,OperationId=null,HarnessError=reason};
        await SaveAsync(token);Pending=false;
    }
    /// <summary>The platform's reason a compute operation did not succeed, with the tail of the command's error output.</summary>
    internal static string Describe(ComputeOperation operation)
    {
        var command=operation.Result?.Command;
        var parts=new List<string>();
        if(operation.Status!="Completed")parts.Add("operation "+operation.Status+(operation.FailureCode is {} code?" ("+code+")":""));
        if(operation.Result?.ErrorCode is {} error)parts.Add(error=="outcome-unknown"?"the platform lost this run's result (outcome-unknown); it was not replayed":"error "+error);
        if(command is not null) {
            if(command.TimedOut)parts.Add("timed out");
            else if(command.ExitCode is {} exit && exit!=0)parts.Add("exit code "+exit);
            if(command.Truncated)parts.Add("output truncated");
            if(command.ErrorCode is {} commandError)parts.Add("command error "+commandError);
            var tail=string.Join(" | ",command.StandardErrorText.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).TakeLast(3));
            if(tail.Length>0)parts.Add("stderr: "+Excerpt(tail,600));
        }
        return parts.Count==0?"command failed":string.Join("; ",parts);
    }
    private static string Excerpt(string value,int length)=>value.Length<=length?value:value[..(length-3)]+"...";

    internal static bool CanRecoverRejectedOutput(State state,WorkExecutionAssignment assignment)
    {
        var prior=assignment.PriorOutcomes?.LastOrDefault();
        return state is{Step:1,OperationId:null,Request.Command.MaximumOutputBytes:32768}&&
            state.Request.Command.Executable=="/usr/bin/docker"&&
            prior is{Disposition:WorkExecutionDispositions.Blocked,OutcomeCode:"blocked"}&&prior.StageExecutionId==assignment.StageExecutionId&&
            prior.Output.ValueKind==JsonValueKind.Object&&prior.Output.TryGetProperty("qaProcessingError",out var error)&&
            error.ValueKind==JsonValueKind.String&&error.GetString()=="QA could not invoke compute.execute.v1 (ValidationFailed): JSON Schema validation failed: $.workload.command.maximumOutputBytes is above the maximum.";
    }
    internal static bool CanRecoverRejectedKey(State state,WorkExecutionAssignment assignment)
    {
        var prior=assignment.PriorOutcomes?.LastOrDefault();
        return state.OperationId is null&&state.Request is{} request&&request.Key.Length>128&&
            prior is{Disposition:WorkExecutionDispositions.Blocked,OutcomeCode:"blocked"}&&prior.StageExecutionId==assignment.StageExecutionId&&
            prior.Output.ValueKind==JsonValueKind.Object&&prior.Output.TryGetProperty("qaProcessingError",out var error)&&error.ValueKind==JsonValueKind.String&&
            error.GetString()=="QA could not invoke compute.execute.v1 (ValidationFailed): JSON Schema validation failed: $.idempotencyKey is too long.";
    }
    internal sealed record State(string Identity,string Commit,string BuildSha256,Guid EnvironmentId,string ScriptSha256,string Script,
        int Run=1,int Cursor=0,int Step=0,Request? Request=null,Guid? OperationId=null,Report? Report=null,int EvidenceOffset=0,bool Complete=false,Receipt? Manifest=null,string? RunnerVersion=null,int EvidenceReadRecoveries=0,
        string? HarnessError=null,IReadOnlyList<CriterionResult>? CriterionResults=null,string? ScenarioNote=null);
    /// <summary>The most recent assertion about one acceptance criterion in this stage.</summary>
    internal sealed record CriterionResult(int Index,bool Satisfied,int Run);
    internal sealed record Request(long Generation,string Key,ComputeCommand Command);
    internal sealed record Report(Guid RequestId,string SourceCommitSha,string BuildSha256,string ScriptSha256,string BrowserVersion,bool Passed,
        IReadOnlyList<Check> Checks,IReadOnlyList<string> Errors,string EvidenceSha256,int EvidenceBytes,
        string? ScriptError=null,IReadOnlyList<string>? OmittedEvidence=null);
    internal sealed record Check(string Name,bool Satisfied,string Evidence,int? Criterion=null,string? Kind=null);
    internal sealed record Receipt(Guid RequestId,string SourceCommitSha,string BuildSha256,string ScriptSha256,string BrowserVersion,bool Passed,
        int CheckCount,int ErrorCount,string EvidenceSha256,int EvidenceBytes);
}
