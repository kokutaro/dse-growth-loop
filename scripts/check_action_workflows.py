"""Validate current GitHub queues, then run pinned actionlint on all other syntax.

actionlint 1.7.12 predates concurrency.queue. Only a validated standalone queue
field is blanked; expressions and every other field reach the real linter.
https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency
"""
import argparse
from pathlib import Path
import re
import subprocess
import yaml

ROOT=Path(__file__).resolve().parents[1]

def pairs(node):
    return node.value if isinstance(node,yaml.MappingNode) else []

def compatible_source(source):
    try:root=yaml.compose(source,Loader=yaml.SafeLoader)
    except yaml.YAMLError:raise ValueError('INVALID_WORKFLOW_YAML') from None
    nodes=[]
    for key,value in pairs(root):
        if key.value=='concurrency':nodes.append(value)
        if key.value=='jobs':
            for _,job in pairs(value):
                nodes.extend(v for k,v in pairs(job) if k.value=='concurrency')
    source_lines=source.splitlines(keepends=True)
    lines=list(source_lines)
    for node in nodes:
        queues=[(k,v) for k,v in pairs(node) if k.value=='queue']
        if not queues:continue
        if len(queues)!=1:raise ValueError('DUPLICATE_CONCURRENCY_QUEUE')
        key,value=queues[0]
        if not isinstance(value,yaml.ScalarNode) or value.tag!='tag:yaml.org,2002:str' or value.value not in ('max','single'):
            raise ValueError('INVALID_CONCURRENCY_QUEUE')
        if value.value=='max':
            for k,v in pairs(node):
                if k.value=='cancel-in-progress' and not (isinstance(v,yaml.ScalarNode) and v.tag=='tag:yaml.org,2002:bool' and v.value.lower()=='false'):
                    raise ValueError('MAX_QUEUE_REQUIRES_CANCEL_FALSE')
        line=key.start_mark.line
        if value.start_mark.line!=line or not re.fullmatch(r'\s*queue\s*:\s*(max|single)\s*(?:#.*)?',source_lines[line].rstrip('\r\n')):
            raise ValueError('QUEUE_MUST_USE_LITERAL_BLOCK_ENTRY')
        # Preserve line numbers for actionlint diagnostics against the real file.
        lines[line]='\n' if lines[line].endswith('\n') else ''
    return ''.join(lines)

def main(argv=None):
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--actionlint',default='actionlint')
    parser.add_argument('--no-shellcheck',action='store_true')
    parser.add_argument('files',nargs='*',type=Path)
    args=parser.parse_args(argv)
    paths=args.files or sorted([*ROOT.glob('.github/workflows/*.yml'),*ROOT.glob('.github/workflows/*.yaml')])
    failed=not paths
    for path in paths:
        try:
            source=compatible_source(path.read_text())
            command=[args.actionlint,'-stdin-filename',str(path),'-']
            if args.no_shellcheck:command.insert(1,'-shellcheck=')
            result=subprocess.run(command,input=source,text=True,capture_output=True)
            print(result.stdout+result.stderr,end='')
            failed=failed or result.returncode!=0
        except (OSError,ValueError) as error:
            print(str(path)+': '+(str(error) if isinstance(error,ValueError) else 'WORKFLOW_LINTER_UNAVAILABLE'))
            failed=True
    return 1 if failed else 0

if __name__=='__main__':raise SystemExit(main())
