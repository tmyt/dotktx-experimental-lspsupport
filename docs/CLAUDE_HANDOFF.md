# DotKtx.Experimental.LspSupport 引継ぎ

対象: DotKt 0.9.13-beta2 / Kotlin 2.4.10
最終更新: 2026-10-12 (第4版、DotKt 0.9.13-beta2 で再ベースライン)

この文書の現在値は DotKt 0.9.13-beta2 で生成した
`tests/Smoke/.dotkt/lsp/projection-report.md` を authority としている。0.9.9 以前の数値は比較用の
履歴であり、現状値として参照しないこと。

## 1. 現在の状態

**KLIB を単一の JAR へ投影し、JetBrains Kotlin LSP の補完・hover・定義ジャンプが動作する。**
Kotlin source stub は存在しない（`source.files: 0`）。

生成 JAR は解析専用である。実行用・DotKt compiler 入力・配布用 CLR assembly の代替にしてはならない。

### 1.1 表現方式

各 CLR 宣言を最小 JVM classfile へ投影し、**Kotlin コンパイル済みライブラリと同じ `kotlin.Metadata`
annotation を載せる**。metadata を持つ class は analyzer が metadata だけを読むので、classfile 側の
Java メンバは参照されない。これにより plain Java classfile では表現できないものが通る。

| 構文 | 表現 |
|---|---|
| nullability (`String` / `String?` / `String!`) | metadata の `Type.nullable` / `flexible_upper_bound` |
| declaration-site variance (`in T` / `out T`) | metadata の `TypeParameter.variance` |
| extension receiver | metadata の `Function.receiver_type`、JVM 側は第1引数 |
| erasure が衝突する overload | Kotlin 名は metadata、JVM 名だけ mangle |
| top-level 関数・プロパティ | file facade class + `META-INF/dotkt_clr.kotlin_module` |
| CLR enum | JVM enum (`ACC_ENUM` + `Signature: Ljava/lang/Enum<Self>;`) |
| CLR attribute | JVM annotation interface + metadata の constructor |
| CLR static | owner 直下の `ACC_STATIC` メンバ（companion block と同形） |
| extension property | receiver を引数に取る accessor メソッド + metadata の `receiver_type` |
| context parameter | 先頭の引数 + metadata の `context_parameter` |
| Kotlin 標準ライブラリ | DotKt 自身の CLR stdlib KLIB を同じ経路で投影 |

### 1.2 データフロー

```text
DotKt MSBuild (public contract)
  DotKtResolveKlibReferences
        ├─ @(DotKtResolvedKlibReference) ─────────┐
        └─ $(DotKtStdlib) (CLR stdlib KLIB) ──────┤
  @(KotlinCompile) ───────────────────────────────┤
                                                  v
                        KlibReader (KLIB を1回だけ読む)
                                  │
                                  v
                            KlibWorld  ── 全 KLIB 横断の型レジストリ
                                  │
                                  v
                        ProjectionPolicy (既定: all + metadata)
                                  │
                                  v
                  classfile + kotlin.Metadata + .kotlin_module
                                  │
shipped kotlin-builtins.jar ──────┤  (Any / Int / List ... のみ)
                                  v
                           workspace.json
                                  │
                                  v
                      JetBrains Kotlin LSP
```

生成物:

```text
workspace.json
.dotkt/lsp/
  ├─ .stamp
  ├─ README.md
  ├─ projection-report.md      <- 近似・欠落・会計の authority
  └─ libraries/dotkt-klib-java-stubs.jar
```

`.dotkt/**/*.kt` は `@(KotlinCompile)` から除外される。生成物が CLR assembly へ混入しないことは
この package の最重要 boundary である。

## 2. DotKt 0.9.13-beta2 への追従

0.9.13-beta2 の MSBuild 統合契約は `DotKt.README.md` に明記されている。

- target `DotKtResolveKlibReferences` の完了後、
- `@(DotKtResolvedKlibReference)` に全参照 KLIB（絶対 KLIB パス、`SourceAssembly` / TFM / RID メタ付き）、
- `$(DotKtStdlib)` に評価時から CLR frontend stdlib KLIB のパス、
- `$(DotKtKotlinVersion)` に埋め込み Kotlin バージョン。
- `_DotKt` 接頭辞と `$(DotKtToolchainDir)` 配下は private。

