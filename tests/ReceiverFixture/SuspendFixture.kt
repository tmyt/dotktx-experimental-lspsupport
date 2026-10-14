package suspendfixture

interface CoroutineScope {
    val marker: Int
}

interface FlowCollector<T> {
    suspend fun emit(value: T)
}

class Flow<T>

fun <T> flow(block: suspend FlowCollector<T>.() -> Unit): Flow<T> = Flow<T>()

fun launch(block: suspend CoroutineScope.() -> Unit) {}

suspend fun delay(timeMillis: Long) {}

suspend fun <T> withContext(block: suspend CoroutineScope.() -> T): T =
    throw UnsupportedOperationException("analysis fixture")

fun <T> plainSuspend(block: suspend () -> T): T =
    throw UnsupportedOperationException("analysis fixture")

fun nestedSuspend(blocks: List<suspend CoroutineScope.() -> Unit>) {}

fun nullableSuspend(block: (suspend CoroutineScope.() -> Unit)?) {}
