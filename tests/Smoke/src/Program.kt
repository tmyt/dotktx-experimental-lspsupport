import System.Text.StringBuilder

fun greeting(name: String): String {
    val builder = StringBuilder()
    builder.Append("Hello, ")
    builder.Append(name)
    return builder.ToString()
}

fun main() {
    println(greeting("DotKtx"))
}
