#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd "$(dirname "$0")" && pwd)"
version_prefix="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$repo_dir/Directory.Build.props")"
version_suffix="$(sed -n 's:.*<VersionSuffix>\(.*\)</VersionSuffix>.*:\1:p' "$repo_dir/Directory.Build.props")"
package_version="$version_prefix"
test -z "$version_suffix" || package_version="$version_prefix-$version_suffix"

"$repo_dir/build.sh"

tool_out="$repo_dir/src/DotKtx.Experimental.LspSupport/bin/Release/net10.0"
# Only the Kotlin core classifiers still come from the JVM distribution; nothing else may ship.
test -f "$tool_out/kotlin-builtins.jar"
test ! -f "$tool_out/kotlin-stdlib.jar"
test ! -f "$tool_out/dotkt-lsp-overlay.jar"
for entry in \
  kotlin/kotlin.kotlin_builtins \
  kotlin/collections/collections.kotlin_builtins \
  kotlin/ranges/ranges.kotlin_builtins \
  kotlin/reflect/reflect.kotlin_builtins; do
  jar tf "$tool_out/kotlin-builtins.jar" | grep -qx "$entry"
done
jar tf "$tool_out/kotlin-builtins.jar" | grep -Fxq 'kotlin/Unit.class'
if jar tf "$tool_out/kotlin-builtins.jar" | grep '\.class$' | grep -Fxqv 'kotlin/Unit.class'; then
  echo "Unit is the only allowed ABI class in the builtins archive" >&2
  exit 1
fi

nupkg="$repo_dir/artifacts/DotKtx.Experimental.LspSupport.$package_version.nupkg"
unzip -Z1 "$nupkg" | grep -qx 'tools/net10.0/any/kotlin-builtins.jar'
unzip -Z1 "$nupkg" | grep -qvx 'tools/net10.0/any/kotlin-stdlib.jar'

# NuGet treats one package ID/version as immutable. This repository intentionally rebuilds the same
# experimental version while developing, so remove its isolated cached extraction before restore;
# otherwise a fresh nupkg can be silently tested through yesterday's targets and tools.
rm -rf "$repo_dir/artifacts/.packages/dotktx.experimental.lspsupport/$package_version"
rm -rf "$repo_dir/tests/Smoke/.dotkt" "$repo_dir/tests/Smoke/workspace.json"
dotnet restore "$repo_dir/tests/Smoke/Smoke.ktproj" --force
dotnet build "$repo_dir/tests/Smoke/Smoke.ktproj" -c Release --no-restore

test -f "$repo_dir/tests/Smoke/workspace.json"
test -f "$repo_dir/tests/Smoke/.dotkt/lsp/.stamp"
java_stub_jar="$repo_dir/tests/Smoke/.dotkt/lsp/libraries/dotkt-klib-java-stubs.jar"
test -f "$java_stub_jar"
for entry in \
  'System/DayOfWeek.class' \
  'System/ICloneable.class' \
  'System/Text/Encoding.class' \
  'System/Text/Rune.class' \
  'System/Text/StringBuilder.class' \
  'System/Text/StringBuilder$AppendInterpolatedStringHandler.class' \
  'System/Text/StringBuilder$ChunkEnumerator.class' \
  'System/Text/UTF8Encoding.class'; do
  jar tf "$java_stub_jar" | grep -Fqx "$entry"
done
# DotKt emits CLR statics as members of their own type instead of duplicating them onto a
# synthetic companion. Kotlin's own standard library still has real companions (`UInt.Companion`),
# so only a companion on a CLR type indicates the old duplication has come back.
if jar tf "$java_stub_jar" | grep 'Companion' | grep -qv '^kotlin/'; then
  echo "A companion class was projected for a CLR type; current DotKt should not declare one" >&2
  jar tf "$java_stub_jar" | grep 'Companion' | grep -v '^kotlin/' | head -5 >&2
  exit 1
fi
# Every referenced declaration now has a JVM shape, so the Kotlin source fallback must be empty.
if [ -d "$repo_dir/tests/Smoke/.dotkt/lsp/stubs" ] && \
   [ -n "$(find "$repo_dir/tests/Smoke/.dotkt/lsp/stubs" -name '*.kt' -print -quit)" ]; then
  echo "Declarations were left in the Kotlin source projection:" >&2
  find "$repo_dir/tests/Smoke/.dotkt/lsp/stubs" -name '*.kt' | head -5 >&2
  exit 1
fi
# A CLR attribute is only usable if it arrives as a real JVM annotation.
javap -classpath "$java_stub_jar" System.ObsoleteAttribute | \
  grep -q '^public interface System.ObsoleteAttribute extends java.lang.annotation.Annotation {'
