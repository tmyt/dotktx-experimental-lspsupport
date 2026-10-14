import suspendfixture.flow
import suspendfixture.launch
import suspendfixture.delay
import suspendfixture.withContext
import suspendfixture.plainSuspend
import suspendfixture.nestedSuspend
import suspendfixture.nullableSuspend

fun suspendLambdaProbe() {
    val inferredFlow = flow { emit(1); delay(1L) }
    launch {
        delay(2L)
        val inferred = withContext { delay(3L); marker + 1 }
        val typed: Int = inferred
    }
    val result: Int = plainSuspend { delay(4L); 42 }
    nestedSuspend(listOf({ delay(5L); marker }))
    nullableSuspend { delay(6L); marker }
}
