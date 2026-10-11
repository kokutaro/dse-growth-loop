#!/usr/bin/env python3
"""Issue #9 のローカル／読み取り点検。配置・推論・データ変更は行わない。"""
import argparse
from datetime import datetime, timezone
import json
import math
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
APP_ENVIRONMENTS = {"dse-api": "dse-environment", "dse-worker": "dse-environment",
                    "dse-api-vnet": "dse-environment-vnet-jpe", "dse-worker-vnet": "dse-environment-vnet-jpe"}


def environment_id(subscription, environment):
    return f"/subscriptions/{subscription}/resourceGroups/{RG}/providers/Microsoft.App/managedEnvironments/{environment}"


def matches_environment(value, app, expected):
    observed = value.get("environment") or value.get("managedEnvironment")
    return value.get("name") == app and isinstance(observed, str) and observed.lower() == expected.lower()


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


def environment_quota_available(value, existing):
    if existing:
        return True
    meter = next((r for r in value if r.get("name", {}).get("value") == "ManagedEnvironmentCount"), {})
    current, limit = meter.get("currentValue"), meter.get("limit")
    return (type(current) in (int, float) and type(limit) in (int, float)
            and math.isfinite(current) and math.isfinite(limit) and current >= 0 and limit - current >= 1)


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


def preflight(subscription, location, foundry_location, environment="dse-environment"):
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
    existing_environment = False
    if group.get("data") is True:
        resources = read("resource-inventory", ["resource", "list", "--resource-group", RG,
                                              "--query", "[].{name:name,type:type,location:location,sku:sku}"], inventory)
        existing_environment = resources["status"] == "passed" and any(
            r.get("type", "").lower() == "microsoft.app/managedenvironments" and r.get("name") == environment
            for r in resources.get("data", []))
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
         lambda v: quotas(v["value"]), lambda v: environment_quota_available(v, existing_environment))
    return checks


def revision_inventory(value):
    result = []
    for row in rows(value):
        name, active, count, state = (row.get(k) for k in ("name", "active", "replicas", "status"))
        if (not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9-]+", name)
                or type(active) is not bool or type(count) is not int or count < 0
                or not isinstance(state, str) or not state):
            raise ValueError("Unknown revision state")
        result.append({"name": name, "active": active, "replicas": count, "status": state})
    if len({r["name"] for r in result}) != len(result):
        raise ValueError("Duplicate revision")
    return result


def confirmed_stopped(checks):
    """A control-plane snapshot, never a billing or future-stop guarantee."""
    if not isinstance(checks, list) or any(not isinstance(c, dict) for c in checks):
        return False
    if len(checks) < 3 or any(c.get("status") != "passed" for c in checks):
        return False
    if checks[0].get("id") != "app-environment" or checks[1].get("id") != "all-revisions":
        return False
    revisions = checks[1].get("data")
    if not isinstance(revisions, list) or not revisions or len(checks) != len(revisions) + 2:
        return False
    try:
        revision_inventory(revisions)
    except (ValueError, TypeError):
        return False
    for revision_row, check in zip(revisions, checks[2:]):
        if not isinstance(check.get("data"), dict):
            return False
        if (revision_row.get("active") is not False or type(revision_row.get("replicas")) is not int
                or revision_row["replicas"] != 0 or revision_row.get("status") != "Stopped"
                or check.get("id") != "replicas-" + revision_row.get("name", "")
                or type(check.get("data", {}).get("count")) is not int or check["data"]["count"] != 0):
            return False
    return True


def replicas(subscription, app, environment=None):
    environment = environment or APP_ENVIRONMENTS.get(app)
    if not environment or APP_ENVIRONMENTS.get(app) != environment:
        raise ValueError("App/environment mismatch")
    expected = environment_id(subscription, environment).lower()
    identity = check_command("app-environment", az(["containerapp", "show", "--name", app,
        "--resource-group", RG, "--query",
        "{name:name,environment:properties.environmentId,managedEnvironment:properties.managedEnvironmentId}"], subscription),
        lambda v: fields(v, ("name", "environment", "managedEnvironment")),
        lambda v: matches_environment(v, app, expected))
    checks = [identity]
    if identity["status"] != "passed":
        return checks
    base = ["containerapp", "revision", "list", "--all", "--name", app, "--resource-group", RG,
            "--query", "[].{name:name,active:properties.active,replicas:properties.replicas,status:properties.runningState}"]
    revisions = check_command("all-revisions", az(base, subscription), revision_inventory, lambda v: bool(v))
    checks.append(revisions)
    if revisions["status"] == "passed":
        for revision_row in revisions["data"]:
            name = revision_row.get("name", "")
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
    parser.add_argument("--app", choices=tuple(APP_ENVIRONMENTS), default="dse-api")
    parser.add_argument("--environment", choices=tuple(sorted(set(APP_ENVIRONMENTS.values()))))
    args = parser.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,79}", args.run_id):
        parser.error("run-id は英数字・ハイフン・下線の1〜80文字で指定してください。")
    if args.action != "local" and (not args.subscription or not re.fullmatch(r"[A-Za-z0-9-]+", args.subscription)):
        parser.error("読み取り先の --subscription を明示してください。CLIの既定先には依存しません。")
    if any(not re.fullmatch(r"[a-z0-9]+", v) for v in (args.location, args.foundry_location)):
        parser.error("リージョン識別子が不正です。")
    environment = args.environment or APP_ENVIRONMENTS[args.app]
    if args.action == "replicas" and APP_ENVIRONMENTS[args.app] != environment:
        parser.error("app と environment の組み合わせが不正です。")
    folder = args.out / args.run_id
    try:
        folder.mkdir(parents=True, exist_ok=False)
    except FileExistsError:
        parser.error("既存runを上書きできません。別のrun-idを指定してください。")
    try:
        commit, dirty = revision()
        report = new_report(args.run_id, args.action, commit, dirty)
        report["target"] = {"subscription": args.subscription, "resourceGroup": RG,
                            "location": args.location, "foundryLocation": args.foundry_location,
                            "app": args.app, "environment": environment}
        report["checks"] = local() if args.action == "local" else preflight(args.subscription, args.location, args.foundry_location, environment) if args.action == "preflight" else replicas(args.subscription, args.app, environment)
        if args.action == "replicas":
            report["stopSnapshotConfirmed"] = confirmed_stopped(report["checks"])
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
