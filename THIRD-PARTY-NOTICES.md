# Third-party notices

## Kotlin builtins

`DotKtx.Experimental.LspSupport` redistributes `kotlin-builtins.jar`, which contains
the `*.kotlin_builtins` metadata resources taken from the Kotlin standard library.
They describe Kotlin's core classifiers - `Any`, `Int`, `collections.List` and the
rest - which a Kotlin analyzer reads from resources rather than from classfiles.
No Kotlin implementation code is redistributed.

Kotlin is Copyright 2010-2026 JetBrains s.r.o. and Kotlin Programming Language
contributors and is licensed under the Apache License 2.0.

Source and license information: <https://github.com/JetBrains/kotlin>

## Kotlin metadata schemas

`KlibMetadata.proto` and `JvmMetadata.proto` are authoring subsets of Kotlin's
`core/metadata/src/metadata.proto`, `core/metadata.jvm/src/jvm_metadata.proto` and
module mapping formats, under the same license.

Everything else this package generates is produced at build time from the DotKt
toolchain's own KLIBs and contains no copied Kotlin code.
