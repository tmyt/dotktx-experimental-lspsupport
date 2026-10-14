#!/usr/bin/env node

import { spawn } from "node:child_process"
import { existsSync, readFileSync, readdirSync, rmSync } from "node:fs"
import { loadavg } from "node:os"
import { homedir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath, pathToFileURL } from "node:url"

const projectDir = resolve(dirname(fileURLToPath(import.meta.url)))
const sourcePath = join(projectDir, "src", "Program.kt")
const sourceUri = pathToFileURL(sourcePath).href
const projectionPath = join(projectDir, "src", "JavaProjection.kt")
const projectionUri = pathToFileURL(projectionPath).href
const receiverPath = join(projectDir, "src", "ReceiverLambda.kt")
const receiverUri = pathToFileURL(receiverPath).href
const receiverDocumentText = readFileSync(receiverPath, "utf8")
const suspendPath = join(projectDir, "src", "SuspendLambda.kt")
const suspendUri = pathToFileURL(suspendPath).href
const suspendDocumentText = readFileSync(suspendPath, "utf8")
const rootUri = pathToFileURL(projectDir).href
const documentText = readFileSync(sourcePath, "utf8")
// The projection report is the only place that records which representation the build produced, and
// the two representations are observably different in the editor.
const reportPath = join(projectDir, ".dotkt", "lsp", "projection-report.md")
const metadataEnabled = existsSync(reportPath) &&
    /^- metadata\.class: /m.test(readFileSync(reportPath, "utf8"))
const projectionDocumentText = readFileSync(projectionPath, "utf8")
const baseAnalysisDocumentText = "import kotlin.clr.\n" + documentText
const analysisDocumentText = baseAnalysisDocumentText
const ownsSystemPath = !process.env.KOTLIN_LSP_SYSTEM_PATH
const systemPath = process.env.KOTLIN_LSP_SYSTEM_PATH ??
    join(projectDir, ".dotkt", `lsp-verifier-${process.pid}`)
const analyzerIndexDir = join(systemPath, "index")

function findServer() {
    if (process.env.KOTLIN_LSP_SERVER) return process.env.KOTLIN_LSP_SERVER

    const extensionRoots = [
        join(homedir(), ".vscode-server", "extensions"),
        join(homedir(), ".vscode", "extensions"),
    ]
    const candidates = []
    for (const root of extensionRoots) {
        if (!existsSync(root)) continue
        for (const entry of readdirSync(root)) {
            if (!entry.startsWith("jetbrains.kotlin-server-")) continue
            const server = join(root, entry, "server", "bin", "intellij-server")
            if (existsSync(server)) candidates.push(server)
        }
    }
    candidates.sort()
    return candidates.at(-1)
}

const serverPath = findServer()
if (!serverPath) {
    throw new Error(
        "Kotlin by JetBrains is not installed. Install VS Code extension " +
        "JetBrains.kotlin-server or set KOTLIN_LSP_SERVER."
    )
}

const child = spawn(serverPath, [
    "--stdio",
    "--system-path", systemPath,
    "--log-level", "WARNING",
], {
    cwd: projectDir,
    stdio: ["pipe", "pipe", "pipe"],
})

child.stderr.setEncoding("utf8")
child.stderr.on("data", chunk => process.stderr.write(chunk))

let nextId = 1
const pending = new Map()
let incoming = Buffer.alloc(0)
let latestDiagnostics = []
let receiverDiagnostics = []

function send(message) {
    const json = JSON.stringify(message)
    const body = Buffer.from(json, "utf8")
    child.stdin.write(`Content-Length: ${body.length}\r\n\r\n`)
    child.stdin.write(body)
}

function notify(method, params = {}) {
    send({ jsonrpc: "2.0", method, params })
}

