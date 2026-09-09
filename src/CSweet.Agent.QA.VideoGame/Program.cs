using CSweet.Agent.SDK;
using CSweet.Agent.QA.VideoGame;
using Microsoft.Extensions.Hosting;

if (args.Contains("--self-test", StringComparer.Ordinal))
{
    var agent = new SpecialistAgent();
    if (string.IsNullOrWhiteSpace(agent.AgentId) || agent.Version != "2.3.1" || string.IsNullOrWhiteSpace(agent.PrimaryCapability))
        throw new InvalidOperationException("Specialist identity self-test failed.");
    Console.WriteLine($"{agent.AgentId} {agent.Version} self-test passed.");
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.AddCSweetAgent<SpecialistAgent>();
await builder.Build().RunAsync();
