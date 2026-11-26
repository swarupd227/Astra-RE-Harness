package astra.equivalence;

import java.io.InputStream;
import java.lang.annotation.Annotation;
import java.lang.reflect.*;
import java.math.BigDecimal;
import java.math.BigInteger;
import java.nio.charset.StandardCharsets;
import java.util.*;

import org.junit.jupiter.api.Test;

/**
 * Astra equivalence harness — added to a generated Java package for one EQUIVALENCE run, never shipped.
 *
 * Reads {@code astra-equivalence/request.json} from the test classpath: the class and method that correspond to the
 * PHP routine, and the input vectors the PHP sidecar ran. For each vector it builds the method's arguments from the
 * vector's inputs (by parameter name, by position, or — when the method takes one map/record/bean — as that one
 * object's fields), calls the method by reflection and prints one line {@code @@ASTRA-EQ@@ {json}} with the return
 * value (a Spring ResponseEntity as {status, body, headers}) or the exception thrown. Collaborators the method's class
 * needs in its constructor are given as stand-ins that throw when called, so a vector that reaches a repository or a
 * remote port is reported as "needs a dependency" instead of passing by accident. Depends on JUnit 5 only.
 */
public class AstraEquivalenceHarnessTest {

    private static final String MARK = "@@ASTRA-EQ@@ ";

    @Test
    void runVectors() throws Exception {
        String json;
        try (InputStream in = getClass().getClassLoader().getResourceAsStream("astra-equivalence/request.json")) {
            if (in == null) { System.out.println(MARK + "{\"fatal\":\"request.json not on the test classpath\"}"); return; }
            json = new String(in.readAllBytes(), StandardCharsets.UTF_8);
        }
        Map<String, Object> req = asMap(new Json(json).parse());
        String className = (String) req.get("className");
        String methodName = (String) req.get("methodName");
        Number arity = (Number) req.get("paramCount");
        Class<?> cls;
        try {
            cls = Class.forName(className);
        } catch (Throwable e) {
            System.out.println(MARK + Json.write(map("fatal", "class not found: " + className)));
            return;
        }
        Method method = pick(cls, methodName, arity == null ? -1 : arity.intValue());
        if (method == null) {
            System.out.println(MARK + Json.write(map("fatal", "no method " + methodName + " on " + className)));
            return;
        }
        method.setAccessible(true);
        System.out.println(MARK + Json.write(map("method", describe(method))));
        for (Object v : (List<?>) req.getOrDefault("vectors", List.of())) {
            System.out.println(MARK + Json.write(runOne(cls, method, asMap(v))));
        }
    }

    // ── one vector ──────────────────────────────────────────────────────────

    private static Map<String, Object> runOne(Class<?> cls, Method m, Map<String, Object> vector) {
        Map<String, Object> out = new LinkedHashMap<>();
        out.put("id", vector.get("id"));
        List<String> notes = new ArrayList<>();
        List<String> unsupplied = new ArrayList<>();
        Object[] args;
        try {
            args = bind(m, vector, notes);
        } catch (Exception e) {
            out.put("ok", false);
            out.put("inputError", e.getMessage());
            out.put("notes", notes);
            return out;
        }
        Object target = null;
        if (!Modifier.isStatic(m.getModifiers())) {
            try {
                target = instantiate(cls, notes, unsupplied, 0);
            } catch (Exception e) {
                out.put("ok", false);
                out.put("inputError", "cannot construct " + cls.getSimpleName() + ": " + e);
                return out;
            }
        }
        long t0 = System.nanoTime();
        try {
            Object ret = m.invoke(target, args);
            out.put("ok", true);
            out.put("returnValue", value(ret, 0, new IdentityHashMap<>()));
            out.put("returnType", m.getReturnType() == void.class ? "void" : (ret == null ? "null" : ret.getClass().getSimpleName()));
        } catch (InvocationTargetException ite) {
            Throwable c = ite.getCause() == null ? ite : ite.getCause();
            out.put("ok", true);
            Map<String, Object> ex = new LinkedHashMap<>();
            ex.put("class", c.getClass().getSimpleName());
            ex.put("message", c.getMessage());
            Integer status = statusOf(c);
            if (status != null) ex.put("status", status);
            for (Throwable x = c; x != null; x = x.getCause() == x ? null : x.getCause())
                if (x instanceof NotSupplied ns) { unsupplied.add(ns.what); break; }
            out.put("exception", ex);
        } catch (Exception e) {
            out.put("ok", false);
            out.put("inputError", "call failed: " + e);
        }
        out.put("durationMs", (System.nanoTime() - t0) / 1_000_000);
        out.put("notes", notes);
        out.put("unsupplied", unsupplied);
        return out;
    }