javap -classpath "$java_stub_jar" System.ObsoleteAttribute | grep -q 'public abstract java.lang.String Message();'
javap -classpath "$java_stub_jar" -s System.Text.StringBuilder | grep -q 'public native java.lang.String ToString();'
javap -classpath "$java_stub_jar" -s System.Text.StringBuilder | grep -q 'descriptor: ()Ljava/lang/String;'
javap -classpath "$java_stub_jar" -p System.Text.StringBuilder | \
  grep -q 'public native <T> System.Text.StringBuilder AppendJoin(char, System.Collections.Generic.IEnumerable<T>);'
javap -classpath "$java_stub_jar" System.ICloneable | grep -q '^public interface System.ICloneable {'
javap -classpath "$java_stub_jar" -s System.Text.Encoding | grep -q 'public static final System.Text.Encoding UTF8;'
javap -classpath "$java_stub_jar" -s System.Text.Encoding | grep -q 'public static native System.Text.Encoding GetEncoding(int);'
javap -classpath "$java_stub_jar" -s System.Text.Rune | grep -q 'public static final System.Text.Rune ReplacementChar;'
javap -classpath "$java_stub_jar" System.Text.Encoding | grep -q '^public abstract class System.Text.Encoding implements System.ICloneable {'
javap -classpath "$java_stub_jar" System.Text.UTF8Encoding | grep -q '^public class System.Text.UTF8Encoding extends System.Text.Encoding {'
javap -classpath "$java_stub_jar" -s 'System.Text.StringBuilder$ChunkEnumerator' | \
  grep -q 'descriptor: ()LSystem/Text/StringBuilder\$ChunkEnumerator;'
# Supertype closure: `Encoding : ICloneable` only resolves because every reachable supertype is emitted,
# no longer because a human happened to list ICloneable in a hard-coded array.
jar tf "$java_stub_jar" | grep -Fqx 'System/IComparable1.class'
jar tf "$java_stub_jar" | grep -Fqx 'System/Runtime/Serialization/ISerializable.class'
# Class-level generics, resolved from the owner's own type-parameter context.
javap -classpath "$java_stub_jar" System.IComparable1 | grep -q '^public interface System.IComparable1<T> {'
javap -classpath "$java_stub_jar" -v System.IComparable1 | grep -q 'Signature:.*<T:Ljava/lang/Object;>Ljava/lang/Object;'
javap -classpath "$java_stub_jar" System.Text.Rune | \
  grep -q 'implements .*System.IComparable1<System.Text.Rune>'
# Field signatures: without one this reads as a raw `System.ReadOnlySpan`.
javap -classpath "$java_stub_jar" System.Text.Encoding | \
  grep -q 'public final System.ReadOnlySpan<kotlin.UByte> Preamble;'
# A JVM generic signature admits only reference types as type arguments.
javap -classpath "$java_stub_jar" System.ISpanFormattable | grep -q 'kotlin.clr.Span<java.lang.Character>'
# CLR static abstract interface members must not become an unloadable `static abstract` method.
javap -classpath "$java_stub_jar" -v System.IUtf8SpanParsable | \
  grep -A3 'public static .* Parse(' | grep -q 'ACC_PUBLIC, ACC_STATIC$'
javap -classpath "$java_stub_jar" -v System.IUtf8SpanParsable | grep -q 'stack=1, locals=2, args_size=2'
# Nested binary names are declared, not just spelled, so the editor shows `StringBuilder.ChunkEnumerator`.
javap -classpath "$java_stub_jar" -v System.Text.StringBuilder | \
  grep -q 'ChunkEnumerator=class System/Text/StringBuilder\$ChunkEnumerator of class System/Text/StringBuilder'
