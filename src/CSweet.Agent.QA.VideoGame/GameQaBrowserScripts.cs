using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CSweet.Agent.SDK;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame;

internal sealed class GameQaBrowserScripts(WorkExecutionAssignment assignment,GameQaInputWorkspace source,
    GameQaJournal journal,AgentRuntimeContext context)
{
    internal AIFunction Tool()=>AIFunctionFactory.Create(async(string script,CancellationToken token)=>{
        await source.VerifyAsync(assignment,context,token);
        var bytes=new UTF8Encoding(false,true).GetBytes(script);
        if(bytes.Length is <1 or >12000)throw new ArgumentException("Use a focused browser test of 1-12,000 UTF-8 bytes.");
        var relative="qa/browser-"+Convert.ToHexStringLower(SHA256.HashData(bytes))+".cjs";
        // Persist first: a lost reply or replaced workspace can restore exactly this draft.
        await journal.RecordAsync("browser-test-script",relative,new Draft(relative,script),token);
        await WriteAsync(source.Path,relative,script,source.IsOriginalFile,token);
        await source.VerifyAsync(assignment,context,token);
        return new{testScriptPath=relative,note="Independent test draft saved, not executed. Call test_web_interactions with this path. It is restored on source-bound continuation."};
    },"save_browser_test_script","Save the body of a focused async Playwright test receiving page, context, origin and check(name,boolean,evidence). This writes only a content-addressed untracked qa/browser-*.cjs draft, never tracked source. It durably retains the exact script and restores drafts after workspace replacement. Supply script as text; no shell/heredoc quoting is needed, and Phaser shutdown event names are ordinary test data. This does not execute or pass any check. Use page.evaluate(fn,{...}) for multiple values; inspect real APIs rather than relying on mock-only fields. Then call test_web_interactions with the returned path.");

    internal async Task RestoreAsync(CancellationToken token)
    {
        var drafts=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var entry in await journal.ReadScriptDraftsAsync(token)) {
            if(entry.Kind=="browser-test-script") {
                var draft=entry.Result.Deserialize<Draft>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if(draft is not null)drafts[draft.Path]=draft.Script;
            } else if(TryReadLegacyDraft(entry.Request,out var draft) && !source.IsOriginalFile(draft.Path))drafts[draft.Path]=draft.Script;
        }
        foreach(var draft in drafts)await WriteAsync(source.Path,draft.Key,draft.Value,source.IsOriginalFile,token);
    }
    internal static bool TryReadLegacyDraft(string request,out Draft draft)
    {
        draft=null!;
        try {
            using var document=JsonDocument.Parse(request);
            if(!document.RootElement.TryGetProperty("command",out var field)||field.ValueKind!=JsonValueKind.String)return false;
            // Recover text only. Never execute, interpret or replay the retained shell command.
            var match=Regex.Match(field.GetString()!,@"(?:^|&&\s*)cat\s*>\s*(qa/[A-Za-z0-9_.-]+)\s*<<'EOF'\r?\n([\s\S]*?)\r?\nEOF(?:\r?\n|$)",RegexOptions.None,TimeSpan.FromSeconds(1));
            if(!match.Success)return false;
            draft=new(match.Groups[1].Value,match.Groups[2].Value);
            return Regex.IsMatch(draft.Path,@"\Aqa/[A-Za-z0-9_-][A-Za-z0-9_.-]*\.(cjs|mjs|js)\z") &&
                new UTF8Encoding(false,true).GetByteCount(draft.Script) is >0 and <=12000;
        }catch(JsonException){return false;}
    }
    internal static async Task WriteAsync(string workspace,string relative,string script,Func<string,bool> original,CancellationToken token)
    {
        if(!Regex.IsMatch(relative,@"\Aqa/[A-Za-z0-9_-][A-Za-z0-9_.-]*\.(cjs|mjs|js)\z")||original(relative)||
            new UTF8Encoding(false,true).GetByteCount(script) is <1 or >12000)
            throw new InvalidOperationException("A retained browser draft must be a bounded untracked qa script, never original source.");
        var path=Path.Combine(workspace,relative.Replace('/',Path.DirectorySeparatorChar));
        for(var ancestor=path;ancestor is not null;ancestor=Path.GetDirectoryName(ancestor)) {
            try {if((File.GetAttributes(ancestor)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Browser draft paths cannot be links.");}
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
        }
        if(File.Exists(path)&&await File.ReadAllTextAsync(path,token)==script)return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,script,new UTF8Encoding(false),token);
    }
    internal sealed record Draft(string Path,string Script);
}