    private static Method pick(Class<?> cls, String name, int arity) {
        Method best = null;
        for (Class<?> c = cls; c != null && c != Object.class; c = c.getSuperclass()) {
            for (Method m : c.getDeclaredMethods()) {
                if (m.isSynthetic() || m.isBridge() || !m.getName().equals(name)) continue;
                if (arity >= 0 && m.getParameterCount() == arity) return m;
                if (best == null || Modifier.isPublic(m.getModifiers()) && !Modifier.isPublic(best.getModifiers())) best = m;
            }
            if (best != null) return best;
        }
        return best;
    }

    private static Map<String, Object> describe(Method m) {
        List<Object> ps = new ArrayList<>();
        for (Parameter p : m.getParameters()) ps.add(map("name", nameOf(p), "type", p.getParameterizedType().getTypeName()));
        return map("class", m.getDeclaringClass().getName(), "name", m.getName(), "params", ps,
                   "returns", m.getGenericReturnType().getTypeName(), "static", Modifier.isStatic(m.getModifiers()));
    }

    // ── binding inputs to parameters ────────────────────────────────────────

    private static Object[] bind(Method m, Map<String, Object> vector, List<String> notes) {
        Parameter[] ps = m.getParameters();
        Object[] args = new Object[ps.length];
        Map<String, Object> named = vector.get("named") instanceof Map<?, ?> n ? asMap(n) : null;
        List<?> positional = vector.get("args") instanceof List<?> l ? l : null;
        if (named != null) {
            Map<String, Object> byNorm = new LinkedHashMap<>();
            for (Map.Entry<String, Object> e : named.entrySet()) byNorm.put(norm(e.getKey()), e.getValue());
            List<Integer> real = new ArrayList<>();
            int matched = 0;
            for (int i = 0; i < ps.length; i++) {
                if (isInfrastructure(ps[i].getType())) continue;
                real.add(i);
                if (byNorm.containsKey(norm(nameOf(ps[i])))) matched++;
            }
            if (matched > 0) {
                for (int i = 0; i < ps.length; i++) {
                    String k = norm(nameOf(ps[i]));
                    if (byNorm.containsKey(k)) args[i] = convert(byNorm.get(k), ps[i].getParameterizedType(), nameOf(ps[i]));
                    else { args[i] = missing(ps[i]); if (!isInfrastructure(ps[i].getType())) notes.add(nameOf(ps[i]) + " not supplied; passed " + args[i]); }
                }
                return args;
            }
            if (real.size() == 1) {
                // the lifted request: the inputs are the fields of the one parameter (a Map, record or bean)
                int i = real.get(0);
                Class<?> t = ps[i].getType();
                if (Map.class.isAssignableFrom(t) || t.isRecord() || !isSimple(t)) {
                    for (int j = 0; j < ps.length; j++) args[j] = j == i ? convert(named, ps[i].getParameterizedType(), nameOf(ps[i])) : missing(ps[j]);
                    notes.add("inputs passed as the fields of " + nameOf(ps[i]));
                    return args;
                }
                if (named.size() == 1) {
                    args[i] = convert(named.values().iterator().next(), ps[i].getParameterizedType(), nameOf(ps[i]));
                    notes.add("the single input bound to " + nameOf(ps[i]));
                    return args;
                }
            }
            if (real.isEmpty() && named.isEmpty()) {
                for (int i = 0; i < ps.length; i++) args[i] = missing(ps[i]);
                return args;
            }
            if (positional == null) {
                if (real.size() == named.size()) {
                    // same count, no names in common: bind in order (parameter names are not always compiled in)
                    Iterator<Object> it = named.values().iterator();
                    for (int i = 0; i < ps.length; i++) args[i] = real.contains(i) ? convert(it.next(), ps[i].getParameterizedType(), nameOf(ps[i])) : missing(ps[i]);
                    notes.add("inputs bound in order (no parameter names in common)");
                    return args;
                }
                throw new IllegalArgumentException("inputs " + named.keySet() + " do not match the parameters of " + m.getName());
            }
        }
        if (positional != null) {
            int k = 0;
            for (int i = 0; i < ps.length; i++) {
                if (isInfrastructure(ps[i].getType())) { args[i] = missing(ps[i]); continue; }
                args[i] = k < positional.size() ? convert(positional.get(k++), ps[i].getParameterizedType(), nameOf(ps[i])) : missing(ps[i]);
            }
            return args;
        }
        for (int i = 0; i < ps.length; i++) args[i] = missing(ps[i]);
        return args;
    }