function request(method, params, timeoutMs = 180_000) {
    const id = nextId++
    send({ jsonrpc: "2.0", id, method, params })
    return new Promise((resolvePromise, rejectPromise) => {
        const timer = setTimeout(() => {
            pending.delete(id)
            rejectPromise(new Error(`${method} timed out after ${timeoutMs} ms`))
        }, timeoutMs)
        pending.set(id, {
            resolve: result => {
                clearTimeout(timer)
                resolvePromise(result)
            },
            reject: error => {
                clearTimeout(timer)
                rejectPromise(error)
            },
        })
    })
}

function reply(id, result) {
    send({ jsonrpc: "2.0", id, result })
}

function handle(message) {
    if (message.method === "textDocument/publishDiagnostics" && message.params?.uri === receiverUri) {
        receiverDiagnostics = message.params.diagnostics ?? []
        return
    }
    if (Object.hasOwn(message, "id") && !message.method) {
        const waiter = pending.get(message.id)
        if (!waiter) return
        pending.delete(message.id)
        if (message.error) waiter.reject(new Error(JSON.stringify(message.error)))
        else waiter.resolve(message.result)
        return
    }

    if (message.method === "textDocument/publishDiagnostics" && message.params?.uri === sourceUri) {
        latestDiagnostics = message.params.diagnostics ?? []
        return
    }

    if (Object.hasOwn(message, "id") && message.method) {
        switch (message.method) {
            case "workspace/configuration":
                reply(message.id, (message.params?.items ?? []).map(() => null))
                break
            case "workspace/workspaceFolders":
                reply(message.id, [{ uri: rootUri, name: "DotKtx.Experimental.LspSupport.Smoke" }])
                break
            case "window/workDoneProgress/create":
            case "client/registerCapability":
            case "client/unregisterCapability":
            case "workspace/diagnostic/refresh":
            case "workspace/inlayHint/refresh":
            case "workspace/semanticTokens/refresh":
                reply(message.id, null)
                break
            case "window/showMessageRequest":
                reply(message.id, null)
                break
            default:
                reply(message.id, null)
                break
        }
    }
}

child.stdout.on("data", chunk => {
    incoming = Buffer.concat([incoming, chunk])
    while (true) {
        const headerEnd = incoming.indexOf("\r\n\r\n")
        if (headerEnd < 0) return
        const header = incoming.subarray(0, headerEnd).toString("ascii")
        const match = /Content-Length:\s*(\d+)/i.exec(header)
        if (!match) throw new Error(`Invalid LSP header: ${header}`)
        const length = Number(match[1])
        const bodyStart = headerEnd + 4
        if (incoming.length < bodyStart + length) return
        const body = incoming.subarray(bodyStart, bodyStart + length).toString("utf8")
        incoming = incoming.subarray(bodyStart + length)
        handle(JSON.parse(body))
    }
})

let terminationRequested = false
const childExit = new Promise((resolvePromise, rejectPromise) => {
    child.once("error", rejectPromise)
    child.once("exit", (code, signal) => {
        if (code === 0 || (terminationRequested && (code === 143 || signal === "SIGTERM"))) resolvePromise()
        else rejectPromise(new Error(`Kotlin LSP exited: code=${code}, signal=${signal}`))
    })
})

function completionLabels(result) {
    const items = Array.isArray(result) ? result : result?.items ?? []
    return items.map(item => item.label)
}

function hoverText(result) {
    const contents = result?.contents
    if (typeof contents === "string") return contents
    if (Array.isArray(contents)) return contents.map(item =>
        typeof item === "string" ? item : item?.value ?? ""
    ).join("\n")
    return contents?.value ?? ""
}

async function errorDiagnostics(uri) {
    const report = await request("textDocument/diagnostic", {
        textDocument: { uri },
    })
    if (report?.kind !== "full" || !Array.isArray(report.items)) {
        throw new Error(`Expected a full diagnostic report for ${uri}: ${JSON.stringify(report)}`)
    }
    return report.items.filter(diagnostic => diagnostic.severity === 1)
}

