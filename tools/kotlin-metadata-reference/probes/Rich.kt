package rich

interface Marker<out T> {
    fun produce(): T
    fun withDefault(a: Int = 1, vararg rest: String): Long = 0L
}

open class Holder<T : Comparable<T>>(val initial: T) : Marker<T> {
    override fun produce(): T = initial
    var mutable: String = ""
    internal val secret: Int get() = 1
    companion object {
        const val CONSTANT: Int = 7
        @JvmStatic fun make(): Holder<String> = Holder("")
    }
}

enum class Color { Red, Green }

fun String.extension(other: Int?): Map<String, List<Int>>? = null

val topLevel: Int get() = 1

inline fun <reified R> reifiedCall(block: (R) -> Unit): Unit = Unit

suspend fun suspending(): String = ""