    private static String nameOf(Parameter p) {
        for (Annotation a : p.getAnnotations()) {
            for (String attr : new String[] {"value", "name"}) {
                try {
                    Method am = a.annotationType().getMethod(attr);
                    if (am.getReturnType() == String.class) {
                        String v = (String) am.invoke(a);
                        if (v != null && !v.isEmpty() && !v.startsWith("\n")) return v;
                    }
                } catch (Exception ignored) { }
            }
        }
        return p.getName();
    }

    private static boolean isInfrastructure(Class<?> t) {
        String n = t.getName();
        return n.startsWith("jakarta.servlet") || n.startsWith("javax.servlet") || n.startsWith("org.springframework.ui")
            || n.startsWith("org.springframework.validation") || n.startsWith("org.springframework.web.servlet")
            || n.equals("java.security.Principal") || n.startsWith("org.springframework.security");
    }

    private static boolean isSimple(Class<?> t) {
        return t.isPrimitive() || t == String.class || Number.class.isAssignableFrom(t) || t == Boolean.class || t == Character.class
            || t.isEnum() || t.getName().startsWith("java.time") || Collection.class.isAssignableFrom(t) || t.isArray()
            || t == Optional.class || t == Object.class;
    }

    private static Object missing(Parameter p) {
        Class<?> t = p.getType();
        if (t == Optional.class) return Optional.empty();
        return t.isPrimitive() ? defaultOf(t) : null;
    }

    private static Object defaultOf(Class<?> t) {
        if (t == boolean.class) return false;
        if (t == char.class) return '\0';
        if (t == long.class) return 0L;
        if (t == double.class) return 0d;
        if (t == float.class) return 0f;
        if (t == short.class) return (short) 0;
        if (t == byte.class) return (byte) 0;
        return 0;
    }

    private static String norm(String s) {
        return s == null ? "" : s.replaceAll("[^A-Za-z0-9]", "").toLowerCase(Locale.ROOT);
    }

    // ── JSON value → Java value ─────────────────────────────────────────────

