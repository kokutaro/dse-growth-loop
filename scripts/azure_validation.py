#!/usr/bin/env python3
"""Issue #9 のローカル／読み取り点検。配置・推論・データ変更は行わない。"""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import time

from deploy import RG
from env_config import ROOT

PROVIDERS = ("Microsoft.App", "Microsoft.OperationalInsights", "Microsoft.Insights",
             "Microsoft.ContainerRegistry", "Microsoft.Storage", "Microsoft.DocumentDB",
             "Microsoft.KeyVault", "Microsoft.ManagedIdentity", "Microsoft.CognitiveServices",
             "Microsoft.Consumption")
SCENARIOS = ("environment-review", "deployment", "entra", "managed-identity", "live-ai",
             "zero-scale", "recovery", "redeploy-rollback-stop", "cost-reconciliation")


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def new_report(run_id, mode, commit, dirty):
    return {"schemaVersion": 1, "runId": run_id, "startedAt": utc_now(), "commit": commit,
            "worktreeDirty": dirty, "mode": mode, "liveAcceptance": "not_run",
            "approval": "pending", "checks": [],
            "scenarios": [{"id": s, "status": "not_run"} for s in SCENARIOS],
            "measurements": dict.fromkeys(("requestUnits", "inputTokens", "outputTokens",
                                          "coldStartMs", "warmRequestMs", "returnToZeroSeconds",
                                          "billedCost", "retainedStorageCost", "logIngestBytes"))}


def rows(value):
    if not isinstance(value, list) or any(not isinstance(r, dict) for r in value):
        raise ValueError("Unexpected response shape")
    return value


def boolean(value):
    if not isinstance(value, bool):
        raise ValueError("Expected bool")
    return value


def fields(value, names):
    if not isinstance(value, dict):
        raise ValueError("Unexpected response shape")
    # Keep only scalar fields; never serialize env, tags, secrets, or raw responses.
    return {k: value[k] for k in names if k in value and (value[k] is None or isinstance(value[k], (str, int, float, bool)))}


def inventory(value):
    return [dict(fields(r, ("name", "type", "location")), sku=fields(r.get("sku") or {}, ("name", "tier"))) for r in rows(value)]


def permissions(value):
    result = []
    for row in rows(value["value"]):
        result.append({k: [v for v in row.get(k, []) if isinstance(v, str)]
                       for k in ("actions", "notActions", "dataActions", "notDataActions")})
    return result


def models(value):
    result = []
    for row in rows(value):
        model = row.get("model") or {}
        result.append(dict(fields(row, ("kind",)), model=fields(model, ("name", "version", "format", "lifecycleStatus")),
                           skus=[fields(s, ("name", "capacity")) for s in rows(model.get("skus") or [])]))
    return result


def quotas(value):
    return [dict(fields(r, ("currentValue", "limit", "unit")), name=fields(r.get("name") or {}, ("value",))) for r in rows(value)]


def check_command(check_id, command, project=None, acceptable=lambda data: True):
    check = {"id": check_id, "command": command, "startedAt": utc_now(), "status": "failed"}
    start = time.monotonic()
    try:
        response = subprocess.run(command, cwd=ROOT, check=True, capture_output=True, text=True, timeout=120)
        check["data"] = project(json.loads(response.stdout)) if project else None
        check["status"] = "passed" if acceptable(check["data"]) else "failed"
        if check["status"] == "failed":
            check["error"] = "PRECONDITION_NOT_MET"
    except subprocess.CalledProcessError as error:
        check.update(error="COMMAND_FAILED", exitCode=error.returncode)
    except subprocess.TimeoutExpired:
        check["error"] = "COMMAND_TIMEOUT"
    except OSError:
        check["error"] = "CLI_UNAVAILABLE"
    except (ValueError, TypeError, KeyError):
        check["error"] = "INVALID_RESPONSE"
    check["elapsedMs"] = round((time.monotonic() - start) * 1000)
    return check


def az(command, subscription):
    return ["az", *command, "--subscription", subscription, "--only-show-errors", "--output", "json"]


