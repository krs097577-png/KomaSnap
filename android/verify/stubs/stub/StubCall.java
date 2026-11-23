package stub;
public class StubCall extends RuntimeException { public StubCall(String m) { super("android stub: " + m); } }