    @SuppressWarnings({"unchecked", "rawtypes"})
    static Object convert(Object v, Type type, String what) {
        Class<?> raw = rawOf(type);
        if (v == null) return raw.isPrimitive() ? defaultOf(raw) : (raw == Optional.class ? Optional.empty() : null);
        if (raw == Object.class) return plain(v);
        if (raw == Optional.class) return Optional.ofNullable(convert(v, arg(type, 0), what));
        if (raw == String.class) return v instanceof String s ? s : phpString(v);
        if (raw == CharSequence.class) return v instanceof String s ? s : phpString(v);
        if (raw == char.class || raw == Character.class) { String s = phpString(v); if (s.isEmpty()) throw bad(v, what); return s.charAt(0); }
        if (raw == boolean.class || raw == Boolean.class) return toBool(v, what);
        if (raw == int.class || raw == Integer.class) return toBig(v, what).intValueExact();
        if (raw == long.class || raw == Long.class) return toBig(v, what).longValueExact();
        if (raw == short.class || raw == Short.class) return toBig(v, what).shortValueExact();
        if (raw == byte.class || raw == Byte.class) return toBig(v, what).byteValueExact();
        if (raw == double.class || raw == Double.class) return toBig(v, what).doubleValue();
        if (raw == float.class || raw == Float.class) return toBig(v, what).floatValue();
        if (raw == BigDecimal.class) return toBig(v, what);
        if (raw == BigInteger.class) return toBig(v, what).toBigIntegerExact();
        if (raw == Number.class) return toBig(v, what);
        if (raw.isEnum()) {
            String s = phpString(v);
            for (Object c : raw.getEnumConstants()) if (((Enum<?>) c).name().equalsIgnoreCase(s) || norm(((Enum<?>) c).name()).equals(norm(s))) return c;
            if (v instanceof Number n) { Object[] cs = raw.getEnumConstants(); int i = n.intValue(); if (i >= 0 && i < cs.length) return cs[i]; }
            throw bad(v, what);
        }
        if (raw.getName().startsWith("java.time")) {
            try { return raw.getMethod("parse", CharSequence.class).invoke(null, phpString(v)); }
            catch (Exception e) { throw bad(v, what); }
        }
        if (raw.isArray()) {
            List<?> l = toList(v, what);
            Object arr = Array.newInstance(raw.getComponentType(), l.size());
            for (int i = 0; i < l.size(); i++) Array.set(arr, i, convert(l.get(i), raw.getComponentType(), what));
            return arr;
        }
        if (Map.class.isAssignableFrom(raw)) {
            if (!(v instanceof Map<?, ?> src)) {
                if (v instanceof List<?> l && l.isEmpty()) return new LinkedHashMap<>();   // PHP's [] is also an empty map
                throw bad(v, what);
            }
            Map out;
            if (raw.isInterface() || Modifier.isAbstract(raw.getModifiers()))
                out = SortedMap.class.isAssignableFrom(raw) ? new TreeMap() : new LinkedHashMap();
            else
                out = (Map) newInstance(raw);
            for (Map.Entry<?, ?> e : src.entrySet()) out.put(convert(e.getKey(), arg(type, 0), what), convert(e.getValue(), arg(type, 1), what));
            return out;
        }
        if (Collection.class.isAssignableFrom(raw) || raw == Iterable.class) {
            List<?> l = toList(v, what);
            Collection out = raw.isInterface() || Modifier.isAbstract(raw.getModifiers())
                ? (Set.class.isAssignableFrom(raw) ? new LinkedHashSet() : new ArrayList()) : (Collection) newInstance(raw);
            for (Object o : l) out.add(convert(o, arg(type, 0), what));
            return out;
        }
        if (v instanceof Map<?, ?> fields) return toObject(raw, asMap(fields), what);
        throw bad(v, what);
    }

    private static Object plain(Object v) {
        return v instanceof BigDecimal b ? (b.scale() <= 0 && b.abs().compareTo(BigDecimal.valueOf(Long.MAX_VALUE)) < 0 ? (Object) b.longValue() : b.doubleValue()) : v;
    }

    private static Object toObject(Class<?> raw, Map<String, Object> fields, String what) {
        Map<String, Object> byNorm = new LinkedHashMap<>();
        fields.forEach((k, x) -> byNorm.put(norm(k), x));
        try {
            if (raw.isRecord()) {
                RecordComponent[] rc = raw.getRecordComponents();
                Class<?>[] types = new Class<?>[rc.length];
                Object[] vals = new Object[rc.length];
                for (int i = 0; i < rc.length; i++) {
                    types[i] = rc[i].getType();
                    vals[i] = convert(byNorm.get(norm(rc[i].getName())), rc[i].getGenericType(), rc[i].getName());
                }
                Constructor<?> c = raw.getDeclaredConstructor(types);
                c.setAccessible(true);
                return c.newInstance(vals);
            }
            Object o = newInstance(raw);
            for (Class<?> c = raw; c != null && c != Object.class; c = c.getSuperclass()) {
                for (Field f : c.getDeclaredFields()) {
                    if (Modifier.isStatic(f.getModifiers()) || !byNorm.containsKey(norm(f.getName()))) continue;
                    f.setAccessible(true);
                    f.set(o, convert(byNorm.get(norm(f.getName())), f.getGenericType(), f.getName()));
                }
            }
            return o;
        } catch (IllegalArgumentException e) {
            throw e;
        } catch (Exception e) {
            throw new IllegalArgumentException("cannot build " + raw.getSimpleName() + " for " + what + ": " + e);
        }
    }

    private static Object newInstance(Class<?> raw) {
        try {
            Constructor<?> c = raw.getDeclaredConstructor();
            c.setAccessible(true);
            return c.newInstance();
        } catch (Exception e) {
            throw new IllegalArgumentException("cannot construct " + raw.getSimpleName() + " without arguments");
        }
    }

    private static List<?> toList(Object v, String what) {
        if (v instanceof List<?> l) return l;
        if (v instanceof Map<?, ?> m) return new ArrayList<>(m.values());
        throw bad(v, what);
    }