`packaging/build/DotKtx.Experimental.LspSupport.targets` はこの公開契約だけを使っている。
private 名（`@(_DotKtKlibReference)`、`DotKtGenerateReferenceKlibs` 等）への依存は無い。

`$(DotKtStdlib)` の `kotlin-stdlib-clr-frontend.klib` も投影している。
これにより JVM `kotlin-stdlib.jar` 近似と手書き overlay を**廃止した**。`kotlin.clr` は DotKt 自身の
宣言から来るので、手動メンテは不要になった。

JVM 側から持ってきているのは `*.kotlin_builtins` リソースだけである。`kotlin.Any` / `Int` /
`collections.List` などの中核 classifier は classfile ではなくこのリソースで供給され、JVM が
`java.lang.*` と primitive へ写像する。プラットフォーム中立なので JVM 版をそのまま使い、
同名の classfile は出さない（除外集合はリソースを実際に読んで決めるので、手書きリストが腐ることはない）。

## 3. 主要ファイル

MSBuild:

- `packaging/build/DotKtx.Experimental.LspSupport.props`
  — enable flag、出力 root、`DotKtxExperimentalLspJavaProjection` policy property。
- `packaging/build/DotKtx.Experimental.LspSupport.targets`
  — 公開契約から入力を集め、response file 化し、generator を `KotlinCompile` 直前に incremental 実行。
  policy もファイル化してあるので property を変えるだけで incremental が正しく無効化される。

generator:

- `KlibModel.cs` — immutable 中間表現。`KlibWorld` が全 KLIB 横断レジストリと短名解決を持つ。
- `KlibReader.cs` — KLIB ZIP + protobuf を1回だけ読む。`NameResolver` もここ。
- `KlibMetadata.proto` — KLIB `.knm` の読み取り schema。
- `JvmMetadata.proto` — JVM `kotlin.Metadata` の書き出し schema と `.kotlin_module`。
  **全フィールド番号は実際の Kotlin コンパイラ出力から読み取ったもの**（7章）。
- `KotlinMetadata.cs` — `BitEncoding`、文字列表、metadata writer、module mapping writer。
- `KlibJavaStubProjector.cs` — 投影対象選択、facade 生成、JAR 作成、`JavaEmitContext`（名前解決・
  descriptor・generic signature・erasure・Kotlin builtin 対応）。
- `JavaClassFile.cs` — classfile writer、`ConstantPool`、`BigEndianWriter`。
- `ProjectionPolicy.cs` — 投影対象の policy。既定は `all` + `metadata`。
- `ProjectionReport.cs` — 会計・counter・distribution・timing・upstream gap・representation limit。
- `KlibSourceProjector.cs` — `no-metadata` fallback 専用。既定経路では 0 file。
- `WorkspaceWriter.cs` — JAR・report・`workspace.json` を atomic 更新。
（手書き overlay `src/DotKtx.Experimental.LspSupport.Overlay/` は 0.9.9 対応で削除した。
`kotlin.clr` は CLR stdlib KLIB から来る。）

検証:

- `tests/Smoke/src/Program.kt`, `tests/Smoke/src/JavaProjection.kt` — 実際に DotKt でコンパイルされる smoke。
- `tests/Smoke/verify-lsp.mjs` — 実 LSP を stdio 起動する verifier（assertion 10 本 + 負荷付き時間計測）。
- `verify.sh` — pack、isolated restore、DotKt build、JAR 構造検査、会計検査、fallback 経路検査。
- `tools/kotlin-metadata-reference/` — metadata 形式を実物から導出・検証する開発用ツール（7章）。

## 4. DotKt 0.9.13-beta2 実測値

Smoke project、参照 KLIB 167 本。

### 4.1 宣言会計（1:1 であることの検査）

参照 KLIB 167 本 + CLR stdlib KLIB 1 本。

| 宣言 | KLIB | JAR | 差 |
|---|---:|---:|---:|
| class | 4405 | 4405 | 0 |
| function | 39531 | 39531 | 0 |
| property | 11637 | 11637 | 0 |
| constructor | 4470 | 4291 | **-179** |
| enum entry | 5232 | 5232 | 0 |
| top-level function | 6760 | 6760 | 0 |
| top-level property | 112 | 112 | 0 |

builtin classifier 134 個は `input.builtin-class` に分離してある（classfile ではなく
`*.kotlin_builtins` から供給されるため）。

