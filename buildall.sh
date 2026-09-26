#!/usr/bin/env bash
set -euo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
configuration=Release
output_directory="$script_dir/artifacts/plugins"
while (($#)); do
    case $1 in
        --configuration) configuration=${2:?Missing configuration}; shift 2 ;;
        --output-directory) output_directory=${2:?Missing output directory}; shift 2 ;;
        *) echo "Unknown argument: $1" >&2; exit 2 ;;
    esac
done
[[ $configuration == Debug || $configuration == Release ]] || { echo "Invalid configuration: $configuration" >&2; exit 2; }

mapfile -d '' -t projects < <(find "$script_dir/src" -type f -name 'Plugins.*.csproj' -print0 | sort -z)
((${#projects[@]})) || { echo 'No Plugins.*.csproj projects found under src.' >&2; exit 1; }
declare -A keys=()
for project in "${projects[@]}"; do
    name=${project##*/}
    key=${name#Plugins.}
    key=${key%.csproj}
    key=${key,,}
    [[ $key =~ ^[a-z0-9][a-z0-9_-]*$ ]] || { echo "Invalid plugin name: $project" >&2; exit 1; }
    [[ ! -v keys[$key] ]] || { echo "Duplicate plugin key: $key" >&2; exit 1; }
    keys[$key]=1
    target="$output_directory/$key"
    if [[ -d $target && -n $(find "$target" -mindepth 1 -maxdepth 1 -print -quit) ]]; then
        echo "Output directory is not empty: $target" >&2
        exit 1
    fi
done

for project in "${projects[@]}"; do
    name=${project##*/}
    key=${name#Plugins.}
    key=${key%.csproj}
    bash "$script_dir/build.sh" --project "$project" --plugin-key "${key,,}" \
        --configuration "$configuration" --output-directory "$output_directory"
done
