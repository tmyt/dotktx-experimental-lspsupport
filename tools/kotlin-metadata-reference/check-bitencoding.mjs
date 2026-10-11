import { readFileSync } from "node:fs"
const MARKER = 0x0000, CHUNK = 32767

function encode(data) {
    const chars = [MARKER, ...data]
    const parts = []
    for (let i = 0; i < chars.length; i += CHUNK)
        parts.push(String.fromCharCode(...chars.slice(i, i + CHUNK)))
    return parts
}

let checked = 0
for (const line of readFileSync(process.argv[2], "utf8").split("\n")) {
    if (!line) continue
    const [dataHex, ...partHexes] = line.split("\t")
    const data = Buffer.from(dataHex, "hex")
    const expected = partHexes.map(p =>
        String.fromCharCode(...p.match(/.{4}/g)?.map(h => parseInt(h, 16)) ?? []))
    const actual = encode(data)
    if (JSON.stringify(actual) !== JSON.stringify(expected))
        throw new Error(`mismatch for ${dataHex.slice(0, 32)}: got ${actual.length} parts, want ${expected.length}`)
    checked++
}
console.log(`BitEncoding model matches the Kotlin reference on ${checked} vectors`)