function positionAfter(text, needle) {
    const index = text.indexOf(needle)
    if (index < 0) throw new Error(`Probe text was not found: ${needle}`)
    const end = index + needle.length
    const prefix = text.slice(0, end)
    const lines = prefix.split("\n")
    return { line: lines.length - 1, character: lines.at(-1).length }
}

async function waitForCompletion(position, expected, uri = sourceUri) {
    const deadline = Date.now() + 180_000
    let labels = []
    while (Date.now() < deadline) {
        try {
            const result = await request("textDocument/completion", {
                textDocument: { uri },
                position,
                context: { triggerKind: 1 },
            })
            labels = completionLabels(result)
            if (labels.includes(expected)) return labels
        } catch (error) {
            // Initial indexing can cancel editor requests. Retry until the workspace is ready.
            if (!String(error).includes("RequestCancelled") && !String(error).includes("-32800")) throw error
        }
        await new Promise(resolvePromise => setTimeout(resolvePromise, 2_000))
    }
    throw new Error(
        `Completion '${expected}' was not returned at ${JSON.stringify(position)}. ` +
        `Last labels: ${labels.slice(0, 30).join(", ")}`
    )
}

// Cold-index time is the number that decides whether this model is usable, and it is meaningless
// without the load it was taken under: on a busy machine the same run varies by a factor of two.
const startedAt = Date.now()
const loadAtStart = loadavg()[0]

