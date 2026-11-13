import hashlib
import json
import pathlib
import subprocess
import sys
import zipfile

sys.path.insert(0, '/opt/browser')
from extract import extract

# Evidence has no size cap: Core stages exported archives in parts (C-Sweet ComputeEvidenceParts). Everything the
# run recorded is kept, in a stable order: result, authored test, screenshot, trace, then anything else.
ORDER = ['result.json', 'test.cjs', 'final.png', 'trace.zip']

request = json.loads(sys.argv[1])
root = pathlib.Path('/tmp/build')
root.mkdir()
extract('/input/build.zip', root, request['buildSha256'])
subprocess.run(['node', '/checks/runner.cjs', json.dumps(request)], timeout=22, check=True)
evidence = pathlib.Path('/evidence')
report = json.loads((evidence / 'result.json').read_text())
known = [name for name in ORDER if (evidence / name).exists()]
names = known + sorted(f.name for f in evidence.iterdir() if f.name not in known and f.name != 'evidence.zip')
report['omittedEvidence'] = []
(evidence / 'result.json').write_text(json.dumps(report))
with zipfile.ZipFile(evidence / 'evidence.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
    for name in names:
        archive.write(evidence / name, name)
digest = hashlib.sha256()
with open(evidence / 'evidence.zip', 'rb') as packed:
    for block in iter(lambda: packed.read(1024 * 1024), b''):
        digest.update(block)
report['evidenceSha256'] = digest.hexdigest()
report['evidenceBytes'] = (evidence / 'evidence.zip').stat().st_size
# The approved callback allows 8 KiB stdout. Full assertion evidence is in the
# digest-identified ZIP, not squeezed or truncated into the control receipt.
receipt = {key: report[key] for key in ('requestId', 'sourceCommitSha', 'buildSha256',
           'scriptSha256', 'browserVersion', 'passed', 'evidenceSha256', 'evidenceBytes')}
receipt['checkCount'] = len(report['checks'])
receipt['errorCount'] = len(report['errors'])
receipt['omittedEvidence'] = report['omittedEvidence']
print(json.dumps(receipt, separators=(',', ':')))
