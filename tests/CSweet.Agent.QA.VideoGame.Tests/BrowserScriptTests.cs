using System.Text.Json;
namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class BrowserScriptTests
{
    [Fact]
    public void Legacy_recovery_extracts_draft_text_without_executing_shell()
    {
        var request=JsonSerializer.Serialize(new{command="cd /old/path && cat > qa/audio.mjs <<'EOF'\nconst shutdown='shutdown';\nEOF\nrm /something"});
        Assert.True(GameQaBrowserScripts.TryReadLegacyDraft(request,out var draft));
        Assert.Equal("qa/audio.mjs",draft.Path);Assert.Equal("const shutdown='shutdown';",draft.Script);
        Assert.False(GameQaBrowserScripts.TryReadLegacyDraft(JsonSerializer.Serialize(new{command="cat > ../../escape.js <<'EOF'\nx\nEOF"}),out _));
        Assert.False(GameQaBrowserScripts.TryReadLegacyDraft(JsonSerializer.Serialize(new{command="echo cat > qa/a.js"}),out _));
        Assert.False(GameQaBrowserScripts.TryReadLegacyDraft("not JSON",out _));
    }
    [Fact]
    public async Task Draft_materialization_cannot_overwrite_original_source_or_escape_qa()
    {
        var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"qa"));
        try {
            await File.WriteAllTextAsync(Path.Combine(root,"qa","original.js"),"original");
            bool Original(string p)=>p=="qa/original.js";
            await Assert.ThrowsAsync<InvalidOperationException>(()=>GameQaBrowserScripts.WriteAsync(root,"qa/original.js","changed",Original,default));
            Assert.Equal("original",await File.ReadAllTextAsync(Path.Combine(root,"qa","original.js")));
            foreach(var path in new[]{"../escape.js","qa/../escape.js","qa/nested/escape.js","/tmp/escape.js","qa/script.sh"})
                await Assert.ThrowsAsync<InvalidOperationException>(()=>GameQaBrowserScripts.WriteAsync(root,path,"text",Original,default));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>GameQaBrowserScripts.WriteAsync(root,"qa/a.cjs",new string('x',12001),Original,default));
            await GameQaBrowserScripts.WriteAsync(root,"qa/a.cjs","const shutdown = true;",Original,default);
            await GameQaBrowserScripts.WriteAsync(root,"qa/a.cjs","const shutdown = true;",Original,default);
            Assert.Equal("const shutdown = true;",await File.ReadAllTextAsync(Path.Combine(root,"qa","a.cjs")));
        }finally{Directory.Delete(root,true);}
    }
}