constructor の -179 は CLR attribute の overload（6.2）。**この表は verify.sh が検査する** —
constructor 以外の差が 0 でなければ失敗し、constructor の差も報告済み overload 数と一致しなければ失敗する。

注意: 入力側は reader が構築したモデルであって生 protobuf ではない。reader が読み取り時に潰すもの
（0.9.8 にあった重複宣言など）はこの表からは見えない。

### 4.2 出力

- JAR: 4,473 class（うち file facade 68）+ `META-INF/dotkt_clr.kotlin_module`
- Kotlin source: **0 file**
- metadata: 4,473 class
- 生成時間: KLIB read 0.94 s / Java projection 1.28 s / 合計 2.26 s
- NuGet package: 約 1.1 MB（PDB を含む）。JVM stdlib 全体ではなく約 17 KB の builtins JAR だけを同梱。

### 4.3 実 LSP

```text
cold index + assertion 10 本完走: 17.1s  (開始時 load 0.8、終了時 1.3)
```

verifier は負荷平均を併記する。**load が ~1.0 を超えた測定値は比較に使えない**。上記は機能検証時の
参考値であり、終了時に 1.3 まで上がったため厳密な性能比較値ではない。

使用 LSP は JetBrains 公式 standalone `v263.6379.0`（VS Code extension 0.0.13 と同世代）。

assertion: `kotlin.clr` 補完 / import 補完 / member 補完 / static 補完 / 継承補完 / **CLR enum 補完** /
generic hover / interface definition / definition navigation / metadata hover。

## 5. 0.9.8 → 0.9.9 で解消した upstream gap

この章は移行履歴。5.2 の後続修正は 0.9.13-beta2 に取り込まれており、現在の report に
`## upstream gaps` セクションは無い。

第2版で「upstream 依存で手が出せない」と分類した 5 件のうち **4 件が 0.9.9 で解消**した。

| 0.9.8 での問題 | 0.9.8 の件数 | 0.9.9 |
|---|---:|---|
| nested 型参照に親リンクが無い | 39 | **解消**（counter 自体が消滅） |
| generic class の companion が外側の型引数を参照 | 651 | **解消**（companion 自体が無くなった） |
| nested generic の arity 未 mangle による同名衝突 | 4 | **解消** |
| supertype が自分自身に解決される | 2 | **解消** |
| 拡張メソッドの幻パッケージ複製 | 725 | 0.9.9 では残存、**dll2klib の後続修正で解消**（5.2） |

副次的な変化:

- `$Companion` class が JAR から消えた（0.9.8 では 923 個）。CLR static は owner 直下の
  `public static final` になり、これは 0.9.9 kotc の `companion { }` が生成する形と同一。
  JAR の class 数は 5,004 → 4,085 に減った。
- erasure 衝突による JVM 名 mangle は 14,966 → 7,780 に減った（重複が消えたぶん）。

### 5.2 拡張メソッドの複製（dll2klib 後続修正で解消）

リリース版 0.9.9 の dll2klib は、静的クラスの拡張メソッドを**そのクラス名のパッケージにも複製**する。
`System.Linq.ParallelEnumerable` のような、CLR には存在しないパッケージが 76 個でき、
top-level 関数が 1,817 個重複していた。

`/tmp/kotlin-net-extension-dedupe`（tmyt/dotkt の後続コミット、dll2klib のみ差分。
stdlib KLIB は md5 一致で無変更）で確認した結果:

| | リリース版 0.9.9 | 修正版 dll2klib |
|---|---:|---:|
| `package.named-after-a-type` | 76 | **0** |
| top-level 関数 | 8,461 | 6,644 |
| file facade | 144 | 68 |
| JAR class 数 | 4,503 | 4,427 |
| upstream gap | 1 種 | **0 種** |

検出は「パッケージ名自体が型として存在するか」で判定しているので、修正版では自動的に沈黙する。

なお残る「同じ拡張メソッドが静的クラスのメンバとしても top-level 拡張としても見える」形は
**欠陥ではない**。C# でも `ParallelEnumerable.AsParallel(x)` と `x.AsParallel()` の両方が有効で、
両者は別スコープなので単一の補完候補一覧に重複は出ない。`extension-method.both-call-forms`
として計上しているのは形を可視化するためであって、損失ではない。

### 5.1 nullability が精密になった（重要）

`klib.typeref.flexible` が 83,260 → **5,218**（全 233,118 type ref 中）に激減した。
これは退行ではなく**上流の精度向上**である。