    private static Class<?> rawOf(Type t) {
        if (t instanceof Class<?> c) return c;
        if (t instanceof ParameterizedType p) return (Class<?>) p.getRawType();
        if (t instanceof WildcardType w) return rawOf(w.getUpperBounds()[0]);
        if (t instanceof GenericArrayType g) return Array.newInstance(rawOf(g.getGenericComponentType()), 0).getClass();
        return Object.class;
    }

    private static Type arg(Type t, int i) {
        return t instanceof ParameterizedType p && p.getActualTypeArguments().length > i ? p.getActualTypeArguments()[i] : Object.class;
    }

    private static BigDecimal toBig(Object v, String what) {
        if (v instanceof BigDecimal b) return b;
        if (v instanceof Number n) return new BigDecimal(n.toString());
        if (v instanceof Boolean b) return b ? BigDecimal.ONE : BigDecimal.ZERO;
        if (v instanceof String s) {
            try { return new BigDecimal(s.trim()); } catch (NumberFormatException e) { throw bad(v, what); }
        }
        throw bad(v, what);
    }

    private static boolean toBool(Object v, String what) {
        if (v instanceof Boolean b) return b;
        if (v instanceof Number n) return new BigDecimal(n.toString()).signum() != 0;
        if (v instanceof String s) {
            String t = s.trim().toLowerCase(Locale.ROOT);
            if (t.equals("true") || t.equals("1") || t.equals("yes") || t.equals("on")) return true;
            if (t.equals("false") || t.equals("0") || t.isEmpty() || t.equals("no") || t.equals("off")) return false;
        }
        throw bad(v, what);
    }

    private static String phpString(Object v) {
        if (v instanceof Boolean b) return b ? "1" : "";
        if (v instanceof BigDecimal b) return b.stripTrailingZeros().scale() <= 0 ? b.toBigInteger().toString() : b.toPlainString();
        return String.valueOf(v);
    }

    private static IllegalArgumentException bad(Object v, String what) {
        return new IllegalArgumentException("input " + Json.write(v) + " for " + what + " has no Java value of that type");
    }

    // ── constructing the method's class ─────────────────────────────────────

    private static Object instantiate(Class<?> cls, List<String> notes, List<String> unsupplied, int depth) throws Exception {
        Constructor<?> best = null;
        for (Constructor<?> c : cls.getDeclaredConstructors()) {
            if (c.isSynthetic()) continue;
            if (best == null || c.getParameterCount() < best.getParameterCount()) best = c;
        }
        if (best == null) throw new IllegalStateException("no constructor");
        best.setAccessible(true);
        Object[] args = new Object[best.getParameterCount()];
        Class<?>[] types = best.getParameterTypes();
        for (int i = 0; i < args.length; i++) {
            Class<?> t = types[i];
            if (t.isPrimitive()) args[i] = defaultOf(t);
            else if (t == String.class) args[i] = "";
            else if (t.isInterface()) { args[i] = standIn(t); notes.add(cls.getSimpleName() + " given a stand-in " + t.getSimpleName()); }
            else if (depth < 2 && !isSimple(t) && !Modifier.isAbstract(t.getModifiers())) {
                try { args[i] = instantiate(t, notes, unsupplied, depth + 1); } catch (Exception e) { args[i] = null; }
            } else args[i] = null;
        }
        return best.newInstance(args);
    }

    static final class NotSupplied extends RuntimeException {
        final String what;
        NotSupplied(String what) { super("astra-equivalence: " + what + " is not available in an equivalence run"); this.what = what; }
    }

    private static Object standIn(Class<?> iface) {
        return Proxy.newProxyInstance(iface.getClassLoader(), new Class<?>[] {iface}, (proxy, method, a) -> {
            switch (method.getName()) {
                case "toString": return "stand-in " + iface.getSimpleName();
                case "hashCode": return System.identityHashCode(proxy);
                case "equals": return proxy == a[0];
                default: throw new NotSupplied(iface.getSimpleName() + "." + method.getName());
            }
        });
    }

    // ── Java value → JSON value ─────────────────────────────────────────────

    private static Integer statusOf(Throwable t) {
        for (String getter : new String[] {"getStatusCode", "getStatus", "getRawStatusCode"}) {
            try {
                Method m = t.getClass().getMethod(getter);
                Object s = m.invoke(t);
                if (s instanceof Integer i) return i;
                if (s != null) {
                    try { return (Integer) s.getClass().getMethod("value").invoke(s); } catch (Exception ignored) { }
                }
            } catch (Exception ignored) { }
        }
        return null;
    }