# A CLR enum has to arrive as a real JVM enum: without the `Enum<Self>` signature Kotlin sees a plain
# class extending a raw java.lang.Enum and stops treating the constants as enum entries.
javap -classpath "$java_stub_jar" -v System.DayOfWeek | grep -q 'ACC_PUBLIC, ACC_FINAL, ACC_SUPER, ACC_ENUM'
javap -classpath "$java_stub_jar" -v System.DayOfWeek | grep -q 'Signature:.*Ljava/lang/Enum<LSystem/DayOfWeek;>;'
javap -classpath "$java_stub_jar" -v System.DayOfWeek | grep -q 'ACC_PUBLIC, ACC_STATIC, ACC_FINAL, ACC_ENUM'
javap -classpath "$java_stub_jar" System.DayOfWeek | grep -q 'public static final System.DayOfWeek Monday;'
# No projected class may extend itself; a short-name supertype must resolve through the outer type.
javap -classpath "$java_stub_jar" System.Text.UTF8Encoding | grep -qv 'extends System.Text.UTF8Encoding'
# Each projected class carries the Kotlin declaration it came from, so the editor reads the CLR API
# rather than a Java-shaped approximation of it.
javap -classpath "$java_stub_jar" -v System.Text.StringBuilder | grep -q 'kotlin.Metadata('
javap -classpath "$java_stub_jar" -v System.Text.StringBuilder | grep -q 'k=1'
report="$repo_dir/tests/Smoke/.dotkt/lsp/projection-report.md"
test -f "$report"
rg -q '^- klib\.libraries: ' "$report"
# A CLR attribute has constructor overloads that a Kotlin annotation class cannot express.
rg -q '^- annotation\.constructor-overload-dropped: ' "$report"
# The projection is declaration-for-declaration with the KLIBs. Constructors are the one exception,
# and its size has to equal the annotation overloads that were reported as unrepresentable.
node -e '
  const fs = require("fs")
  const report = fs.readFileSync(process.argv[1], "utf8")
  const rows = [...report.matchAll(/^\| ([a-z -]+) \| (\d+) \| (\d+) \| ([+-]?\d+) \|$/gm)]
  if (rows.length < 7) { console.error("declaration accounting table is missing"); process.exit(1) }
  const overloads = Number(/^- annotation\.constructor-overload-dropped: (\d+)$/m.exec(report)?.[1] ?? -1)
  let failed = false
  for (const [, kind, , , delta] of rows) {
    const allowed = kind.trim() === "constructor" ? -overloads : 0
    if (Number(delta) !== allowed) {
      console.error(`${kind.trim()}: delta ${delta}, expected ${allowed}`)
      failed = true
    }
  }
  if (failed) process.exit(1)
' "$report"
# Platform types only survive through metadata; the KLIB records tens of thousands of them.
rg -q '^- metadata\.type\.flexible: [1-9]' "$report"
rg -q '^- metadata\.class: [1-9]' "$report"
# Receiver lambdas need a type-use annotation, not merely Function1<T, Unit>. Count both sides,
# then let Kotlin itself type-check this/implicit receivers and ordinary it against the emitted JAR.
rg -q '^- klib\.typeref\.extension-function: [1-9]' "$report"
rg -q '^- metadata\.type\.extension-function: [1-9]' "$report"
rg -q '^- klib\.typeref\.suspend-function: [1-9]' "$report"
rg -q '^- metadata\.type\.suspend-function: [1-9]' "$report"
dotkt_version="$(sed -n 's:.*<DotKtVersion>\(.*\)</DotKtVersion>.*:\1:p' "$repo_dir/Directory.Build.props")"
compiler_lib="$repo_dir/artifacts/.packages/dotkt.toolchain/$dotkt_version/tools/kotc/lib"
# Use exactly the shipped analysis classpath: the full JVM stdlib would mask missing ABI classes.
java -cp "$compiler_lib/*" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler \
  -no-stdlib -classpath "$tool_out/kotlin-builtins.jar:$java_stub_jar" \
  -XXLanguage:+CompanionBlocksAndExtensions \
  -d "$repo_dir/artifacts/verify-receiver-consumer" \
  "$repo_dir/tests/Smoke/src/ReceiverLambda.kt" "$repo_dir/tests/Smoke/src/SuspendLambda.kt"
# Overloads that erase to one JVM descriptor are kept by mangling the JVM name, not by dropping them.
rg -q '^- member\.mangled\.method: [1-9]' "$report"
# Top-level CLR declarations have no owning class, so they only exist for the analyzer as file facades
# named by the module mapping. Both halves have to be in the archive.
jar tf "$java_stub_jar" | grep -Fqx 'META-INF/dotkt_clr.kotlin_module'
rg -q '^- java\.class\.facade: [1-9]' "$report"
rg -q '^- java\.facade\.callable: [1-9]' "$report"
javap -classpath "$java_stub_jar" -v System.Linq.System_Linq_ParallelKt | grep -q 'k=2'
javap -classpath "$java_stub_jar" System.Linq.System_Linq_ParallelKt | grep -q 'public static native .* AsParallel('
# A C# extension method is callable as a static of its class and as an extension; both forms are
# declared and both are emitted, in different scopes. Counted so the shape stays visible.
rg -q '^- extension-method\.both-call-forms: [1-9]' "$report"
if rg -q '^## upstream gaps$' "$report"; then
  echo "DotKt 0.9.14 left an upstream KLIB gap:" >&2
  sed -n '/^## upstream gaps$/,/^## /p' "$report" >&2
  exit 1