def preflight(subscription, location, foundry_location):
    checks = []

    def read(check_id, command, project, acceptable=lambda data: True):
        check = check_command(check_id, az(command, subscription), project, acceptable)
        checks.append(check)
        return check

    account = read("account", ["account", "show"], lambda v: fields(v, ("id", "tenantId", "state")),
                   lambda v: v.get("id") == subscription and v.get("state") == "Enabled")
    if account["status"] != "passed":
        return checks
    group = read("resource-group", ["group", "exists", "--name", RG], boolean)
    if group.get("data") is True:
        read("resource-inventory", ["resource", "list", "--resource-group", RG,
                                   "--query", "[].{name:name,type:type,location:location,sku:sku}"], inventory)
    for provider in PROVIDERS:
        read("provider-" + provider, ["provider", "show", "--namespace", provider,
                                     "--query", "{namespace:namespace,registrationState:registrationState}"],
             lambda v: fields(v, ("namespace", "registrationState")), lambda v: v.get("registrationState") == "Registered")
    for name, scope in [("subscription", f"/subscriptions/{subscription}"),
                        *([("resource-group", f"/subscriptions/{subscription}/resourceGroups/{RG}")] if group.get("data") is True else [])]:
        read("permissions-" + name, ["rest", "--method", "get", "--url",
             "https://management.azure.com" + scope + "/providers/Microsoft.Authorization/permissions?api-version=2022-04-01"], permissions)
    read("foundry-model-catalog", ["cognitiveservices", "model", "list", "--location", foundry_location], models)
    read("foundry-quota", ["cognitiveservices", "usage", "list", "--location", foundry_location], quotas)
    read("app-quota", ["rest", "--method", "get", "--url",
         f"https://management.azure.com/subscriptions/{subscription}/providers/Microsoft.App/locations/{location}/usages?api-version=2025-07-01"],
         lambda v: quotas(v["value"]))
    return checks


def replicas(subscription, app):
    base = ["containerapp", "revision", "list", "--name", app, "--resource-group", RG,
            "--query", "[?properties.active].{name:name}"]
    # Filter again in Python as well, so inactive revisions cannot affect counts.
    def active(value):
        return [fields(r, ("name",)) for r in rows(value) if r.get("properties", {}).get("active", True)]
    revisions = check_command("active-revisions", az(base, subscription), active, lambda v: bool(v))
    checks = [revisions]
    if revisions["status"] == "passed":
        for revision_row in revisions["data"]:
            name = revision_row.get("name", "")
            if not re.fullmatch(r"[A-Za-z0-9-]+", name):
                revisions.update(status="failed", error="INVALID_RESPONSE")
                break
            command = ["containerapp", "replica", "list", "--name", app, "--resource-group", RG,
                       "--revision", name, "--query", "[].{name:name}"]
            checks.append(check_command("replicas-" + name, az(command, subscription), lambda v: {"count": len(rows(v))}))
    return checks


def revision():
    commit = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip()
    dirty = bool(subprocess.run(["git", "status", "--porcelain"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.strip())
    return commit, dirty


def local():
    with tempfile.TemporaryDirectory(prefix="dse-validation-") as folder:
        return [check_command("bicep", ["az", "bicep", "build", "--file", "infra/main.bicep", "--outfile", str(Path(folder) / "main.json")]),
                check_command("whitespace", ["git", "diff", "--check"])]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("local", "preflight", "replicas"))
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--out", type=Path, default=ROOT / "docs/validation-results")
    parser.add_argument("--subscription")
    parser.add_argument("--location", default="japaneast")
    parser.add_argument("--foundry-location", default="japaneast")
    parser.add_argument("--app", choices=("dse-api", "dse-worker"), default="dse-api")
    args = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,79}", args.run_id):
        parser.error("run-id は英数字・ハイフン・下線の1〜80文字で指定してください。")
    if args.action != "local" and (not args.subscription or not re.fullmatch(r"[A-Za-z0-9-]+", args.subscription)):
        parser.error("読み取り先の --subscription を明示してください。CLIの既定先には依存しません。")
    if any(not re.fullmatch(r"[a-z0-9]+", v) for v in (args.location, args.foundry_location)):
        parser.error("リージョン識別子が不正です。")
    folder = args.out / args.run_id
    try:
        folder.mkdir(parents=True, exist_ok=False)
    except FileExistsError:
        parser.error("既存runを上書きできません。別のrun-idを指定してください。")
    try:
        commit, dirty = revision()
        report = new_report(args.run_id, args.action, commit, dirty)
        report["target"] = {"subscription": args.subscription, "resourceGroup": RG,
                            "location": args.location, "foundryLocation": args.foundry_location}
        report["checks"] = local() if args.action == "local" else preflight(args.subscription, args.location, args.foundry_location) if args.action == "preflight" else replicas(args.subscription, args.app)
        report["finishedAt"] = utc_now()
        path = folder / "report.json"
        path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        path.chmod(0o600)
        print(path)
        return 0 if all(c["status"] == "passed" for c in report["checks"]) else 1
    except (OSError, subprocess.SubprocessError):
        print("記録作成に失敗しました。CLI／書込権限を確認してください。", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
