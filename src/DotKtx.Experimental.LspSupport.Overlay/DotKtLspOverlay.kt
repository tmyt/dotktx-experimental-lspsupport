@file:Suppress("UNUSED_PARAMETER")

package kotlin.clr

import kotlin.reflect.KProperty

/** Analysis-only declaration of DotKt's managed-reference compiler intrinsic. */
public class ClrRef<T> private constructor() {
    public operator fun getValue(thisRef: Any?, property: KProperty<*>): T =
        throw UnsupportedOperationException("DotKt LSP overlay")

    public operator fun setValue(thisRef: Any?, property: KProperty<*>, value: T): Unit =
        throw UnsupportedOperationException("DotKt LSP overlay")
}

/** Analysis-only declaration of DotKt's CLR by-reference operator. */
public fun <T> byref(x: T): ClrRef<T> =
    throw UnsupportedOperationException("DotKt LSP overlay")

/** Analysis-only declaration of DotKt's scoped stack buffer. */
public class StackBuffer<T> private constructor() {
    public val size: Int
        get() = throw UnsupportedOperationException("DotKt LSP overlay")

    public operator fun get(index: Int): T =
        throw UnsupportedOperationException("DotKt LSP overlay")

    public operator fun set(index: Int, value: T): Unit =
        throw UnsupportedOperationException("DotKt LSP overlay")

    public fun asSpan(): Span<T> =
        throw UnsupportedOperationException("DotKt LSP overlay")
}

/** Analysis-only declaration of the Kotlin view of CLR System.Span<T>. */
public class Span<T> private constructor()

/** Analysis-only declaration of DotKt's scoped stack allocation intrinsic. */
public fun <T, R> stackBuffer(n: Int, block: (StackBuffer<T>) -> R): R =
    throw UnsupportedOperationException("DotKt LSP overlay")

/** Analysis-only declaration of DotKt's compiler-provided CLR event handle. */
public abstract class ClrEvent<out T> private constructor() {
    public abstract fun subscribe(handler: @UnsafeVariance T): EventSubscription<@UnsafeVariance T>

    public abstract operator fun invoke(vararg args: Any?)

    public abstract operator fun getValue(thisRef: Any?, property: KProperty<*>): ClrEvent<T>
}

/** Analysis-only declaration of a Kotlin-owned CLR event. */
public fun clrEvent(): ClrEvent<Nothing> =
    throw UnsupportedOperationException("DotKt LSP overlay")

/** Analysis-only declaration of the closeable token returned by ClrEvent.subscribe. */
public class EventSubscription<T>(
    private val handler: T,
    private val remove: (T) -> Unit,
) : AutoCloseable {
    override fun close(): Unit = throw UnsupportedOperationException("DotKt LSP overlay")
}
