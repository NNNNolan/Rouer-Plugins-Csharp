#!/usr/bin/env bash
set -euo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
dotnet test "$script_dir/tests/Router.Tests/Router.Tests.csproj" \
    --disable-build-servers -m:1 -p:ConcurrentBuild=false -p:UseSharedCompilation=false \
    --logger 'console;verbosity=minimal'
