#!/usr/bin/env bash
set -euo pipefail

query_dir="$(builtin cd -- "$(dirname -- "$0")" && pwd)"
expected=(
  attribution_check_status.kql
  connector_task_states_and_restarts.kql
  consumer_lag_by_partition.kql
  grace_window_headroom.kql
  inline_gap_head_loss_detection.kql
  per_stage_lag_by_tenant.kql
  sentnotifications_conflict_rate.kql
  spend_against_budget.kql
  tail_drift_per_tenant_per_hour.kql
)

mapfile -t actual < <(find "$query_dir" -maxdepth 1 -type f -name '*.kql' -print \
  | sed 's#^.*/##' | sort)
if ((${#actual[@]} != ${#expected[@]})); then
  printf 'expected exactly %d .kql files, found %d\n' "${#expected[@]}" "${#actual[@]}" >&2
  exit 1
fi
for expected_file in "${expected[@]}"; do
  if [[ ! -f "$query_dir/$expected_file" ]]; then
    printf 'missing expected query: %s\n' "$expected_file" >&2
    exit 1
  fi
  first_line=$(head -n 1 "$query_dir/$expected_file")
  if [[ "$first_line" != '// Bad reading:'* ]]; then
    printf '%s must start with a bad-reading comment\n' "$expected_file" >&2
    exit 1
  fi
done

harness_root=$(mktemp -d)
trap 'rm -rf "$harness_root"' EXIT
harness="$harness_root/kql-validator"
dotnet new console --framework net10.0 --output "$harness" --no-restore >/dev/null
cat > "$harness/NuGet.Config" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
XML
cat > "$harness/Program.cs" <<'CS'
using Kusto.Language;
using Kusto.Language.Symbols;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: validator <query-directory>");
    return 2;
}

var queryDirectory = args[0];
var tables = new[]
{
    Table("AppTraces", "(TimeGenerated:datetime, Message:string, Properties:dynamic, OperationId:string, AppRoleName:string, AppRoleInstance:string)"),
    Table("AppMetrics", "(TimeGenerated:datetime, Name:string, Properties:dynamic, Sum:real)"),
    Table("KubePodInventory", "(TimeGenerated:datetime, ContainerStatus:string, ContainerStatusReason:string, PodStatus:string, ContainerRestartCount:int, PodRestartCount:int, Name:string, ContainerName:string, Namespace:string)"),
    Table("ContainerLogV2", "(TimeGenerated:datetime, LogMessage:dynamic, ContainerName:string, PodName:string, PodNamespace:string)"),
    Table("Usage", "(TimeGenerated:datetime, Quantity:real, DataType:string, QuantityUnit:string, StartTime:datetime)"),
};
var database = new DatabaseSymbol("offline", tables.Cast<Symbol>());
var globals = GlobalState.Default.WithDatabase(database);
var failures = 0;
foreach (var path in Directory.EnumerateFiles(queryDirectory, "*.kql").OrderBy(path => path, StringComparer.Ordinal))
{
    var code = KustoCode.ParseAndAnalyze(File.ReadAllText(path), globals);
    var diagnostics = code.GetDiagnostics();
    if (diagnostics.Count == 0)
    {
        Console.WriteLine($"PASS {Path.GetFileName(path)}");
        continue;
    }

    failures++;
    foreach (var diagnostic in diagnostics)
    {
        Console.Error.WriteLine($"FAIL {Path.GetFileName(path)}: {diagnostic}");
    }
}

return failures == 0 ? 0 : 1;

static TableSymbol Table(string name, string schema) =>
    TableSymbol.From(schema).WithName(name);
CS
dotnet add "$harness" package Microsoft.Azure.Kusto.Language --version 12.4.1 \
  --source https://api.nuget.org/v3/index.json --no-restore >/dev/null
dotnet restore "$harness" --configfile "$harness/NuGet.Config" >/dev/null
dotnet run --no-restore --project "$harness" -- "$query_dir"
