# kotlin.Metadata reference tooling

Development-only tooling. It is not part of the NuGet package and nothing in `src/` depends on it.

`KotlinMetadata.cs` writes the `kotlin.Metadata` annotation that makes a projected classfile *be* the
CLR declaration instead of an approximation of it. Getting one field number or one encoding detail
wrong there does not fail loudly - the analyzer silently reads a different declaration, or drops the
class. So none of that format was written from memory: every part of it was derived from output of the
real Kotlin compiler shipped in `DotKt.Toolchain`, and these are the tools that derived it.

Re-run them whenever the Kotlin version behind `DotKtKotlinVersion` changes, and when the new kotc
lands so the companion projection can be re-measured rather than assumed.

## Setup

```bash
repo=$(git rev-parse --show-toplevel)
LIB="$repo/artifacts/.packages/dotkt.toolchain/0.9.14/tools/kotc/lib"
CP="$LIB/kotlin-compiler-embeddable-2.4.21.jar:$LIB/kotlin-stdlib-2.4.21.jar:."
cd "$repo/tools/kotlin-metadata-reference"
mkdir -p out   # gitignored scratch for compiled classes and dumps
javac -cp "$CP" -d . MetaTool.java MetaDump.java MapTool.java
```

## What each tool answers

### `check-bitencoding.mjs` - is the `d1` encoding right?

`MetaTool vectors` calls the compiler's own `BitEncoding` on pseudo-random payloads (fixed seed) and
writes input/output pairs. `check-bitencoding.mjs` re-derives them from the rule the C# encoder
implements: a NUL marker char, one char per payload byte, chunks of 32767 chars.

```bash
java -cp "$CP" MetaTool vectors 60 out/vectors.tsv
node check-bitencoding.mjs out/vectors.tsv   # the committed vectors.tsv must match
```

`MapTool` prints the byte-to-char mapping for all 256 values and the chunk boundaries at payload sizes
around 64 KiB, which is where the rule above came from.

### `MetaDump` - what is actually in a classfile's annotation?

Reads a `.class` file with no dependencies beyond the compiler jar, prints `k`, `mv` and `xi`, and
writes the decoded `d1` payload plus the `d2` string table.

```bash
java -cp "$CP" MetaDump <path-to.class> out/dump
# -> out/dump.bin (protobuf payload), out/dump.d2.txt (string table)
```

It works on compiler output and on our generated classfiles alike, which is how the writer is checked
against the thing it imitates.

### `dump-proto.mjs` - what does that payload say?

A schema-free protobuf wire dumper: it prints every `(field number, wire type, value)` and recurses
into anything that re-parses as a message. This is what makes the field numbers evidence rather than
recollection - `JvmMetadata.proto` was written by reading these dumps.

```bash
node dump-proto.mjs out/dump.bin out/dump.d2.txt
```

The payload is a length-delimited `StringTableTypes` followed by the bare `Class` or `Package`
message; the dumper splits them for you and annotates varints with the string they index.

## Probes

Compile these with the toolchain's Kotlin compiler and dump the result to see how the compiler itself
represents a construct.

```bash
java -cp "$LIB/*" org.jetbrains.kotlin.cli.jvm.K2JVMCompiler \
  -no-stdlib -classpath "$LIB/kotlin-stdlib-2.4.21.jar" -d out probes/Rich.kt
```

- `Rich.kt` - generics with bounds, an interface with a default method, default and vararg arguments,
  an extension function, a top-level property, `inline`/`reified`, `suspend`, an enum, a companion
  object with `const` and `@JvmStatic`. Compile it to see every extension number the writer needs.
- `PlainCompanion.kt` - an ordinary `companion object`, for comparison.
- `CompanionBlock.kt` - `companion { }` and `companion fun T.x()`, which need
  `-XXLanguage:+CompanionBlocksAndExtensions`. Compiling it shows that companion-block members become
  plain JVM statics on the owner with no `$Companion` class and no annotation of their own, that a
  companion extension lands on the file facade instead of the type, and that the flag sets the
  pre-release bit in `xi` (48 becomes 50).
