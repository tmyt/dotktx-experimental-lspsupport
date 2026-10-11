// Schema-free protobuf wire dumper: prints (field, wiretype, value) so a schema can be read off
// real payloads instead of recalled from memory.
import { readFileSync } from "node:fs"

const bytes = readFileSync(process.argv[2])
const strings = process.argv[3]
    ? readFileSync(process.argv[3], "utf8").split("\n").slice(0, -1)
    : []
const maxDepth = Number(process.argv[4] ?? 6)

function varint(buf, at) {
    let result = 0n, shift = 0n, i = at
    while (true) {
        const b = buf[i++]
        result |= BigInt(b & 0x7f) << shift
        if ((b & 0x80) === 0) break
        shift += 7n
    }
    return [result, i]
}

function printable(buf) {
    // A nested message must re-parse cleanly and consume every byte.
    try {
        const fields = parse(buf, 0)
        return fields.length > 0 ? fields : null
    } catch { return null }
}

function parse(buf, depth) {
    const out = []
    let i = 0
    while (i < buf.length) {
        const [key, next] = varint(buf, i)
        i = next
        const field = Number(key >> 3n), wire = Number(key & 7n)
        if (field === 0) throw new Error("field 0")
        if (wire === 0) { const [v, n] = varint(buf, i); i = n; out.push({ field, wire, value: v }) }
        else if (wire === 2) {
            const [len, n] = varint(buf, i); i = n
            const end = n + Number(len)
            if (end > buf.length) throw new Error("overrun")
            out.push({ field, wire, bytes: buf.subarray(n, end) })
            i = end
        }
        else if (wire === 5) { out.push({ field, wire, value: buf.readUInt32LE(i) }); i += 4 }
        else if (wire === 1) { out.push({ field, wire, value: buf.readBigUInt64LE(i) }); i += 8 }
        else throw new Error("wire " + wire)
    }
    return out
}

function render(fields, depth, indent) {
    for (const f of fields) {
        if (f.wire !== 2) {
            const hint = strings[Number(f.value)] !== undefined ? `  // "${strings[Number(f.value)]}"` : ""
            console.log(`${indent}#${f.field} varint ${f.value}${hint}`)
            continue
        }
        const nested = depth < maxDepth ? printable(f.bytes) : null
        if (nested) {
            console.log(`${indent}#${f.field} len=${f.bytes.length} {`)
            render(nested, depth + 1, indent + "  ")
            console.log(`${indent}}`)
        } else {
            const text = f.bytes.toString("utf8")
            const ascii = /^[\x20-\x7e]*$/.test(text) && text.length > 0
            console.log(`${indent}#${f.field} bytes[${f.bytes.length}] ${ascii ? JSON.stringify(text) : f.bytes.toString("hex")}`)
        }
    }
}

// The payload is a length-delimited StringTableTypes followed by the Class/Package message.
const [tableLength, afterLength] = varint(bytes, 0)
console.log(`== StringTableTypes (${tableLength} bytes) ==`)
render(parse(bytes.subarray(afterLength, afterLength + Number(tableLength)), 0), 0, "")
console.log(`== message (${bytes.length - afterLength - Number(tableLength)} bytes) ==`)
render(parse(bytes.subarray(afterLength + Number(tableLength)), 0), 0, "")
