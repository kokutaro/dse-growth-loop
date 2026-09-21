"""単純な KEY=VALUE 形式を読み、既存の環境変数を優先する。シェルとして実行しない。"""
import os
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def load_env(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    if path.exists():
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            key, separator, value = line.partition("=")
            key, value = key.strip(), value.strip()
            if not separator or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key):
                raise ValueError(f"{path.name}:{number}: KEY=VALUE 形式で指定してください。")
            if value.startswith(('"', "'")):
                if len(value) < 2 or value[-1] != value[0]:
                    raise ValueError(f"{path.name}:{number}: 引用符が閉じていません。")
                value = value[1:-1]
            values[key] = value
    return {**values, **os.environ}
