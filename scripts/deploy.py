#!/usr/bin/env python3
"""Bicep 展開。引数なしでは実行しない。秘密値はコマンドラインへ渡さない。"""
import argparse
import copy
from datetime import datetime, timezone
import json
import os
import re
import subprocess
import tempfile
from pathlib import Path
from env_config import ROOT, load_env
from cost_config import integer, parameters as cost_parameters

RG = "dse-growth-loop-rg"


def analysis_execution_template(snapshot, person_id, run_id, cutoff):
    """Prepare per-execution overrides offline; never enable AI or start a job."""
    if any(not isinstance(value, str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}', value)
           for value in (person_id, run_id)):
        raise ValueError('analysis person/run IDs are required and must be valid IDs')
    try:
        timestamp = datetime.fromisoformat(cutoff.replace('Z', '+00:00'))
        if timestamp.tzinfo is None:
            raise ValueError()
    except (AttributeError, TypeError, ValueError):
        raise ValueError('analysis cutoff requires an ISO-8601 timestamp with timezone') from None
    if not isinstance(snapshot, dict) or snapshot.get('name') != 'dse-business-analysis':
        raise ValueError('snapshot must be the reviewed dse-business-analysis job')
    properties = snapshot.get('properties', {})
    if not isinstance(properties, dict):
        raise ValueError('analysis job properties must be an object')
    config = properties.get('configuration', {})
    if not isinstance(config, dict):
        raise ValueError('analysis job configuration must be an object')
    manual = config.get('manualTriggerConfig', {})
    if not isinstance(manual, dict):
        raise ValueError('analysis manual trigger configuration must be an object')
    if (config.get('triggerType') != 'Manual' or config.get('replicaRetryLimit') != 0 or
            config.get('replicaTimeout') != 180 or manual.get('parallelism') != 1 or manual.get('replicaCompletionCount') != 1):
        raise ValueError('analysis job requires Manual/180s/no retry/one replica configuration')
    template = copy.deepcopy(properties.get('template', {}))
    if not isinstance(template, dict):
        raise ValueError('analysis template must be an object')
    containers = template.get('containers', [])
    if (not isinstance(containers, list) or len(containers) != 1 or not isinstance(containers[0], dict) or template.get('initContainers') or
            set(template) - {'containers', 'initContainers'}):
        raise ValueError('analysis template requires exactly one container and no init containers')
    container = containers[0]
    if not isinstance(container.get('resources'), dict):
        raise ValueError('analysis container resources must be an object')
    if (container.get('name') != 'analysis' or container.get('args') != ['--analyze-business'] or container.get('command') or
            not isinstance(container.get('image'), str) or not container['image'] or
            container.get('resources', {}).get('cpu') != 0.25 or container.get('resources', {}).get('memory') != '0.5Gi'):
        raise ValueError('analysis container command/image/resources do not match the reviewed job')
    env = container.get('env', [])
    if not isinstance(env, list) or any(not isinstance(item, dict) for item in env):
        raise ValueError('analysis environment must be an array of objects')
    names = [item.get('name') for item in env]
    if (any(not isinstance(name, str) or not name for name in names) or len(names) != len(set(names)) or
            any(('value' in item) == ('secretRef' in item) or not isinstance(item.get('value', item.get('secretRef')), str) for item in env)):
        raise ValueError('analysis environment must contain unique names with value or secretRef')
    overrides = {'BusinessAnalysis__PersonId': person_id, 'BusinessAnalysis__RunId': run_id,
                 'BusinessAnalysis__Cutoff': timestamp.astimezone(timezone.utc).isoformat().replace('+00:00', 'Z')}
    container['env'] = [item for item in env if item['name'] not in overrides]
    container['env'].extend({'name': name, 'value': value} for name, value in overrides.items())
    return template


def identity_settings(env):
    pattern = r"Auth__(Bindings__\d+__(TenantId|Oid|PrincipalId|PersonId|Enabled|Roles__\d+)|Grants__\d+__(PrincipalId|ScopeIds__\d+))"
    return [{"name": key, "value": value} for key, value in sorted(env.items()) if re.fullmatch(pattern, key) and value]


