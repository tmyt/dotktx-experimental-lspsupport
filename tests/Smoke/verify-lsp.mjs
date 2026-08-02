#!/usr/bin/env node

import { spawn } from "node:child_process"
import { existsSync, readFileSync, readdirSync } from "node:fs"
import { homedir } from "node:os"
import { dirname, join, resolve } from "node:path"
import { fileURLToPath, pathToFileURL } from "node:url"

const projectDir = resolve(dirname(fileURLToPath(import.meta.url)))
const sourcePath = join(projectDir, "src", "Program.kt")
const sourceUri = pathToFileURL(sourcePath).href
const rootUri = pathToFileURL(projectDir).href
const documentText = readFileSync(sourcePath, "utf8")
const analysisDocumentText = "import kotlin.clr.\n" + documentText

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
    "--system-path", join(projectDir, ".lsp-system", String(process.pid)),
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
    if (Object.hasOwn(message, "id") && !message.method) {
        const waiter = pending.get(message.id)
        if (!waiter) return
        pending.delete(message.id)
        if (message.error) waiter.reject(new Error(JSON.stringify(message.error)))
        else waiter.resolve(message.result)
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

const childExit = new Promise((resolvePromise, rejectPromise) => {
    child.once("error", rejectPromise)
    child.once("exit", (code, signal) => {
        if (code === 0 || signal === "SIGTERM") resolvePromise()
        else rejectPromise(new Error(`Kotlin LSP exited: code=${code}, signal=${signal}`))
    })
})

function completionLabels(result) {
    const items = Array.isArray(result) ? result : result?.items ?? []
    return items.map(item => item.label)
}

async function waitForCompletion(position, expected) {
    const deadline = Date.now() + 180_000
    let labels = []
    while (Date.now() < deadline) {
        try {
            const result = await request("textDocument/completion", {
                textDocument: { uri: sourceUri },
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
    if (!definitionUris.some(uri => uri?.includes("/.dotkt/lsp/stubs/"))) {
        throw new Error(`Definition did not navigate to the CLR stub: ${definitionUris.join(", ")}`)
    }

    console.log("Kotlin LSP DotKt overlay completion: ClrEvent, ClrRef, EventSubscription, Span, StackBuffer, byref, clrEvent, stackBuffer")
    console.log("Kotlin LSP import completion: StringBuilder")
    console.log("Kotlin LSP member completion: Append, Length, ToString")
    console.log("Kotlin LSP definition: .dotkt/lsp/stubs/.../Declarations.kt")

    await request("shutdown", null, 30_000)
    notify("exit")
    await childExit
} catch (error) {
    child.kill("SIGTERM")
    throw error
}
