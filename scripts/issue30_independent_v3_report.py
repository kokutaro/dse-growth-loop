#!/usr/bin/env python3
"""Score the frozen approved v3 service capture. No model calls."""
import argparse
import hashlib
import json
from pathlib import Path
from issue30_independent_v2_report import score
ROOT=Path(__file__).resolve().parents[1]
RUN='issue30-independent-20261004-v3'
def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--capture',required=True,type=Path)
    parser.add_argument('--output',required=True,type=Path)
    args=parser.parse_args()
    dataset=ROOT/'evaluation/issue-30/independent-v3/approved/dataset.json'
    data=json.loads(dataset.read_text());report=json.loads(args.capture.read_text())
    if report['plan']['datasetSha256']!=hashlib.sha256(dataset.read_bytes()).hexdigest() or data['humanReviewStatus']!='human-approved':raise ValueError('Unapproved or changed labels')
    result=score(data,report,run=RUN,manual_codes=('CLASSIFICATION_OUT_OF_SCOPE',),require_expected_unknown=True)
    args.output.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n')
    print(json.dumps({k:result[k] for k in ['serviceQualityPassed','overallGate','usage']},indent=2))
if __name__=='__main__':main()
