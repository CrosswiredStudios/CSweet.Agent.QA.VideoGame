using System.Text.Json;
using CSweet.Agent.SDK;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class ReportTransportTests
{
    [Theory]
    [InlineData(false,8192)] [InlineData(false,32768)] [InlineData(true,32768)] [InlineData(false,128000)]
    public async Task Reserved_report_uses_real_platform_response_schema_and_malformed_json_cannot_become_a_verdict(bool malformed,int outputBudget)
    {
        var root=Path.Combine(Path.GetTempPath(),"qa-report-transport-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var json=new JsonSerializerOptions(JsonSerializerDefaults.Web);var structuredCalls=0;
        var runtime=new AgentTestRuntime().RegisterCapability<JsonElement,JsonElement>(CapabilityNames.Platform.LlmChatStream,(request,_)=> {
            var text="Evaluation finished; a structured report is still required.";
            if(request.TryGetProperty("responseSchema",out var schema)&&schema.ValueKind==JsonValueKind.Object)
            {
                structuredCalls++;
                if(outputBudget==128000)Assert.Contains("long-evidence-tail",request.GetProperty("messages")[1].GetProperty("text").GetString());
                Assert.Equal("qa_report",request.GetProperty("responseSchemaName").GetString());
                Assert.Equal(0,request.GetProperty("tools").GetArrayLength());Assert.Equal(JsonValueKind.Null,request.GetProperty("maxOutputTokens").ValueKind);
                Assert.Contains("report",request.GetProperty("messages")[0].GetRawText(),StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("save_qa_test_plan",request.GetRawText());
                text=malformed?"unparseable report":JsonSerializer.Serialize(new {summary="Observed actual output",passed=false,
                    validations=new[]{new GitValidationResult("actual fixture check",false,1,"Observed actual failure")},
                    findings=new[]{"Observed actual failure"},criteria=new[]{new GameQaCriterion("Observed",false,"Observed actual failure")}},json);
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new {role="assistant",text},json));
        });
        using var client=runtime.CreateContext().CreateChatClient(new(Guid.NewGuid(),"fixture"));
        using var budget=new GameQaReport.CorrectionBudget(default,true);
        var report=GameQaReport.CreateTool(root,new string('a',40),["Observed"],budget);
        try
        {
            if(malformed)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(()=>GameQaFinalization.RunAsync(client,report,
                    "Assess observed fixture output",budget,outputBudget,default));
                Assert.Null(budget.AcceptedReport);Assert.True(budget.Exhausted);Assert.Equal(4,structuredCalls);
            }
            else
            {
                await GameQaFinalization.RunAsync(client,report,outputBudget==128000?new string('x',70000)+"long-evidence-tail":"Assess observed fixture output",budget,outputBudget,default);
                Assert.NotNull(budget.AcceptedReport);Assert.False(budget.AcceptedReport.Passed);Assert.Equal(1,structuredCalls);
            }
        }
        finally {Directory.Delete(root,true);}
    }
}