    static Object value(Object o, int depth, IdentityHashMap<Object, Boolean> seen) {
        if (depth > 10) return "…";
        if (o == null || o instanceof String || o instanceof Boolean) return o;
        if (o instanceof Character c) return String.valueOf(c);
        if (o instanceof BigDecimal || o instanceof BigInteger || o instanceof Number) return o;
        if (o instanceof Enum<?> e) return e.name();
        if (o instanceof Optional<?> opt) return opt.map(x -> value(x, depth + 1, seen)).orElse(null);
        if (o.getClass().getName().startsWith("java.time") || o instanceof UUID || o instanceof Date) return o.toString();
        if (seen.containsKey(o)) return "(cycle)";
        seen.put(o, true);
        if (o.getClass().isArray()) {
            List<Object> l = new ArrayList<>();
            for (int i = 0; i < Array.getLength(o); i++) l.add(value(Array.get(o, i), depth + 1, seen));
            return l;
        }
        if (o instanceof Iterable<?> it) {
            List<Object> l = new ArrayList<>();
            for (Object x : it) l.add(value(x, depth + 1, seen));
            return l;
        }
        if (o instanceof Map<?, ?> m) {
            Map<String, Object> out = new LinkedHashMap<>();
            for (Map.Entry<?, ?> e : m.entrySet()) out.put(String.valueOf(e.getKey()), value(e.getValue(), depth + 1, seen));
            return out;
        }
        if (isResponseEntity(o)) {
            Map<String, Object> out = new LinkedHashMap<>();
            out.put("__http", true);
            out.put("status", statusOfEntity(o));
            try { out.put("body", value(o.getClass().getMethod("getBody").invoke(o), depth + 1, seen)); } catch (Exception e) { out.put("body", null); }
            try { out.put("headers", value(o.getClass().getMethod("getHeaders").invoke(o), depth + 1, seen)); } catch (Exception e) { out.put("headers", Map.of()); }
            return out;
        }
        Map<String, Object> out = new LinkedHashMap<>();
        out.put("__class", o.getClass().getSimpleName());
        if (o.getClass().isRecord()) {
            for (RecordComponent rc : o.getClass().getRecordComponents()) {
                try { Method a = rc.getAccessor(); a.setAccessible(true); out.put(rc.getName(), value(a.invoke(o), depth + 1, seen)); }
                catch (Exception e) { out.put(rc.getName(), null); }
            }
            return out;
        }
        for (Method m : o.getClass().getMethods()) {
            if (m.getParameterCount() != 0 || m.getDeclaringClass() == Object.class || Modifier.isStatic(m.getModifiers())) continue;
            String n = m.getName();
            String prop = n.startsWith("get") && n.length() > 3 ? n.substring(3) : n.startsWith("is") && n.length() > 2 && (m.getReturnType() == boolean.class || m.getReturnType() == Boolean.class) ? n.substring(2) : null;
            if (prop == null || n.equals("getClass")) continue;
            try { out.put(Character.toLowerCase(prop.charAt(0)) + prop.substring(1), value(m.invoke(o), depth + 1, seen)); } catch (Exception ignored) { }
        }
        for (Field f : o.getClass().getFields()) {
            if (Modifier.isStatic(f.getModifiers()) || out.containsKey(f.getName())) continue;
            try { out.put(f.getName(), value(f.get(o), depth + 1, seen)); } catch (Exception ignored) { }
        }
        return out;
    }

    private static boolean isResponseEntity(Object o) {
        for (Class<?> c = o.getClass(); c != null; c = c.getSuperclass())
            if (c.getName().equals("org.springframework.http.ResponseEntity") || c.getName().equals("org.springframework.http.HttpEntity") && hasMethod(o, "getStatusCode")) return true;
        return false;
    }

    private static boolean hasMethod(Object o, String name) {
        try { o.getClass().getMethod(name); return true; } catch (Exception e) { return false; }
    }