def main():
    parser = argparse.ArgumentParser(description="dse-growth-loop-rg の基盤・アプリ配置")
    parser.add_argument("action", choices=["validate", "what-if", "foundation", "build", "push", "apps", "seed", "analysis-plan"])
    parser.add_argument("--env-file", type=Path, default=ROOT / ".env")
    parser.add_argument("--include-apps", action="store_true", help="what-if でアプリ配置も比較する")
    parser.add_argument("--build-method", choices=["local", "acr"], default=None)
    parser.add_argument("--confirm-cost", action="store_true", help="対象と費用の承認後にのみ指定。有料操作を許可")
    parser.add_argument('--analysis-job-snapshot', type=Path, help='private ARM job snapshot; offline plan only')
    parser.add_argument('--analysis-person-id')
    parser.add_argument('--analysis-run-id')
    parser.add_argument('--analysis-cutoff')
    parser.add_argument('--analysis-output', type=Path, help='new private override JSON file (never overwritten)')
    args = parser.parse_args()
    if args.build_method and args.action != "build":
        parser.error("--build-method は build と組み合わせてください。")
    if args.include_apps and args.action != "what-if":
        parser.error("--include-apps は what-if と組み合わせてください。")
    analysis_args = (args.analysis_job_snapshot, args.analysis_person_id, args.analysis_run_id, args.analysis_cutoff, args.analysis_output)
    if args.action == 'analysis-plan':
        if not all(analysis_args) or args.confirm_cost:
            parser.error('analysis-plan requires all five --analysis-* inputs; it does not accept --confirm-cost or start resources')
        try:
            template = analysis_execution_template(json.loads(args.analysis_job_snapshot.read_text(encoding='utf-8')),
                                                   args.analysis_person_id, args.analysis_run_id, args.analysis_cutoff)
            descriptor = os.open(args.analysis_output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(descriptor, 'w', encoding='utf-8') as output:
                json.dump(template, output, ensure_ascii=False, indent=2)
        except (OSError, ValueError, TypeError, KeyError) as error:
            parser.error('analysis-plan could not prepare a new private template: ' + type(error).__name__)
        print('Prepared private analysis execution template; no Azure call or job start.')
        return
    if any(value is not None for value in analysis_args):
        parser.error('--analysis-* inputs are only valid with analysis-plan')
    env = load_env(args.env_file)

    def run(command, capture=False):
        return subprocess.run(command, cwd=ROOT, env=env, check=True, text=True, capture_output=capture)

    if args.action == "validate":
        with tempfile.TemporaryDirectory(prefix="dse-bicep-") as folder:
            run(["az", "bicep", "build", "--file", "infra/main.bicep", "--outfile", f"{folder}/main.json"])
        return

    tag = env.get("IMAGE_TAG", "initial")
    if not re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}", tag):
        parser.error("IMAGE_TAG が不正です。")
    if args.action == "build" and args.build_method != "acr":
        for component in ("Api", "Worker"):
            run(["docker", "build", "--platform", "linux/amd64", "--tag", f"dse-{component.lower()}:{tag}",
                 "--file", f"src/Dse.{component}/Dockerfile", "."])
        return

    paid = args.action in ("foundation", "apps", "seed", "push", "build")
    if paid:
        try:
            budget = integer(env, "APPROVED_MONTHLY_BUDGET_JPY", 0)
        except ValueError as error:
            parser.error(str(error))
        if not args.confirm_cost or budget <= 0:
            parser.error("有料操作です。承認済み APPROVED_MONTHLY_BUDGET_JPY と --confirm-cost が必要です。")
    subscription = env.get("AZURE_SUBSCRIPTION_ID", "")
    if not subscription:
        parser.error("AZURE_SUBSCRIPTION_ID を設定してください。")
    location = env.get("AZURE_LOCATION", "japaneast")

    if args.action == "seed":
        run(["az", "containerapp", "job", "start", "--name", "dse-seed-demo", "--resource-group", RG, "--subscription", subscription])
        return
    if args.action in ("build", "push"):
        registry = run(["az", "deployment", "sub", "show", "--name", "dse-growth-loop", "--subscription", subscription,
                        "--query", "properties.outputs.registryName.value", "--output", "tsv"], capture=True).stdout.strip()
        if not re.fullmatch(r"[a-zA-Z0-9]{5,50}", registry):
            parser.error("先に foundation を実行してください。ACR 名を取得できませんでした。")
        if args.action == "push":
            run(["az", "acr", "login", "--name", registry, "--subscription", subscription])
            for component in ("api", "worker"):
                image = f"dse-{component}:{tag}"
                target = f"{registry}.azurecr.io/{image}"
                run(["docker", "tag", image, target])
                run(["docker", "push", target])
            return
        for component in ("Api", "Worker"):
            run(["az", "acr", "build", "--registry", registry, "--subscription", subscription,
                 "--image", f"dse-{component.lower()}:{tag}", "--file", f"src/Dse.{component}/Dockerfile", "."])
        return

    include_apps = args.action == "apps" or args.include_apps
    values = {
        "resourceGroupName": RG, "location": location,
        "foundryLocation": env.get("FOUNDRY_LOCATION", location),
        "deployApplications": include_apps, "imageTag": tag,
        "entraTenantId": env.get("Auth__TenantId", ""), "entraClientId": env.get("Auth__ClientId", ""),
        "allowedObjectIds": [value for key, value in sorted(env.items()) if re.fullmatch(r"Auth__AllowedObjectIds__\d+", key) and value],
        "modelName": env.get("FOUNDRY_MODEL_NAME", ""), "modelVersion": env.get("FOUNDRY_MODEL_VERSION", ""),
        "modelDeploymentName": env.get("Foundry__Deployment", ""),
        "modelSku": env.get("FOUNDRY_MODEL_SKU", "GlobalStandard"),
        "modelCapacity": int(env.get("FOUNDRY_MODEL_CAPACITY", "1")),
    }
    try:
        values.update(cost_parameters(env))
        values["runtimeSettings"].extend(identity_settings(env))
    except ValueError as error:
        parser.error(str(error))
    if include_apps:
        if not all(values[key] for key in ("entraTenantId", "entraClientId", "allowedObjectIds")):
            parser.error("apps には Auth__TenantId、Auth__ClientId、Auth__AllowedObjectIds__0 以降が必要です。")
        if len(values["allowedObjectIds"]) != 1:
            parser.error("初期のAzure検証環境は許可ユーザー1名に限定してください。")
    if values["modelDeploymentName"] and not (values["modelName"] and values["modelVersion"]):
        parser.error("モデル配置時は FOUNDRY_MODEL_NAME と FOUNDRY_MODEL_VERSION が必要です。")
    parameters = {"$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
                  "contentVersion": "1.0.0.0", "parameters": {k: {"value": v} for k, v in values.items()}}
    with tempfile.TemporaryDirectory(prefix="dse-parameters-") as folder:
        path = Path(folder) / "parameters.json"
        path.write_text(json.dumps(parameters), encoding="utf-8")
        path.chmod(0o600)
        run(["az", "deployment", "sub", "what-if" if args.action == "what-if" else "create",
             "--name", "dse-growth-loop", "--subscription", subscription, "--location", location,
             "--template-file", "infra/main.bicep", "--parameters", f"@{path}"])


if __name__ == "__main__":
    main()
