"""Real Chromium acceptance for the packaged isolated interaction runner."""
import hashlib
import json
import pathlib
import subprocess
import tempfile
import uuid
import zipfile

root = pathlib.Path(__file__).resolve().parents[1]
resources = root / 'src/CSweet.Agent.QA.VideoGame'
artifacts = root / '.artifacts/browser-interactions'
artifacts.mkdir(parents=True, exist_ok=True)
html = """<html><head><link rel="icon" href="data:,"><style>
body {margin:0;overflow:hidden} #portrait {display:none}
@media (orientation:portrait) {#portrait {display:block} button {display:none}}
</style></head><body><button id="play">Play</button><div id="portrait">Rotate to Landscape</div>
<script>window.entries=0;document.querySelector('#play').onclick=()=>{window.entries++;};</script></body></html>"""
script = """await page.goto(origin); await page.click('#play');
check('scene entry', await page.evaluate(()=>window.entries===1), 'One live click produced one entry');
const audio=await page.evaluate(async()=>{
 const context=new AudioContext();await context.resume();const oscillator=context.createOscillator();
 const analyser=context.createAnalyser();oscillator.connect(analyser);analyser.connect(context.destination);
 oscillator.start();await new Promise(resolve=>setTimeout(resolve,100));
 const samples=new Float32Array(analyser.fftSize);analyser.getFloatTimeDomainData(samples);
 const peak=Math.max(...samples.map(Math.abs));oscillator.stop();await context.close();return peak;
});
check('real Web Audio', audio>0.1, 'Observed browser waveform peak '+audio);
await page.setViewportSize({width:390,height:844});
check('portrait rejection', await page.locator('#portrait').isVisible() && !await page.locator('#play').isVisible(),
 'Live Chromium portrait media query hides input and displays rejection');"""
cases = [('behavior', script, html, True),
         ('failed-assertion', "await page.goto(origin); check('intentional',false,'Observed failure');", html, False),
         ('page-error', "await page.goto(origin); check('load',true,'Loaded fixture');", html+'<script>throw Error("fixture-runtime-error")</script>', False),
         ('external-request', "await page.goto(origin); check('load',true,'Loaded fixture');", html+'<script src="https://192.0.2.1/blocked.js"></script>', False),
         ('empty-assertions', 'await page.goto(origin);', html, False)]
for name, test, source, expected in cases:
    with tempfile.TemporaryDirectory(prefix='fixture-', dir=artifacts) as temp:
        directory = pathlib.Path(temp)
        evidence = directory / 'evidence'
        evidence.mkdir()
        with zipfile.ZipFile(directory / 'build.zip', 'w') as archive:
            archive.writestr('index.html', source)
        for original, destination in [('qa-interactions.cjs','runner.cjs'),('qa-interactions.py','run.py')]:
            directory.joinpath(destination).write_bytes(resources.joinpath(original).read_bytes())
        directory.joinpath('test.cjs').write_bytes(test.encode('utf-8'))
        request = dict(requestId=str(uuid.uuid4()), sourceCommitSha='a'*40,
                       buildSha256=hashlib.sha256(directory.joinpath('build.zip').read_bytes()).hexdigest(),
                       scriptSha256=hashlib.sha256(test.encode('utf-8')).hexdigest())
        container = 'csweet-qa-fixture-'+uuid.uuid4().hex
        process = subprocess.run(['docker','run','--rm','--name',container,'--init','--network=none','--read-only',
            '--cap-drop=ALL','--security-opt=no-new-privileges','--memory=1536m','--cpus=2','--pids-limit=256',
            '--shm-size=256m','--tmpfs=/tmp:rw,size=256m','--mount',f'type=bind,src={directory},dst=/input,readonly',
            '--mount',f'type=bind,src={directory},dst=/checks,readonly','--mount',f'type=bind,src={evidence},dst=/evidence',
            '--entrypoint','timeout','csweet/browser-tests:1.64.0','--signal=KILL','26s','python3','/checks/run.py',json.dumps(request)],
            capture_output=True,text=True,timeout=40)
        assert process.returncode == 0, (name,process.stdout,process.stderr)
        report = json.loads(process.stdout)
        assert report['passed'] == expected, (name,report)
        for field,value in request.items():
            assert report[field] == value, (name,field)
        payload = evidence.joinpath('evidence.zip').read_bytes()
        assert hashlib.sha256(payload).hexdigest() == report['evidenceSha256']
        assert len(payload) == report['evidenceBytes']
        with zipfile.ZipFile(evidence / 'evidence.zip') as archive:
            assert {'test.cjs','final.png','trace.zip','result.json'} <= set(archive.namelist())
            assert archive.read('test.cjs') == test.encode('utf-8')
        assert subprocess.run(['docker','inspect',container],capture_output=True).returncode != 0, 'Container retained'
        artifacts.joinpath(name+'.zip').write_bytes(payload)
        artifacts.joinpath(name+'.json').write_text(json.dumps(report,indent=2))
        print(name+': passed',flush=True)
print('All five real Chromium behavioral fixtures passed.')
