#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd "$(dirname "$0")" && pwd)"
version_prefix="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$repo_dir/Directory.Build.props")"
version_suffix="$(sed -n 's:.*<VersionSuffix>\(.*\)</VersionSuffix>.*:\1:p' "$repo_dir/Directory.Build.props")"
package_version="$version_prefix"
test -z "$version_suffix" || package_version="$version_prefix-$version_suffix"

"$repo_dir/build.sh"

tool_out="$repo_dir/src/DotKtx.Experimental.LspSupport/bin/Release/net10.0"
test -f "$tool_out/kotlin-stdlib.jar"
test -f "$tool_out/dotkt-lsp-overlay.jar"
for entry in \
  kotlin/clr/ClrRef.class \
  kotlin/clr/StackBuffer.class \
  kotlin/clr/Span.class \
  kotlin/clr/ClrEvent.class \
  kotlin/clr/EventSubscription.class \
  META-INF/dotkt_lsp_overlay.kotlin_module; do
  jar tf "$tool_out/dotkt-lsp-overlay.jar" | grep -qx "$entry"
done

nupkg="$repo_dir/artifacts/DotKtx.Experimental.LspSupport.$package_version.nupkg"
unzip -Z1 "$nupkg" | grep -qx 'tools/net10.0/any/kotlin-stdlib.jar'
unzip -Z1 "$nupkg" | grep -qx 'tools/net10.0/any/dotkt-lsp-overlay.jar'

# NuGet treats one package ID/version as immutable. This repository intentionally rebuilds the same
# experimental version while developing, so remove its isolated cached extraction before restore;
# otherwise a fresh nupkg can be silently tested through yesterday's targets and tools.
rm -rf "$repo_dir/artifacts/.packages/dotktx.experimental.lspsupport/$package_version"
rm -rf "$repo_dir/tests/Smoke/.dotkt" "$repo_dir/tests/Smoke/workspace.json"
dotnet restore "$repo_dir/tests/Smoke/Smoke.ktproj" --force
dotnet build "$repo_dir/tests/Smoke/Smoke.ktproj" -c Release --no-restore

test -f "$repo_dir/tests/Smoke/workspace.json"
test -f "$repo_dir/tests/Smoke/.dotkt/lsp/.stamp"
rg -uu -q 'class StringBuilder' "$repo_dir/tests/Smoke/.dotkt/lsp/stubs"
rg -q 'dotkt-lsp-overlay\.jar' "$repo_dir/tests/Smoke/workspace.json"
rg -q 'kotlin-stdlib\.jar' "$repo_dir/tests/Smoke/workspace.json"

echo "DotKtx.Experimental.LspSupport: package and MSBuild generation verified"
