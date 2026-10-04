#!/usr/bin/env python3
"""Decode gzip chunks from the validation job's own captured log. No network."""
import base64,gzip,hashlib,json,re,sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
def decode(path, expected_run="issue30-hardening-20261003-v1", regression=True):
    lines=[]
    for line in Path(path).read_text().splitlines():
        match=re.search(r'"Log":"(.*)"\}\s*$',line)
        if match:
            value=re.sub(r'^[FP] ','',match[1])
            # Azure CLI's log formatter exposes JSON-escaped '+' as '\+' (invalid JSON).
            # Limit normalization to the known base64 alphabet; the report hash verifies it.
            if '"data"' in value:value=re.sub(r'\\([+/=])',r'\1',value)
            lines.append(value)
    chunks={};decoder=json.JSONDecoder()
    for i,line in enumerate(lines):
        if line.startswith('REGRESSION_CHUNK '):
            block=line.split('REGRESSION_CHUNK ',1)[1]+'\n'+'\n'.join(lines[i+1:])
            chunk,_=decoder.raw_decode(block)
            if chunk['index'] in chunks:raise ValueError('Duplicate chunk')
            chunks[chunk['index']]=chunk
    if not chunks:raise ValueError('No chunks')
    first=chunks[min(chunks)];count=first['count'];expected=first['sha256']
    if set(chunks)!=set(range(count)) or any(c['count']!=count or c['sha256']!=expected for c in chunks.values()):raise ValueError('Missing or inconsistent chunks')
    data=gzip.decompress(base64.b64decode(''.join(chunks[i]['data'] for i in range(count)),validate=True))
    if hashlib.sha256(data).hexdigest()!=expected:raise ValueError('Report checksum mismatch')
    report=json.loads(data)
    if report['plan']['evaluationId']!=expected_run or report['plan']['observedRegressionSet']!=regression or report['plan']['independentHoldout']==regression:raise ValueError('Wrong report scope')
    return data,{'chunks':count,'bytes':len(data),'sha256':expected,'reportComplete':report.get('completed'),'rows':len(report.get('rows',[])),'aiAudits':len(report.get('aiAudits',[]))}
def main():
    data,meta=decode(sys.argv[1]);out=ROOT/'docs/validation-results/20261003-issue-30-hardening-production'
    (out/'production-report.json').write_bytes(data);(out/'export-provenance.json').write_text(json.dumps(meta,indent=2)+'\n');print(json.dumps(meta))
if __name__=='__main__':main()