    private static Object statusOfEntity(Object o) {
        try {
            Object s = o.getClass().getMethod("getStatusCode").invoke(o);
            return s instanceof Integer i ? i : s.getClass().getMethod("value").invoke(s);
        } catch (Exception e) { return null; }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    @SuppressWarnings("unchecked")
    static Map<String, Object> asMap(Object o) {
        return o instanceof Map<?, ?> m ? (Map<String, Object>) m : new LinkedHashMap<>();
    }

    static Map<String, Object> map(Object... kv) {
        Map<String, Object> m = new LinkedHashMap<>();
        for (int i = 0; i + 1 < kv.length; i += 2) m.put((String) kv[i], kv[i + 1]);
        return m;
    }

    /** A small JSON reader/writer so the harness needs nothing beyond JUnit. Numbers read as BigDecimal. */
    static final class Json {
        private final String s;
        private int i;

        Json(String s) { this.s = s; }

        Object parse() { ws(); Object v = val(); ws(); return v; }

        private Object val() {
            ws();
            char c = s.charAt(i);
            if (c == '{') {
                i++;
                Map<String, Object> m = new LinkedHashMap<>();
                ws();
                if (s.charAt(i) == '}') { i++; return m; }
                while (true) {
                    ws(); String k = str(); ws(); i++; // ':'
                    m.put(k, val()); ws();
                    if (s.charAt(i++) == '}') return m;
                }
            }
            if (c == '[') {
                i++;
                List<Object> l = new ArrayList<>();
                ws();
                if (s.charAt(i) == ']') { i++; return l; }
                while (true) {
                    l.add(val()); ws();
                    if (s.charAt(i++) == ']') return l;
                }
            }
            if (c == '"') return str();
            if (s.startsWith("true", i)) { i += 4; return true; }
            if (s.startsWith("false", i)) { i += 5; return false; }
            if (s.startsWith("null", i)) { i += 4; return null; }
            int st = i;
            while (i < s.length() && "+-0123456789.eE".indexOf(s.charAt(i)) >= 0) i++;
            return new BigDecimal(s.substring(st, i));
        }

        private String str() {
            StringBuilder b = new StringBuilder();
            i++;
            while (true) {
                char c = s.charAt(i++);
                if (c == '"') return b.toString();
                if (c == '\\') {
                    char e = s.charAt(i++);
                    switch (e) {
                        case 'n' -> b.append('\n');
                        case 't' -> b.append('\t');
                        case 'r' -> b.append('\r');
                        case 'b' -> b.append('\b');
                        case 'f' -> b.append('\f');
                        case 'u' -> { b.append((char) Integer.parseInt(s.substring(i, i + 4), 16)); i += 4; }
                        default -> b.append(e);
                    }
                } else b.append(c);
            }
        }

        private void ws() { while (i < s.length() && Character.isWhitespace(s.charAt(i))) i++; }

        static String write(Object v) {
            StringBuilder b = new StringBuilder();
            w(b, v);
            return b.toString();
        }

        private static void w(StringBuilder b, Object v) {
            if (v == null) b.append("null");
            else if (v instanceof String str) q(b, str);
            else if (v instanceof Boolean) b.append(v);
            else if (v instanceof BigDecimal d) b.append(d.toPlainString());
            else if (v instanceof Double d) { if (d.isNaN() || d.isInfinite()) q(b, d.toString()); else b.append(BigDecimal.valueOf(d).toPlainString()); }
            else if (v instanceof Float f) { if (f.isNaN() || f.isInfinite()) q(b, f.toString()); else b.append(new BigDecimal(Float.toString(f)).toPlainString()); }
            else if (v instanceof Number) b.append(v);
            else if (v instanceof Map<?, ?> m) {
                b.append('{');
                boolean first = true;
                for (Map.Entry<?, ?> e : m.entrySet()) { if (!first) b.append(','); first = false; q(b, String.valueOf(e.getKey())); b.append(':'); w(b, e.getValue()); }
                b.append('}');
            } else if (v instanceof Collection<?> c) {
                b.append('[');
                boolean first = true;
                for (Object o : c) { if (!first) b.append(','); first = false; w(b, o); }
                b.append(']');
            } else q(b, String.valueOf(v));
        }

        private static void q(StringBuilder b, String s) {
            b.append('"');
            for (char c : s.toCharArray()) {
                switch (c) {
                    case '"' -> b.append("\\\"");
                    case '\\' -> b.append("\\\\");
                    case '\n' -> b.append("\\n");
                    case '\r' -> b.append("\\r");
                    case '\t' -> b.append("\\t");
                    default -> { if (c < 0x20) b.append(String.format("\\u%04x", (int) c)); else b.append(c); }
                }
            }
            b.append('"');
        }
    }
}
