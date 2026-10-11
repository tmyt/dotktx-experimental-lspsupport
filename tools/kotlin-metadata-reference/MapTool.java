import org.jetbrains.kotlin.metadata.jvm.deserialization.BitEncoding;

public final class MapTool {
    public static void main(String[] args) {
        for (int value = 0; value < 256; value++) {
            String[] encoded = BitEncoding.encodeBytes(new byte[] { (byte) value });
            StringBuilder chars = new StringBuilder();
            for (char c : encoded[0].toCharArray()) chars.append(String.format("%04x ", (int) c));
            System.out.printf("%02x -> parts=%d %s%n", value, encoded.length, chars.toString().trim());
        }
        // Length behaviour: where does a single string get split?
        for (int size : new int[] { 1, 100, 65533, 65534, 65535, 65536, 131070, 200000 }) {
            String[] encoded = BitEncoding.encodeBytes(new byte[size]);
            int[] lengths = new int[encoded.length];
            for (int i = 0; i < encoded.length; i++) lengths[i] = encoded[i].length();
            System.out.printf("size=%d parts=%d firstLen=%d lastLen=%d%n",
                size, encoded.length, lengths[0], lengths[lengths.length - 1]);
        }
    }
}
