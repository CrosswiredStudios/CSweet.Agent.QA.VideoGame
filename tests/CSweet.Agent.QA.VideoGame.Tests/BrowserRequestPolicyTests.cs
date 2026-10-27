using System.Diagnostics;

namespace CSweet.Agent.QA.VideoGame.Tests;

public sealed class BrowserRequestPolicyTests
{
    [Fact]
    public async Task ActualNodePolicyAllowsOnlyTheLocalServerAndExtractedBuildFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "qa-request-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var policy = Path.Combine(root, "policy.cjs");
            await File.WriteAllTextAsync(policy, GameQaInteractionTesting.Resource("qa-request-policy.cjs"));
            var script = """
                const assert = require('node:assert/strict');
                const {allowedRequest} = require(process.argv[1]);
                const origin = 'http://127.0.0.1:12345';
                for (const url of [origin+'/', origin+'/styles.css?v=2',
                    'file:///tmp/build/index.html', 'file:///tmp/build/assets/tone.js',
                    'file://localhost/tmp/build/index.html', 'file:///tmp/build/a%20b.js',
                    'file:///tmp/build/assets/../index.html'])
                  assert.equal(allowedRequest(url, origin), true, url);
                for (const url of ['https://example.test/', 'http://127.0.0.1:54321/',
                    'http://localhost:12345/', 'file:///etc/passwd', 'file:///tmp/build-other/x',
                    'file:///tmp/build/../secret', 'file:///tmp/build/%2e%2e/secret',
                    'file:///tmp/build/%2e%2e%2fsecret', 'file:///tmp/build/%00',
                    'file://remote/tmp/build/index.html', 'data:text/html,hello',
                    'javascript:alert(1)', 'not-a-url'])
                  assert.equal(allowedRequest(url, origin), false, url);
                console.log('20 request policy cases passed');
                """;
            var start = new ProcessStartInfo("node") { RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-e"); start.ArgumentList.Add(script); start.ArgumentList.Add(policy);
            using var process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await error);
            Assert.Contains("20 request policy cases passed", await output);
        }
        finally { Directory.Delete(root, true); }
    }
}
