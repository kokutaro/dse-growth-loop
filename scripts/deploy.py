#!/usr/bin/env python3
"""Bicep 展開。引数なしでは実行しない。秘密値はコマンドラインへ渡さない。"""
import argparse
import json
import re
import subprocess
import tempfile
from pathlib import Path
from env_config import ROOT, load_env

RG = "dse-growth-loop-rg"


def main():
    parser = argparse.ArgumentParser(description="dse-growth-loop-rg の基盤・アプリ配置")
    parser.add_argument("action", choices=["validate", "what-if", "foundation", "build", "apps", "seed"])
    parser.add_argument("--env-file", type=Path, default=ROOT / ".env")
    parser.add_argument("--include-apps", action="store_true", help="what-if でアプリ配置も比較する")
    args = parser.parse_args()
    if args.include_apps and args.action != "what-if":
        parser.error("--include-apps は what-if と組み合わせてください。")
    env = load_env(args.env_file)

    def run(command, capture=False):
        return subprocess.run(command, cwd=ROOT, env=env, check=True, text=True, capture_output=capture)

    if args.action == "validate":
        with tempfile.TemporaryDirectory(prefix="dse-bicep-") as folder:
            run(["az", "bicep", "build", "--file", "infra/main.bicep", "--outfile", f"{folder}/main.json"])
        return

    subscription = env.get("AZURE_SUBSCRIPTION_ID", "")
    if not subscription:
        parser.error("AZURE_SUBSCRIPTION_ID を設定してください。")
    location = env.get("AZURE_LOCATION", "japaneast")
    tag = env.get("IMAGE_TAG", "initial")
    if not re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}", tag):
        parser.error("IMAGE_TAG が不正です。")

    if args.action == "seed":
        run(["az", "containerapp", "job", "start", "--name", "dse-seed-demo", "--resource-group", RG, "--subscription", subscription])
        return
    if args.action == "build":
        registry = run(["az", "deployment", "sub", "show", "--name", "dse-growth-loop", "--subscription", subscription,
                        "--query", "properties.outputs.registryName.value", "--output", "tsv"], capture=True).stdout.strip()
        if not re.fullmatch(r"[a-zA-Z0-9]{5,50}", registry):
            parser.error("先に foundation を実行してください。ACR 名を取得できませんでした。")
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
    if include_apps:
        if not all(values[key] for key in ("entraTenantId", "entraClientId", "allowedObjectIds")):
            parser.error("apps には Auth__TenantId、Auth__ClientId、Auth__AllowedObjectIds__0 以降が必要です。")
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
