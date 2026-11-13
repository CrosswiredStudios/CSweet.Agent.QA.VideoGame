const fs = require('node:fs/promises');
const crypto = require('node:crypto');
const { chromium } = require('/opt/browser/node_modules/playwright');
const { serverFor } = require('/opt/browser/runner.cjs');
const { allowedRequest } = require('./qa-request-policy.cjs');

// This runner and the independently authored test execute only inside the isolated,
// network-disabled browser container. Neither receives host credentials or paths.
async function run(request) {
  const script = await fs.readFile('/checks/test.cjs', 'utf8');
  if (crypto.createHash('sha256').update(script).digest('hex') !== request.scriptSha256)
    throw Error('QA test script digest mismatch');
  const server = serverFor('/tmp/build');
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const origin = `http://127.0.0.1:${server.address().port}`;
  let browser;
  const checks = [], errors = [];
  const report = { ...request, browserVersion: '', passed: false, checks, errors };
  const recordError = value => { if (errors.length < 12) errors.push(String(value).slice(0, 300)); };
  try {
    browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--use-gl=angle',
      '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--autoplay-policy=no-user-gesture-required'] });
    report.browserVersion = browser.version();
    const context = await browser.newContext({ viewport: { width: 1280, height: 720 }, serviceWorkers: 'block' });
    await context.route('**/*', route => {
      if (allowedRequest(route.request().url(), origin)) return route.continue();
      recordError('External request blocked: ' + route.request().url());
      return route.abort('blockedbyclient');
    });
    await context.tracing.start({ screenshots: true, snapshots: true, sources: false });
    const page = await context.newPage();
    page.setDefaultTimeout(3000);
    page.on('pageerror', error => recordError(error.message));
    page.on('console', message => { if (message.type() === 'error') recordError(message.text()); });
    page.on('requestfailed', request => recordError('Request failed: ' + request.url()));
    page.on('response', response => { if (response.status() >= 400) recordError(`HTTP ${response.status()}: ${response.url()}`); });
    page.on('websocket', socket => recordError('Unsupported WebSocket: ' + socket.url()));
    // check(name, satisfied, evidence, { criterion, kind }): criterion is the zero-based acceptance criterion the
    // check serves; kind 'assertion' (default) means satisfied = the criterion holds, 'observation' records a value
    // and never fails the run.
    const criteriaCount = Number.isInteger(request.criteriaCount) && request.criteriaCount > 0 ? request.criteriaCount : 64;
    const check = (name, satisfied, evidence, options) => {
      const criterion = options?.criterion, kind = options?.kind ?? 'assertion';
      if (checks.length >= 24 || typeof name !== 'string' || !name.trim() || name.length > 160 ||
          checks.some(item => item.name === name) || typeof satisfied !== 'boolean' ||
          typeof evidence !== 'string' || !evidence.trim() || evidence.length > 1000)
        throw Error('Use at most 24 uniquely named boolean checks with concrete evidence (up to 1000 characters).');
      if ((criterion !== undefined && (!Number.isInteger(criterion) || criterion < 0 || criterion >= criteriaCount)) ||
          (kind !== 'assertion' && kind !== 'observation'))
        throw Error(`check options: criterion is a zero-based acceptance criterion index below ${criteriaCount}; kind is 'assertion' or 'observation'.`);
      checks.push(criterion === undefined && kind === 'assertion' ? { name, satisfied, evidence } : { name, satisfied, evidence, criterion, kind });
    };
    // attach('scenarios', value): the shared browser scenario evaluation (CSweet.Agent.SDK BrowserScenarios), kept whole
    // in the evidence archive so QA can compare it with the author's generated observation records.
    const attach = async (name, value) => {
      const text = JSON.stringify(value);
      if (name !== 'scenarios' || typeof text !== 'string' || text.length > 2 * 1024 * 1024)
        throw Error("attach accepts only 'scenarios' with up to 2 MiB of JSON.");
      await fs.writeFile('/evidence/scenarios.json', text);
    };
    try {
      // QA may inspect and drive Playwright in this container. It must load the
      // production bundle from origin; scripts are retained for independent review.
      const AsyncFunction = Object.getPrototypeOf(async function() {}).constructor;
      await new AsyncFunction('page', 'context', 'origin', 'check', 'attach', script)(page, context, origin, check, attach);
    } catch (error) {
      // The authored test failing is a harness problem, not an application defect; page errors stay in errors.
      report.scriptError = String(error?.message ?? error).slice(0, 600);
    }
    finally {
      try { await page.screenshot({ path: '/evidence/final.png', timeout: 2000 }); }
      catch (error) { recordError(error.message); }
      await context.tracing.stop({ path: '/evidence/trace.zip' });
      await context.close();
    }
    if (!checks.length && !report.scriptError) report.scriptError = 'The test recorded no checks.';
    // Observations record values; only assertions decide the run.
    report.passed = checks.length > 0 && !report.scriptError && errors.length === 0 &&
      checks.every(check => check.kind === 'observation' || check.satisfied);
  } finally {
    if (browser) await browser.close();
    await new Promise(resolve => server.close(resolve));
  }
  await fs.writeFile('/evidence/test.cjs', script);
  await fs.writeFile('/evidence/result.json', JSON.stringify(report));
}

if (require.main === module) run(JSON.parse(process.argv[2])).catch(error => {
  console.error(error.message); process.exitCode = 1;
});
