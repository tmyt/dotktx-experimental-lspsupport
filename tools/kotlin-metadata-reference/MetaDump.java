import org.jetbrains.kotlin.metadata.jvm.deserialization.BitEncoding;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;

/** Minimal classfile reader that extracts the kotlin.Metadata annotation payload. Dev tooling only. */
public final class MetaDump {
    public static void main(String[] args) throws Exception {
        byte[] classFile = Files.readAllBytes(Path.of(args[0]));
        String outPrefix = args[1];
        new MetaDump(classFile).run(outPrefix);
    }

    private final DataInputStream in;
    private final Object[] pool;

    MetaDump(byte[] classFile) throws IOException {
        in = new DataInputStream(new ByteArrayInputStream(classFile));
        in.readInt(); in.readUnsignedShort(); in.readUnsignedShort();
        int count = in.readUnsignedShort();
        pool = new Object[count];
        for (int i = 1; i < count; i++) {
            int tag = in.readUnsignedByte();
            switch (tag) {
                case 1 -> pool[i] = in.readUTF();
                case 3 -> pool[i] = in.readInt();
                case 4 -> pool[i] = Float.intBitsToFloat(in.readInt());
                case 5 -> { pool[i] = in.readLong(); i++; }
                case 6 -> { pool[i] = in.readDouble(); i++; }
                case 7, 8, 16, 19, 20 -> pool[i] = in.readUnsignedShort();
                case 15 -> { in.readUnsignedByte(); in.readUnsignedShort(); }
                case 9, 10, 11, 12, 17, 18 -> { in.readUnsignedShort(); in.readUnsignedShort(); }
                default -> throw new IOException("tag " + tag);
            }
        }
    }

    void run(String outPrefix) throws IOException {
        in.readUnsignedShort(); in.readUnsignedShort(); in.readUnsignedShort();
        int interfaces = in.readUnsignedShort();
        for (int i = 0; i < interfaces; i++) in.readUnsignedShort();
        skipMembers(); skipMembers();
        int attributes = in.readUnsignedShort();
        for (int i = 0; i < attributes; i++) {
            String name = (String) pool[in.readUnsignedShort()];
            int length = in.readInt();
            byte[] body = in.readNBytes(length);
            if (!name.equals("RuntimeVisibleAnnotations")) continue;
            readAnnotations(body, outPrefix);
        }
    }

    void skipMembers() throws IOException {
        int count = in.readUnsignedShort();
        for (int i = 0; i < count; i++) {
            in.readUnsignedShort(); in.readUnsignedShort(); in.readUnsignedShort();
            int attributes = in.readUnsignedShort();
            for (int a = 0; a < attributes; a++) { in.readUnsignedShort(); in.readNBytes(in.readInt()); }
        }
    }

    void readAnnotations(byte[] body, String outPrefix) throws IOException {
        DataInputStream annotations = new DataInputStream(new ByteArrayInputStream(body));
        int count = annotations.readUnsignedShort();
        for (int i = 0; i < count; i++) {
            String type = (String) pool[annotations.readUnsignedShort()];
            int pairs = annotations.readUnsignedShort();
            Map<String, Object> values = new LinkedHashMap<>();
            for (int p = 0; p < pairs; p++) {
                String key = (String) pool[annotations.readUnsignedShort()];
                values.put(key, readValue(annotations));
            }
            if (!type.equals("Lkotlin/Metadata;")) continue;
            System.out.println("k=" + values.get("k") + " mv=" + values.get("mv") + " xi=" + values.get("xi"));
            List<?> d1 = (List<?>) values.get("d1");
            List<?> d2 = (List<?>) values.getOrDefault("d2", List.of());
            String[] data = d1.stream().map(String::valueOf).toArray(String[]::new);
            Files.write(Path.of(outPrefix + ".bin"), BitEncoding.decodeBytes(data));
            Files.write(Path.of(outPrefix + ".d2.txt"),
                d2.stream().map(String::valueOf).toList(), StandardCharsets.UTF_8);
            System.out.println("d1 bytes=" + Files.size(Path.of(outPrefix + ".bin")) + " d2 entries=" + d2.size());
        }
    }

    Object readValue(DataInputStream input) throws IOException {
        int tag = input.readUnsignedByte();
        return switch (tag) {
            case 's', 'I', 'B', 'C', 'D', 'F', 'J', 'S', 'Z' -> pool[input.readUnsignedShort()];
            case 'e' -> { input.readUnsignedShort(); yield pool[input.readUnsignedShort()]; }
            case 'c' -> pool[input.readUnsignedShort()];
            case '[' -> {
                int size = input.readUnsignedShort();
                List<Object> items = new ArrayList<>();
                for (int i = 0; i < size; i++) items.add(readValue(input));
                yield items;
            }
            default -> throw new IOException("value tag " + (char) tag);
        };
    }
}