コンパイラで確認済み:

```kotlin
fun inner(e: System.Exception): System.Exception = e.InnerException
// error: return type mismatch: expected 'Exception', actual 'Exception?'
fun message(e: System.Exception): String = e.Message   // OK（非 null）
```

0.9.9 は CLR の nullable metadata を KLIB に反映しており、注釈のないアセンブリ由来のメンバだけが
platform type として残る。**metadata 経路だからこの3状態を区別できている** — plain Java classfile
なら全部 platform type に潰れて区別が失われる。

## 6. 現在残っている損失

report が自分で 2 種類に分類する。未分類のものは無い。

### 6.1 upstream gap

**0 件。** 0.9.9 で残っていた幻パッケージ複製（5.2）は 0.9.13-beta2 で解消済み。

### 6.2 representation limit（Kotlin 側の原理的制約、誰にも直せない）1 件

- **CLR attribute の constructor overload（179 件）**。Kotlin の annotation class は constructor が
  1 つだけなので、attribute のメンバをその 1 つの引数リストにしている。
  `@System.ObsoleteAttribute(Message = "...")` の名前付き引数形式で使える。
  なお 0.9.8 以前の source stub 表現では attribute の constructor が 1 つも出ておらず、
  そもそも使えなかったので、これは退行ではなく改善である。

### 6.3 損失ではないが計上しているもの

- `member.mangled.method: 7780` / `member.mangled.field: 69` — JVM 名のみ変更。Kotlin 名は metadata が
  保持しているので Kotlin から見れば同一。
- `java.constructor.synthetic: 2329` — 構造妥当性のために補った no-arg constructor（`ACC_SYNTHETIC`）。
- `member.static-abstract: 86` — CLR の static abstract interface member。最小 Code body を与えている。

## 7. metadata 形式の検証ツール

`tools/kotlin-metadata-reference/`（開発専用、NuGet には入らない）。

`kotlin.Metadata` は 1 ビット間違えても LSP が黙って誤読するため、形式は記憶ではなく**実物から導出**した。

- `MetaTool.java` / `check-bitencoding.mjs` / `vectors.tsv` — `d1` のエンコードを compiler の
  `BitEncoding` を参照実装として 60 本のベクタで照合。
- `MapTool.java` — byte→char 写像と 32767 char のチャンク境界の根拠。
- `MetaDump.java` — 任意の `.class` から `k`/`mv`/`xi` と `d1`/`d2` を取り出す。自作物にも使える。
- `dump-proto.mjs` — スキーマ非依存 protobuf wire dumper。`JvmMetadata.proto` の全フィールド番号は
  この出力から読み取った。
- `probes/*.kt` — compiler 自身の表現を見る probe。`CompanionBlock.kt` は
  `-XXLanguage:+CompanionBlocksAndExtensions` の検証用。

`DotKtKotlinVersion` が変わったら再検証すること。README に手順がある。

### 7.1 実装時に潰した罠（再発しやすい）

- classfile の `CONSTANT_Utf8` は **modified UTF-8**。NUL は `0xC0 0x80`。`d1` は先頭がマーカー NUL で
  protobuf payload にも `0x00` が頻出するため、標準 UTF-8 エンコーダでは不正な classfile になる。
- `var` プロパティには `setter_value_parameter` が必須。欠けると **class 全体の読み込みが中断**する。
- `getter_flags` に 0 を明示すると getter が internal 扱いになり**プロパティが消える**。未設定にすること。
- JVM generic signature の型引数は参照型のみ。`Span<Char>` は `Ljava/lang/Character;` に box する。
- `.kotlin_module` の `class_name` は binary name ではなく**単純名**。
- enum には `Signature: Ljava/lang/Enum<Self>;` が必要。無いと Kotlin が enum と見なさない。
- **Kotlin コンパイラが作った KLIB は型を `TypeTable` に入れ `*_type_id` で参照する**（dll2klib は
  インライン）。これを読まないと例外も出ず、stdlib の型が**全部 `Any` に潰れる**。
  `Class.type_table` / `Package.type_table` はどちらも field 30、`Class.supertype_id` は 2、
  `Function.return_type_id` は 7、`Property.return_type_id` は 9、`ValueParameter.type_id` は 5。
  `TypeTable.first_nullable` 以降の要素は nullable 版として扱う。
