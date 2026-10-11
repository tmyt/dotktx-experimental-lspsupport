import org.jetbrains.kotlin.metadata.jvm.deserialization.BitEncoding;
import java.io.*;
import java.nio.file.*;
import java.util.*;

public final class MetaTool {
    public static void main(String[] args) throws Exception {
        switch (args[0]) {
            case "vectors" -> vectors(Integer.parseInt(args[1]), args[2]);
            case "decode" -> decode(args[1], args[2]);
            default -> throw new IllegalArgumentException(args[0]);
        }
    }

    // Reference vectors so an independent encoder can be checked byte for byte.
    static void vectors(int count, String out) throws Exception {
        Random random = new Random(20260810);
        try (PrintWriter writer = new PrintWriter(out)) {
            for (int i = 0; i < count; i++) {
                byte[] data = new byte[i == 0 ? 0 : random.nextInt(400)];
                random.nextBytes(data);
                String[] encoded = BitEncoding.encodeBytes(data);
                if (!Arrays.equals(BitEncoding.decodeBytes(encoded), data))
                    throw new IllegalStateException("round trip failed at " + i);
                StringBuilder line = new StringBuilder(hex(data));
                for (String part : encoded) line.append('\t').append(hexChars(part));
                writer.println(line);
            }
        }
    }

    static void decode(String stringsFile, String out) throws Exception {
        List<String> lines = Files.readAllLines(Path.of(stringsFile));
        String[] data = new String[lines.size()];
        for (int i = 0; i < lines.size(); i++) data[i] = unhexChars(lines.get(i));
        Files.write(Path.of(out), BitEncoding.decodeBytes(data));
    }

    static String hex(byte[] data) {
        StringBuilder builder = new StringBuilder();
        for (byte b : data) builder.append(String.format("%02x", b));
        return builder.toString();
    }

    static String hexChars(String value) {
        StringBuilder builder = new StringBuilder();
        for (char c : value.toCharArray()) builder.append(String.format("%04x", (int) c));
        return builder.toString();
    }

    static String unhexChars(String value) {
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < value.length(); i += 4)
            builder.append((char) Integer.parseInt(value.substring(i, i + 4), 16));
        return builder.toString();
    }
}
