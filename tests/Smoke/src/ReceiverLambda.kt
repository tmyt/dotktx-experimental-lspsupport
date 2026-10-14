import System.Threading.Tasks.Task
import receiverfixture.text
import receiverfixture.ordinary
import receiverfixture.nested
import receiverfixture.nullable

fun receiverLambdaProbe() {
    text("Pane 1", configure = { FontSize = 20.0 })
    text("Pane 2", configure = { this.FontSize = 21.0 })
    ordinary { it.FontSize = 22.0 }
    nested(listOf({ FontSize = 23.0 }))
    nullable { FontSize = 24.0 }
}

fun callbackInvokeProbe(report: (String) -> Unit) {
    report("ready")
}

fun taskDelayProbe() = Task.Delay(1000)
