#!/usr/bin/env bash
set -euo pipefail
if [[ ${1:-} == --help ]]; then
  echo 'worker-trial.sh inspect|plan|prepare|duplicate CONFIG_JSON EVIDENCE_DIR'
  echo 'Uses existing Azure identity for inspect/plan only. Mutations require the bounded CLI inside the existing Worker; never infers or changes IAM.'
  exit 0
fi
[[ $# == 3 && ( $1 == inspect || $1 == plan || $1 == prepare || $1 == duplicate ) ]]
trial_mode=$1
trial_config=$(cd "$(dirname "$2")" && pwd)/$(basename "$2")
trial_evidence=$(cd "$3" && pwd)
trial_repository=$(cd "$(dirname "$0")/.." && pwd)
cd "$trial_repository"
{
printf '#:property PublishAot=false\n#:property JsonSerializerIsReflectionEnabledByDefault=true\n'
printf '#:project %s/src/Dse.Infrastructure/Dse.Infrastructure.csproj\n' "$trial_repository"
cat <<'CS'
using Dse.Infrastructure;
using Microsoft.Extensions.Configuration;
using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(3));
var config=new ConfigurationBuilder().AddJsonFile(args[1]).Build();
Console.WriteLine((await WorkerTrialOperations.RunAsync(args[0],config,args[2],deadline.Token)).ToJsonString());
CS
} | AZURE_TOKEN_CREDENTIALS=AzureCliCredential dotnet run - -- "$trial_mode" "$trial_config" "$trial_evidence"