fi
if rg -q '^- member\.dropped\.' "$report"; then
  echo "The projection dropped members that the metadata representation can carry:" >&2
  rg '^- member\.dropped\.' "$report" >&2
  exit 1
fi
rg -q 'kotlin-builtins\.jar' "$repo_dir/tests/Smoke/workspace.json"
rg -q 'dotkt-klib-java-stubs\.jar' "$repo_dir/tests/Smoke/workspace.json"
if rg -q 'kotlin-stdlib\.jar|dotkt-lsp-overlay\.jar' "$repo_dir/tests/Smoke/workspace.json"; then
  echo "The workspace still references the JVM stdlib approximation" >&2
  exit 1
fi
# kotlin.clr now comes from DotKt's own standard library rather than a hand-written overlay.
javap -classpath "$java_stub_jar" kotlin.clr.ClrRef > /dev/null
javap -classpath "$java_stub_jar" kotlin.clr.EventSubscription > /dev/null
# The Kotlin compiler stores a declaration's types in a per-class table and refers to them by index.
# Missing that indirection does not fail: every type silently becomes Any, so it has to be asserted.
javap -p -classpath "$java_stub_jar" kotlin.text.Regex | grep -q 'public final java.lang.String pattern;'
javap -p -classpath "$java_stub_jar" kotlin.clr.ClrRef | \
  grep -q 'public native T getValue(java.lang.Object, kotlin.reflect.KProperty<?>);'
# The compiler names type variables where dll2klib numbers them; both have to resolve.
javap -p -classpath "$java_stub_jar" kotlin.clr.kotlin_stdlib_clr_frontendKt | \
  grep -q 'public static native <T> kotlin.clr.ClrRef<T> byref(T);'
# An extension property has no backing field and must arrive as accessors.
javap -p -classpath "$java_stub_jar" kotlin.collections.kotlin_stdlib_clr_frontendKt | \
  grep -q 'getIndices('
rg -q '^- member\.extension-property: [1-9]' "$report"
# A JVM-only stdlib API must not be reachable at all.
if jar tf "$java_stub_jar" | grep -qE '^kotlin/concurrent/(Threads|Locks|Timers)Kt\.class$'; then
  echo "A JVM-only stdlib facade reached the projection" >&2
  exit 1
fi
# Parse JSON so this assertion is independent of System.Text.Json's escaping choices.
node -e '
  const workspace = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
  const args = workspace.kotlinSettings?.[0]?.additionalArguments ?? "";
  if (!args.split(/\s+/).includes("-XXLanguage:+CompanionBlocksAndExtensions")) process.exit(1);
' "$repo_dir/tests/Smoke/workspace.json"

# The plain-Java fallback is the escape hatch if a metadata bug ever ships, so it has to keep working.
fallback_out="$repo_dir/artifacts/verify-no-metadata"
rm -rf "$fallback_out"
mkdir -p "$fallback_out/project"
dotnet "$repo_dir/src/DotKtx.Experimental.LspSupport/bin/Release/net10.0/DotKtx.Experimental.LspSupport.dll" \
  generate --project "$fallback_out/project" --name fallback --output "$fallback_out/model" \
  --sources "$repo_dir/tests/Smoke/obj/dotkt-lsp-sources.rsp" \
  --klibs "$repo_dir/tests/Smoke/obj/dotkt-lsp-klibs.rsp" \
  --java-projection 'all;no-metadata' > /dev/null
fallback_jar="$fallback_out/model/libraries/dotkt-klib-java-stubs.jar"
rg --no-ignore -q '@kotlin\.ExtensionFunctionType.*receiverfixture\.TextBlock' "$fallback_out/model/stubs"
rg --no-ignore -q 'block: suspend suspendfixture.CoroutineScope\.\(\) -> T' "$fallback_out/model/stubs"
rg --no-ignore -q 'block: \(suspend suspendfixture.CoroutineScope\.\(\) -> kotlin.Unit\)\?' "$fallback_out/model/stubs"
javap -classpath "$fallback_jar" -v System.Text.StringBuilder | grep -qv 'kotlin.Metadata('
javap -classpath "$fallback_jar" System.Text.StringBuilder | grep -q 'public native java.lang.String ToString();'
# Without metadata there is nowhere to record a Kotlin name, so the erasure collisions come back.
rg -q '^- member\.dropped\.erasure-clash\.method: [1-9]' "$fallback_out/model/projection-report.md"
rm -rf "$fallback_out"

echo "DotKtx.Experimental.LspSupport: package and MSBuild generation verified"
