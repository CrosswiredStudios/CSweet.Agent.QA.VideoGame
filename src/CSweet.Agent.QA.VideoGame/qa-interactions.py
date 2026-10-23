import hashlib
import json
import pathlib
import subprocess
import sys
import zipfile

sys.path.insert(0, '/opt/browser')
from extract import extract

request = json.loads(sys.argv[1])
root = pathlib.Path('/tmp/build')
root.mkdir()
extract('/input/build.zip', root, request['buildSha256'])
subprocess.run(['node', '/checks/runner.cjs', json.dumps(request)], timeout=22, check=True)
evidence = pathlib.Path('/evidence')
report = json.loads((evidence / 'result.json').read_text())
with zipfile.ZipFile(evidence / 'evidence.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
    total = 0
    for file in sorted(evidence.iterdir()):
        if file.name == 'evidence.zip':
            continue
        total += file.stat().st_size
        if total > 4 * 1024 * 1024:
            raise ValueError('QA interaction evidence exceeds 4 MiB')
        archive.write(file, file.name)
payload = (evidence / 'evidence.zip').read_bytes()
if len(payload) > 4 * 1024 * 1024:
    raise ValueError('QA interaction evidence ZIP exceeds 4 MiB')
report['evidenceSha256'] = hashlib.sha256(payload).hexdigest()
report['evidenceBytes'] = len(payload)
# The approved callback allows 8 KiB stdout. Full assertion evidence is in the
# digest-identified ZIP, not squeezed or truncated into the control receipt.
receipt = {key: report[key] for key in ('requestId', 'sourceCommitSha', 'buildSha256',
           'scriptSha256', 'browserVersion', 'passed', 'evidenceSha256', 'evidenceBytes')}
receipt['checkCount'] = len(report['checks'])
receipt['errorCount'] = len(report['errors'])
print(json.dumps(receipt, separators=(',', ':')))
