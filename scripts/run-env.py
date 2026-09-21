#!/usr/bin/env python3
"""例: python3 scripts/run-env.py -- dotnet run --project src/Dse.Api"""
import argparse
import os
from pathlib import Path
from env_config import ROOT, load_env

parser = argparse.ArgumentParser(description=".env と環境変数を子プロセスに注入")
parser.add_argument("--env-file", type=Path, default=ROOT / ".env")
parser.add_argument("command", nargs=argparse.REMAINDER)
args = parser.parse_args()
command = args.command[1:] if args.command[:1] == ["--"] else args.command
if not command:
    parser.error("実行コマンドが必要です。")
os.chdir(ROOT)
os.execvpe(command[0], command, load_env(args.env_file))
