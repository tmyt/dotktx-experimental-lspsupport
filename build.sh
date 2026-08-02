#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd "$(dirname "$0")" && pwd)"

dotnet pack "$repo_dir/packaging/DotKtx.Experimental.LspSupport.pack.csproj" \
  -c Release \
  -o "$repo_dir/artifacts"
