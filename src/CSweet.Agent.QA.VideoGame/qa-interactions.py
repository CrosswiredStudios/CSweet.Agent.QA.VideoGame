import hashlib
import json
import pathlib
import subprocess
import sys
import zipfile

sys.path.insert(0, '/opt/browser')
from extract import extract

# The platform exports at most 4 MiB of evidence per run (PlatformComputeClient). Evidence is kept in priority
# order until it no longer fits; only the result and the authored test are required. Size never fails a run.
BUDGET = 4 * 1024 * 1024
REQUIRED = ['result.json', 'test.cjs']
OPTIONAL = ['final.png', 'trace.zip']

request = json.loads(sys.argv[1])
root = pathlib.Path('/tmp/build')
root.mkdir()
extract('/input/build.zip', root, request['buildSha256'])
subprocess.run(['node', '/checks/runner.cjs', json.dumps(request)], timeout=22, check=True)
evidence = pathlib.Path('/evidence')
report = json.loads((evidence / 'result.json').read_text())
known = [name for name in REQUIRED + OPTIONAL if (evidence / name).exists()]
order = known + sorted(f.name for f in evidence.iterdir() if f.name not in known and f.name != 'evidence.zip')


def build(names):
    report['omittedEvidence'] = [name for name in order if name not in names]
    (evidence / 'result.json').write_text(json.dumps(report))
    with zipfile.ZipFile(evidence / 'evidence.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
        for name in names:
            archive.write(evidence / name, name)
    return (evidence / 'evidence.zip').stat().st_size


names = list(order)
while len(names) > len(REQUIRED) and sum((evidence / n).stat().st_size for n in names) > BUDGET:
    names.pop()
size = build(names)
while size > BUDGET and len(names) > len(REQUIRED):
    names.pop()
    size = build(names)
if size > BUDGET:
    raise ValueError('The QA result and authored test alone exceed the 4 MiB evidence budget')
payload = (evidence / 'evidence.zip').read_bytes()
report['evidenceSha256'] = hashlib.sha256(payload).hexdigest()
report['evidenceBytes'] = len(payload)
# The approved callback allows 8 KiB stdout. Full assertion evidence is in the
# digest-identified ZIP, not squeezed or truncated into the control receipt.
receipt = {key: report[key] for key in ('requestId', 'sourceCommitSha', 'buildSha256',
           'scriptSha256', 'browserVersion', 'passed', 'evidenceSha256', 'evidenceBytes')}
receipt['checkCount'] = len(report['checks'])
receipt['errorCount'] = len(report['errors'])
receipt['omittedEvidence'] = report['omittedEvidence']
print(json.dumps(receipt, separators=(',', ':')))
