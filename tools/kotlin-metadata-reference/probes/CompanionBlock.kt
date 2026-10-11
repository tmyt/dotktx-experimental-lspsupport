package probe3

class Task {
    companion {
        val BackedByField: Int = 9
        var Mutable: String = "x"
        fun Blocked(): Int = 2
    }
}

companion fun Task.Extra(): Int = 3
companion val Task.Extended: Int get() = 4

fun use(): Int = Task.Blocked() + Task.BackedByField + Task.Extra() + Task.Extended
