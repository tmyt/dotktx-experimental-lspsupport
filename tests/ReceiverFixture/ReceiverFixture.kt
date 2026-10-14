package receiverfixture

class TextBlock {
    var FontSize: Double = 0.0
}

fun text(content: String, configure: TextBlock.() -> Unit = {}): TextBlock {
    val block = TextBlock()
    block.configure()
    return block
}

fun ordinary(configure: (TextBlock) -> Unit) {
    configure(TextBlock())
}

fun nested(configures: List<TextBlock.() -> Unit>) {
    val block = TextBlock()
    for (configure in configures) block.configure()
}

fun nullable(configure: (TextBlock.() -> Unit)?) {
    if (configure != null) TextBlock().configure()
}
