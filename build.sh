#!/usr/bin/env bash
set -euo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)

configuration=Release
output_directory="$script_dir/artifacts/plugins"
project="$script_dir/src/Plugins.ForwardAPI/Plugins.ForwardAPI.csproj"
plugin_key=forwardapi
while (($#)); do
    case $1 in
        --configuration) configuration=${2:?Missing configuration}; shift 2 ;;
        --output-directory) output_directory=${2:?Missing output directory}; shift 2 ;;
        --project) project=${2:?Missing project}; shift 2 ;;
        --plugin-key) plugin_key=${2:?Missing plugin key}; shift 2 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done
[[ $configuration == Debug || $configuration == Release ]] || { echo "Invalid configuration: $configuration" >&2; exit 2; }
[[ $plugin_key =~ ^[a-z0-9][a-z0-9_-]*$ ]] || { echo "Invalid plugin key: $plugin_key" >&2; exit 2; }
[[ -f $project ]] || { echo "Project not found: $project" >&2; exit 1; }

project_dir=$(cd -- "$(dirname -- "$project")" && pwd -P)
project_name=$(basename -- "$project" .csproj)
output="$output_directory/$plugin_key"
if [[ -d $output && -n $(find "$output" -mindepth 1 -maxdepth 1 -print -quit) ]]; then
    echo "Output directory is not empty: $output" >&2
    exit 1
fi

dotnet build "$project" -c "$configuration" --disable-build-servers -m:1 \
    -p:ConcurrentBuild=false -p:UseSharedCompilation=false
source_dir="$project_dir/bin/$configuration/net10.0"
[[ -d $source_dir ]] || { echo "Build output missing: $source_dir" >&2; exit 1; }
mkdir -p -- "$output"
while IFS= read -r -d '' file; do
    [[ ${file##*/} == Router.Contracts.* ]] && continue
    relative=${file#"$source_dir"/}
    destination="$output/$relative"
    mkdir -p -- "$(dirname -- "$destination")"
    cp -- "$file" "$destination"
done < <(find "$source_dir" -type f -print0)
[[ -f "$output/$project_name.dll" ]] || { echo "Main DLL missing: $output/$project_name.dll" >&2; exit 1; }
echo "Build complete: $output"