- 型変数を dll2klib は id（`Type.type_parameter`）、Kotlin コンパイラは**名前**
  （`Type.type_parameter_name`、field 9）で書く。両方読まないと generic が `Any` になり型推論が壊れる。
- extension property は backing field を持てない。receiver を第1引数に取る accessor
  （`getX(receiver)` / `setX(receiver, value)`）として出し、metadata は
  `JvmPropertySignature.getter` / `setter` で紐付ける。
- 会計表は**存在**しか見ない。型が全部 `Any` に潰れても件数は 1:1 のままなので、
  `klib.typeref.*` のような形の計測と、`javap` による具体的な型の assertion が要る。

## 8. 残っている作業

**upstream 非依存の pending は無い。** JVM stdlib 近似の廃止は 0.9.9 対応で完了した
（`kotlin-stdlib.jar` と手書き overlay を削除し、CLR stdlib KLIB を投影）。

残るのは:

- CLR attribute の constructor overload（6.2）。Kotlin の言語仕様上、誰にも直せない。

拡張メソッドの幻パッケージ複製は dll2klib の後続修正で解消され、その修正は
0.9.13-beta2 に含まれている（5.2）。現在の upstream gap は 0。

将来の改善候補（欠陥ではない）:

- `*.kotlin_builtins` も CLR stdlib KLIB から生成する。現在は JVM 版をそのまま使っている。
  中核 classifier はプラットフォーム中立なので実害は確認されていないが、完全に DotKt 由来に
  するなら 1 パッケージあたり複数の `.knm` fragment を 1 つへマージする必要がある
  （`kotlin` は 86、`kotlin.collections` は 59 fragment）。文字列表と修飾名表の索引振り直しを伴う。
- 生成 JAR の名前が `dotkt-klib-java-stubs.jar` のままで、実態（metadata 付き単一 JAR）と合っていない。
  改名は verify.sh / README / LSP verifier の assertion に波及するので未実施。
- 複数 project / 実プロジェクト規模での cold / warm 計測。現在の実測は smoke project のみ。

## 9. 検証方法

```bash
bash verify.sh                      # pack, restore, DotKt build, JAR 構造・会計・fallback 検査
node tests/Smoke/verify-lsp.mjs     # 実 JetBrains Kotlin LSP
cat tests/Smoke/.dotkt/lsp/projection-report.md
```

policy を変えた実験:

```bash
dotnet build tests/Smoke/Smoke.ktproj -c Release \
  '-p:DotKtxExperimentalLspJavaProjection=all%3Bno-metadata'
```

（コマンドラインでは `;` を `%3B` にエスケープすること。MSBuild が `-p:` を複数 property に分割する。）

### 9.1 index の置き場所に関する注意

`/tmp` は tmpfs で、**LSP の RocksDB index が実際に埋め尽くして LSP が落ちた実績がある**。
cold index 1 回で 400 MiB を超える。verifier は既定で `tests/Smoke/.dotkt/lsp-verifier-<pid>` を使い
終了時に削除する。`KOTLIN_LSP_SYSTEM_PATH` を明示した場合は削除しないので、実ディスク側を指定し、
測定後に必ず消すこと。

## 10. 作業上の注意

- 生成 JAR を実行可能にしようとしない。KLIB に body が無いので正しい runtime JAR は作れない。
- `workspace.json` がこの package 所有でない場合は上書きしない保護を維持する。
- 生成物を `KotlinCompile` に混ぜない。
- 解析 JAR の API は editor approximation であり、compiler diagnostics より優先しない。
- `Directory.Build.props` の `DotKtVersion` / `DotKtKotlinVersion` を変えたら metadata version、
  builtins 抽出元、7章の参照ベクトルを見直す。package version は対象 DotKt リリースに揃えている。
- nested `./kotlin-lsp` を外側 repo へ誤って追加しない。
- installed VS Code extension の JAR を直接変更した状態で結果を確定しない。

## 11. 引継ぎ直後に行う確認

```bash
git status -sb
dotnet build src/DotKtx.Experimental.LspSupport/DotKtx.Experimental.LspSupport.csproj -c Release -v:minimal
bash verify.sh
node tests/Smoke/verify-lsp.mjs
cat tests/Smoke/.dotkt/lsp/projection-report.md
```

report の `## upstream gaps` と `## representation limits`（0件の章は出力されない）が現在の損失の
全体である。それ以外に黙って落ちているものは無い（会計表が検査している）。