try {
    const initializeResult = await request("initialize", {
        processId: process.pid,
        clientInfo: { name: "DotKtx.Experimental.LspSupport verifier", version: "1" },
        rootPath: projectDir,
        rootUri,
        workspaceFolders: [{ uri: rootUri, name: "DotKtx.Experimental.LspSupport.Smoke" }],
        initializationOptions: {
            defaultSdk: null,
            buildTools: { [rootUri]: "json" },
            indexDir: analyzerIndexDir,
        },
        capabilities: {
            workspace: {
                configuration: true,
                workspaceFolders: true,
            },
            window: { workDoneProgress: true },
            textDocument: {
                synchronization: { didSave: true },
                completion: {
                    completionItem: {
                        snippetSupport: false,
                        documentationFormat: ["markdown", "plaintext"],
                    },
                },
                definition: { linkSupport: true },
                hover: { contentFormat: ["markdown", "plaintext"] },
            },
        },
        workDoneToken: "dotkt-lsp-support-import",
    })
    if (!initializeResult?.capabilities?.completionProvider) {
        throw new Error("Kotlin LSP did not advertise completion support")
    }

    notify("initialized")
    notify("textDocument/didOpen", {
        textDocument: {
            uri: sourceUri,
            languageId: "kotlin",
            version: 1,
            text: analysisDocumentText,
        },
    })
    notify("textDocument/didOpen", {
        textDocument: {
            uri: projectionUri,
            languageId: "kotlin",
            version: 1,
            text: projectionDocumentText,
        },
    })
    notify("textDocument/didOpen", {
        textDocument: {
            uri: receiverUri,
            languageId: "kotlin",
            version: 1,
            text: receiverDocumentText,
        },
    })
    notify("textDocument/didOpen", {
        textDocument: {
            uri: suspendUri,
            languageId: "kotlin",
            version: 1,
            text: suspendDocumentText,
        },
    })

    const overlayLabels = await waitForCompletion(
        { line: 0, character: "import kotlin.clr.".length },
        "ClrRef",
    )
    for (const expected of [
        "ClrEvent", "ClrRef", "EventSubscription", "Span", "StackBuffer",
        "byref", "clrEvent", "stackBuffer",
    ]) {
        if (!overlayLabels.includes(expected)) {
            throw new Error(`DotKt overlay completion did not contain '${expected}'`)
        }
    }
    const importLabels = await waitForCompletion(
        { line: 1, character: "import System.Text.".length },
        "StringBuilder",
    )
    for (const expected of ["Encoding", "Rune", "StringBuilder", "UTF8Encoding"]) {
        if (!importLabels.includes(expected)) {
            throw new Error(`Java-projected import completion did not contain '${expected}'`)
        }
    }
    const memberLabels = await waitForCompletion(
        { line: 5, character: "    builder.".length },
        "Append",
    )
    for (const expected of ["Append", "Length", "ToString"]) {
        if (!memberLabels.includes(expected)) {
            throw new Error(`Member completion did not contain '${expected}'`)
        }
    }
    const definitions = await request("textDocument/definition", {
        textDocument: { uri: sourceUri },
        position: { line: 1, character: "import System.Text.Stri".length },
    })
    const locations = Array.isArray(definitions) ? definitions : definitions ? [definitions] : []
    const definitionUris = locations.map(location => location.targetUri ?? location.uri)
    if (!definitionUris.some(uri => uri?.includes("dotkt-klib-java-stubs.jar"))) {
        throw new Error(`Definition did not navigate to the CLR stub: ${definitionUris.join(", ")}`)
    }

    const hover = await request("textDocument/hover", {
        textDocument: { uri: sourceUri },
        position: { line: 7, character: "    return builder.ToStr".length },
    })
    const renderedHover = hoverText(hover)
    if (metadataEnabled) {
        // With kotlin.Metadata the declaration is the KLIB's own, so a CLR non-null String stays
        // non-null. The Java-only path physically cannot express that: an unannotated Java return is
        // flexible and always renders with the nullable upper bound.
        if (!/ToString\(\): String(?!\?)/.test(renderedHover)) {
            throw new Error(`ToString hover did not expose the declared Kotlin return type: ${renderedHover}`)
        }
    } else if (!renderedHover.includes("ToString(): String?")) {
        // Kotlin's internal type is flexible for an unannotated Java String return. The LSP hover renderer
        // prints its nullable upper bound rather than source-only `String!`; the non-null return statement in
        // Program.kt verifies that it was not imported as an ordinary nullable Kotlin type.
        throw new Error(`ToString hover did not expose the Java flexible-type upper bound: ${renderedHover}`)
    }
    const typeErrors = latestDiagnostics.filter(diagnostic =>
        /type mismatch|return type mismatch/i.test(diagnostic.message ?? "")
    )
    if (typeErrors.length !== 0) {
        throw new Error(`Java platform return was rejected as Kotlin String: ${JSON.stringify(typeErrors)}`)
    }

    const encodingLabels = await waitForCompletion(
        positionAfter(projectionDocumentText, "Encoding."),
        "UTF8",
        projectionUri,
    )
    for (const expected of ["GetEncoding", "UTF8"]) {
        if (!encodingLabels.includes(expected)) {
            throw new Error(`Encoding static completion did not contain '${expected}'`)
        }
    }
    const encodingDefinitions = await request("textDocument/definition", {
        textDocument: { uri: projectionUri },
        position: {
            line: 0,
            character: "import System.Text.Enco".length,
        },
    })
    const encodingLocations = Array.isArray(encodingDefinitions)
        ? encodingDefinitions
        : encodingDefinitions ? [encodingDefinitions] : []
    const encodingDefinitionUris = encodingLocations.map(location => location.targetUri ?? location.uri)
    if (!encodingDefinitionUris.some(uri => uri?.includes("dotkt-klib-java-stubs.jar"))) {
        throw new Error(`Encoding definition did not navigate to the Java stub: ${encodingDefinitionUris.join(", ")}`)
    }

    const runeLabels = await waitForCompletion(
        positionAfter(projectionDocumentText, "Rune(value)."),
        "Value",
        projectionUri,
    )
    for (const expected of ["ToString", "Value"]) {
        if (!runeLabels.includes(expected)) {
            throw new Error(`Rune member completion did not contain '${expected}'`)
        }
    }
    const utf8EncodingLabels = await waitForCompletion(
        positionAfter(projectionDocumentText, "UTF8Encoding()."),
        "WebName",
        projectionUri,
    )
    if (!utf8EncodingLabels.includes("WebName")) {
        throw new Error("UTF8Encoding completion did not inherit Encoding.WebName")
    }
    const enumLabels = await waitForCompletion(
        positionAfter(projectionDocumentText, "day == DayOfWeek."),
        "Saturday",
        projectionUri,
    )
    for (const expected of ["Saturday", "Sunday", "Monday"]) {
        if (!enumLabels.includes(expected)) {
            throw new Error(`CLR enum completion did not contain '${expected}'`)
        }
    }
    const genericHover = await request("textDocument/hover", {
        textDocument: { uri: projectionUri },
        position: positionAfter(projectionDocumentText, "builder.AppendJ"),
    })
    const renderedGenericHover = hoverText(genericHover)
    if (!renderedGenericHover.includes("<T") || !renderedGenericHover.includes("IEnumerable<T")) {
        throw new Error(`AppendJoin hover lost its generic signature: ${renderedGenericHover}`)
    }
    const interfaceDefinitions = await request("textDocument/definition", {
        textDocument: { uri: projectionUri },
        position: positionAfter(projectionDocumentText, "System.IClone"),
    })
    const interfaceLocations = Array.isArray(interfaceDefinitions)
        ? interfaceDefinitions
        : interfaceDefinitions ? [interfaceDefinitions] : []
    const interfaceDefinitionUris = interfaceLocations.map(location => location.targetUri ?? location.uri)
    if (!interfaceDefinitionUris.some(uri => uri?.includes("dotkt-klib-java-stubs.jar"))) {
        throw new Error(`ICloneable definition did not navigate to the Java stub: ${interfaceDefinitionUris.join(", ")}`)
    }

    if (metadataEnabled) {
        const receiverErrors = await errorDiagnostics(receiverUri)
        if (receiverErrors.length !== 0) {
            throw new Error(`Receiver lambda diagnostics: ${JSON.stringify(receiverErrors)}`)
        }
        for (const needle of [
            'configure = { Font',
            'configure = { this.Font',
            'ordinary { it.Font',
            'nested(listOf({ Font',
            'nullable { Font',
        ]) {
            const completion = await request("textDocument/completion", {
                textDocument: { uri: receiverUri },
                position: positionAfter(receiverDocumentText, needle),
                context: { triggerKind: 1 },
            })
            if (!completionLabels(completion).includes("FontSize")) {
                throw new Error(`Lambda completion did not resolve FontSize at ${needle}; labels=${completionLabels(completion).slice(0, 20).join(", ")}; diagnostics=${JSON.stringify(receiverDiagnostics)}`)
            }
        }
        const receiverScope = completionLabels(await request("textDocument/completion", {
            textDocument: { uri: receiverUri },
            position: positionAfter(receiverDocumentText, 'configure = { '),
            context: { triggerKind: 1 },
        }))
        if (receiverScope.includes("it")) {
            throw new Error("A receiver-only lambda incorrectly introduced an implicit it parameter")
        }
        console.log("Kotlin LSP receiver lambda: implicit receiver, this, ordinary it, nested and nullable receivers; callback invocation and Task.Delay(Int)")

        const suspendErrors = await errorDiagnostics(suspendUri)
        if (suspendErrors.length !== 0) {
            throw new Error(`Suspend lambda diagnostics: ${JSON.stringify(suspendErrors)}`)
        }
        for (const [needle, expected] of [
            ['flow { emi', 'emit'],
            ['withContext { delay(3L); mar', 'marker'],
            ['nullableSuspend { delay(6L); mar', 'marker'],
        ]) {
            const completion = await request("textDocument/completion", {
                textDocument: { uri: suspendUri },
                position: positionAfter(suspendDocumentText, needle),
                context: { triggerKind: 1 },
            })
            if (!completionLabels(completion).includes(expected)) {
                throw new Error(`Suspend lambda completion did not resolve ${expected} at ${needle}`)
            }
        }
        const inferredHover = hoverText(await request("textDocument/hover", {
            textDocument: { uri: suspendUri },
            position: positionAfter(suspendDocumentText, 'val typed: Int = infer'),
        }))
        if (!/inferred:\s*Int\b/.test(inferredHover)) {
            throw new Error(`withContext did not infer Int: ${inferredHover}`)
        }
        // A normal callback must still reject suspend calls. This prevents a "fix" that simply
        // grants coroutine context to every lambda or suppresses the diagnostic altogether.
        notify("textDocument/didChange", {
            textDocument: { uri: suspendUri, version: 2 },
            contentChanges: [{ text: suspendDocumentText +
                '\nfun notSuspendProbe() { receiverfixture.ordinary { delay(7L) } }\n' }],
        })
        const negativeErrors = await errorDiagnostics(suspendUri)
        if (negativeErrors.length !== 1 || negativeErrors[0].code !== "ILLEGAL_SUSPEND_FUNCTION_CALL") {
            throw new Error(`Ordinary callback did not reject a suspend call: ${JSON.stringify(negativeErrors)}`)
        }
        notify("textDocument/didChange", {
            textDocument: { uri: suspendUri, version: 3 },
            contentChanges: [{ text: suspendDocumentText }],
        })
        console.log("Kotlin LSP suspend lambdas: flow/emit, launch/delay, withContext inference, plain/nested/nullable callbacks, no error diagnostics")
        console.log("Kotlin LSP negative control: ordinary callback still reports ILLEGAL_SUSPEND_FUNCTION_CALL")
    }

    console.log("Kotlin LSP DotKt overlay completion: ClrEvent, ClrRef, EventSubscription, Span, StackBuffer, byref, clrEvent, stackBuffer")
    console.log("Kotlin LSP import completion: StringBuilder")
    console.log("Kotlin LSP member completion: Append, Length, ToString")
    console.log("Kotlin LSP multi-class completion: Encoding.GetEncoding, Encoding.UTF8, Rune.ToString, Rune.Value")
    console.log("Kotlin LSP Java inheritance completion: UTF8Encoding.WebName")
    console.log("Kotlin LSP CLR enum completion: DayOfWeek.Monday, DayOfWeek.Saturday, DayOfWeek.Sunday")
    console.log("Kotlin LSP Java generic hover: AppendJoin<T>(..., IEnumerable<T>)")
    console.log("Kotlin LSP Java interface definition: System.ICloneable")
    console.log("Kotlin LSP definition: dotkt-klib-java-stubs.jar!/System/Text/StringBuilder.class")
    console.log(metadataEnabled
        ? "Kotlin LSP metadata hover: ToString(): String (declared, not approximated)"
        : "Kotlin LSP Java-platform hover upper bound: ToString(): String?")

    const elapsed = (Date.now() - startedAt) / 1000
    console.log(
        `Kotlin LSP cold index and all assertions: ${elapsed.toFixed(1)}s ` +
        `(1-minute load average ${loadAtStart.toFixed(1)} at start, ${loadavg()[0].toFixed(1)} at end; ` +
        `a figure taken above ~1.0 is not comparable)`
    )

    await request("shutdown", null, 30_000)
    notify("exit")
    const exitedAfterNotification = await Promise.race([
        childExit.then(() => true),
        new Promise(resolvePromise => setTimeout(() => resolvePromise(false), 5_000)),
    ])
    // Recent standalone servers can leave the native launcher alive after the LSP endpoint has
    // acknowledged shutdown and received exit. The assertions are complete at that point; do not
    // leave CI waiting on a wrapper process indefinitely.
    if (!exitedAfterNotification) {
        terminationRequested = true
        child.kill("SIGTERM")
        await childExit
    }
} catch (error) {
    terminationRequested = true
    child.kill("SIGTERM")
    await childExit.catch(() => {})
    throw error
} finally {
    if (ownsSystemPath) rmSync(systemPath, { recursive: true, force: true })
}
