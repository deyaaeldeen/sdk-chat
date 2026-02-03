///usr/bin/env jbang "$0" "$@" ; exit $?
//DEPS com.github.javaparser:javaparser-core:3.26.3
//DEPS com.google.code.gson:gson:2.10.1
//NATIVE_OPTIONS -O3 --no-fallback --initialize-at-build-time=com.github.javaparser --initialize-at-run-time=

import com.github.javaparser.*;
import com.github.javaparser.ast.*;
import com.github.javaparser.ast.body.*;
import com.github.javaparser.ast.expr.*;
import com.github.javaparser.ast.stmt.*;
import com.github.javaparser.ast.type.*;
import com.github.javaparser.ast.comments.*;
import com.github.javaparser.ast.visitor.*;
import com.github.javaparser.ast.nodeTypes.*;
import com.google.gson.*;
import java.io.*;
import java.nio.file.*;
import java.util.*;
import java.util.stream.*;

public class ExtractApi {
    private static final Gson gson = new GsonBuilder().disableHtmlEscaping().create();
    private static final Gson prettyGson = new GsonBuilder().disableHtmlEscaping().setPrettyPrinting().create();

    public static void main(String[] args) throws Exception {
        if (args.length < 1) {
            System.err.println("Usage: jbang ExtractApi.java <path> [--json] [--stub] [--pretty]");
            System.err.println("       jbang ExtractApi.java --usage <api_json_file> <samples_path>");
            System.exit(1);
        }

        // Handle --usage mode
        if (args[0].equals("--usage")) {
            if (args.length < 3) {
                System.err.println("Usage: jbang ExtractApi.java --usage <api_json_file> <samples_path>");
                System.exit(1);
            }
            analyzeUsage(Paths.get(args[1]), Paths.get(args[2]));
            return;
        }

        Path root = Paths.get(args[0]).toAbsolutePath();
        boolean outputJson = Arrays.asList(args).contains("--json");
        boolean outputStub = Arrays.asList(args).contains("--stub") || !outputJson;
        boolean pretty = Arrays.asList(args).contains("--pretty");

        Map<String, Object> api = extractPackage(root);

        if (outputJson) {
            System.out.println(pretty ? prettyGson.toJson(api) : gson.toJson(api));
        } else {
            System.out.println(formatStubs(api));
        }
    }

    // ===== Usage Analysis Mode =====
    static void analyzeUsage(Path apiJsonFile, Path samplesPath) throws Exception {
        // Load API index
        String apiJson = Files.readString(apiJsonFile);
        JsonObject apiIndex = gson.fromJson(apiJson, JsonObject.class);

        // Build map of client classes -> methods
        Map<String, Set<String>> clientMethods = new HashMap<>();
        JsonArray packages = apiIndex.getAsJsonArray("packages");
        if (packages != null) {
            for (JsonElement pkgEl : packages) {
                JsonObject pkg = pkgEl.getAsJsonObject();
                JsonArray classes = pkg.getAsJsonArray("classes");
                if (classes != null) {
                    for (JsonElement clsEl : classes) {
                        JsonObject cls = clsEl.getAsJsonObject();
                        String className = cls.get("name").getAsString();
                        if (className.endsWith("Client") || className.endsWith("AsyncClient")) {
                            Set<String> methods = new HashSet<>();
                            JsonArray methodsArr = cls.getAsJsonArray("methods");
                            if (methodsArr != null) {
                                for (JsonElement mEl : methodsArr) {
                                    methods.add(mEl.getAsJsonObject().get("name").getAsString());
                                }
                            }
                            if (!methods.isEmpty()) {
                                clientMethods.put(className, methods);
                            }
                        }
                    }
                }
            }
        }

        if (clientMethods.isEmpty()) {
            System.out.println(gson.toJson(Map.of("fileCount", 0, "covered", List.of(), "uncovered", List.of(), "patterns", List.of())));
            return;
        }

        // Find sample files
        List<Path> javaFiles = Files.walk(samplesPath)
            .filter(p -> p.toString().endsWith(".java"))
            .filter(p -> !p.toString().contains("/target/") && !p.toString().contains("\\target\\"))
            .collect(Collectors.toList());

        List<Map<String, Object>> covered = new ArrayList<>();
        Set<String> seenOps = new HashSet<>();
        Set<String> patterns = new HashSet<>();

        ParserConfiguration config = new ParserConfiguration();
        config.setLanguageLevel(ParserConfiguration.LanguageLevel.JAVA_17);
        JavaParser parser = new JavaParser(config);

        for (Path file : javaFiles) {
            try {
                ParseResult<CompilationUnit> result = parser.parse(file);
                if (!result.isSuccessful()) continue;

                CompilationUnit cu = result.getResult().orElse(null);
                if (cu == null) continue;

                String relPath = samplesPath.relativize(file).toString().replace('\\', '/');

                // Find method calls using AST
                cu.walk(com.github.javaparser.ast.expr.MethodCallExpr.class, call -> {
                    String methodName = call.getNameAsString();
                    String scope = call.getScope().map(s -> s.toString()).orElse("");

                    for (Map.Entry<String, Set<String>> entry : clientMethods.entrySet()) {
                        String clientName = entry.getKey();
                        Set<String> methods = entry.getValue();
                        String clientBase = clientName.replace("Client", "").replace("AsyncClient", "");

                        if ((scope.toLowerCase().contains(clientBase.toLowerCase()) || scope.toLowerCase().contains("client"))
                            && methods.contains(methodName)) {
                            String key = clientName + "." + methodName;
                            if (seenOps.add(key)) {
                                int line = call.getBegin().map(p -> p.line).orElse(0);
                                covered.add(Map.of("client", clientName, "method", methodName, "file", relPath, "line", line));
                            }
                        }
                    }
                });

                // Detect patterns using AST
                cu.walk(Node.class, node -> {
                    if (node instanceof com.github.javaparser.ast.stmt.TryStmt) patterns.add("error-handling");
                    if (node instanceof com.github.javaparser.ast.stmt.ForEachStmt) {
                        String typeStr = node.toString().toLowerCase();
                        if (typeStr.contains("page") || typeStr.contains("iterator")) patterns.add("pagination");
                    }
                    if (node instanceof com.github.javaparser.ast.expr.LambdaExpr) patterns.add("async-callback");
                });

                // Check for common patterns in code
                String code = Files.readString(file).toLowerCase();
                if (code.contains("credential") || code.contains("authenticate")) patterns.add("authentication");
                if (code.contains("stream") || code.contains("flux") || code.contains("mono")) patterns.add("streaming");
                if (code.contains("retry") || code.contains("exponential")) patterns.add("retry");

            } catch (Exception e) {
                // Skip problematic files
            }
        }

        // Build uncovered list
        List<Map<String, Object>> uncovered = new ArrayList<>();
        for (Map.Entry<String, Set<String>> entry : clientMethods.entrySet()) {
            String clientName = entry.getKey();
            for (String method : entry.getValue()) {
                String key = clientName + "." + method;
                if (!seenOps.contains(key)) {
                    uncovered.add(Map.of("client", clientName, "method", method, "sig", method + "(...)"));
                }
            }
        }

        Map<String, Object> result = Map.of(
            "fileCount", javaFiles.size(),
            "covered", covered,
            "uncovered", uncovered,
            "patterns", new ArrayList<>(patterns)
        );
        System.out.println(gson.toJson(result));
    }

    static Map<String, Object> extractPackage(Path root) throws Exception {
        String packageName = detectPackageName(root);
        List<Map<String, Object>> packages = new ArrayList<>();
        Map<String, Map<String, Object>> packageMap = new TreeMap<>();

        // Get root for relative path calculations
        String rootStr = root.toString().replace('\\', '/');
        if (!rootStr.endsWith("/")) rootStr += "/";
        final String finalRootStr = rootStr;

        List<Path> javaFiles = Files.walk(root)
            .filter(p -> p.toString().endsWith(".java"))
            .filter(p -> {
                // Get path relative to root for filtering
                String pathStr = p.toString().replace('\\', '/');
                String relativePath = pathStr.startsWith(finalRootStr)
                    ? pathStr.substring(finalRootStr.length())
                    : pathStr;
                // Skip test directories, build artifacts, and generated code
                if (relativePath.startsWith("src/test/") || relativePath.contains("/src/test/")) return false;
                if (relativePath.contains("/target/") || relativePath.startsWith("target/")) return false;
                if (relativePath.contains("/build/") || relativePath.startsWith("build/")) return false;
                if (relativePath.contains("/.gradle/") || relativePath.startsWith(".gradle/")) return false;
                if (relativePath.contains("/bin/") || relativePath.startsWith("bin/")) return false;
                if (relativePath.contains("/out/") || relativePath.startsWith("out/")) return false;
                return true;
            })
            .filter(p -> !p.getFileName().toString().startsWith("package-info"))
            .sorted()
            .collect(Collectors.toList());

        ParserConfiguration config = new ParserConfiguration();
        config.setLanguageLevel(ParserConfiguration.LanguageLevel.JAVA_17);
        JavaParser parser = new JavaParser(config);

        for (Path file : javaFiles) {
            try {
                ParseResult<CompilationUnit> result = parser.parse(file);
                if (!result.isSuccessful()) continue;

                CompilationUnit cu = result.getResult().orElse(null);
                if (cu == null) continue;

                String pkg = cu.getPackageDeclaration()
                    .map(pd -> pd.getNameAsString())
                    .orElse("");

                // Skip implementation packages
                if (pkg.contains(".implementation") || pkg.contains(".internal")) continue;

                packageMap.putIfAbsent(pkg, new LinkedHashMap<>());
                Map<String, Object> pkgInfo = packageMap.get(pkg);
                pkgInfo.put("name", pkg);

                for (TypeDeclaration<?> type : cu.getTypes()) {
                    extractType(type, pkgInfo);
                }
            } catch (Exception e) {
                // Skip unparseable files
            }
        }

        packages.addAll(packageMap.values());

        Map<String, Object> result = new LinkedHashMap<>();
        result.put("package", packageName);
        result.put("packages", packages);
        return result;
    }

    static void extractType(TypeDeclaration<?> type, Map<String, Object> pkgInfo) {
        if (!isPublic(type)) return;

        if (type instanceof ClassOrInterfaceDeclaration) {
            ClassOrInterfaceDeclaration cid = (ClassOrInterfaceDeclaration) type;
            Map<String, Object> info = extractClassOrInterface(cid);

            String key = cid.isInterface() ? "interfaces" : "classes";
            @SuppressWarnings("unchecked")
            List<Map<String, Object>> list = (List<Map<String, Object>>)
                pkgInfo.computeIfAbsent(key, k -> new ArrayList<>());
            list.add(info);
        } else if (type instanceof EnumDeclaration) {
            Map<String, Object> info = extractEnum((EnumDeclaration) type);
            @SuppressWarnings("unchecked")
            List<Map<String, Object>> list = (List<Map<String, Object>>)
                pkgInfo.computeIfAbsent("enums", k -> new ArrayList<>());
            list.add(info);
        }
    }

    static Map<String, Object> extractClassOrInterface(ClassOrInterfaceDeclaration cid) {
        Map<String, Object> info = new LinkedHashMap<>();
        info.put("name", cid.getNameAsString());
        boolean isInterface = cid.isInterface();

        List<String> mods = getModifiers(cid);
        if (!mods.isEmpty()) info.put("modifiers", mods);

        if (!cid.getTypeParameters().isEmpty()) {
            info.put("typeParams", cid.getTypeParameters().stream()
                .map(tp -> tp.toString())
                .collect(Collectors.joining(", ")));
        }

        if (!cid.getExtendedTypes().isEmpty()) {
            info.put("extends", cid.getExtendedTypes().get(0).toString());
        }

        if (!cid.getImplementedTypes().isEmpty()) {
            info.put("implements", cid.getImplementedTypes().stream()
                .map(t -> t.toString())
                .collect(Collectors.toList()));
        }

        getDocString(cid).ifPresent(doc -> info.put("doc", doc));

        // Constructors (not applicable for interfaces)
        if (!isInterface) {
            List<Map<String, Object>> constructors = new ArrayList<>();
            for (ConstructorDeclaration cd : cid.getConstructors()) {
                if (!isPublicOrProtected(cd)) continue;
                constructors.add(extractMethod(cd));
            }
            if (!constructors.isEmpty()) info.put("constructors", constructors);
        }

        // Methods - for interfaces, all methods are implicitly public
        List<Map<String, Object>> methods = new ArrayList<>();
        for (MethodDeclaration md : cid.getMethods()) {
            // Interface methods are implicitly public
            if (!isInterface && !isPublicOrProtected(md)) continue;
            methods.add(extractMethod(md));
        }
        if (!methods.isEmpty()) info.put("methods", methods);

        // Fields
        List<Map<String, Object>> fields = new ArrayList<>();
        for (FieldDeclaration fd : cid.getFields()) {
            if (!isPublicOrProtected(fd)) continue;
            for (VariableDeclarator vd : fd.getVariables()) {
                fields.add(extractField(fd, vd));
            }
        }
        if (!fields.isEmpty()) info.put("fields", fields);

        return info;
    }

    static Map<String, Object> extractEnum(EnumDeclaration ed) {
        Map<String, Object> info = new LinkedHashMap<>();
        info.put("name", ed.getNameAsString());

        getDocString(ed).ifPresent(doc -> info.put("doc", doc));

        List<String> values = ed.getEntries().stream()
            .map(e -> e.getNameAsString())
            .collect(Collectors.toList());
        if (!values.isEmpty()) info.put("values", values);

        List<Map<String, Object>> methods = new ArrayList<>();
        for (MethodDeclaration md : ed.getMethods()) {
            if (!isPublicOrProtected(md)) continue;
            methods.add(extractMethod(md));
        }
        if (!methods.isEmpty()) info.put("methods", methods);

        return info;
    }

    static Map<String, Object> extractMethod(CallableDeclaration<?> cd) {
        Map<String, Object> info = new LinkedHashMap<>();
        info.put("name", cd.getNameAsString());

        List<String> mods = getModifiers(cd);
        if (!mods.isEmpty()) info.put("modifiers", mods);

        if (!cd.getTypeParameters().isEmpty()) {
            info.put("typeParams", cd.getTypeParameters().stream()
                .map(tp -> tp.toString())
                .collect(Collectors.joining(", ")));
        }

        String sig = cd.getParameters().stream()
            .map(p -> p.getType().toString() + " " + p.getNameAsString())
            .collect(Collectors.joining(", "));
        info.put("sig", sig);

        if (cd instanceof MethodDeclaration) {
            info.put("ret", ((MethodDeclaration) cd).getType().toString());
        }

        if (!cd.getThrownExceptions().isEmpty()) {
            info.put("throws", cd.getThrownExceptions().stream()
                .map(t -> t.toString())
                .collect(Collectors.toList()));
        }

        getDocString(cd).ifPresent(doc -> info.put("doc", doc));

        return info;
    }

    static Map<String, Object> extractField(FieldDeclaration fd, VariableDeclarator vd) {
        Map<String, Object> info = new LinkedHashMap<>();
        info.put("name", vd.getNameAsString());
        info.put("type", vd.getType().toString());

        List<String> mods = getModifiers(fd);
        if (!mods.isEmpty()) info.put("modifiers", mods);

        if (fd.isFinal() && vd.getInitializer().isPresent()) {
            String val = vd.getInitializer().get().toString();
            if (val.length() < 100) info.put("value", val);
        }

        getDocString(fd).ifPresent(doc -> info.put("doc", doc));

        return info;
    }

    static List<String> getModifiers(NodeWithModifiers<?> node) {
        List<String> mods = new ArrayList<>();
        if (node.hasModifier(com.github.javaparser.ast.Modifier.Keyword.PUBLIC)) mods.add("public");
        if (node.hasModifier(com.github.javaparser.ast.Modifier.Keyword.PROTECTED)) mods.add("protected");
        if (node.hasModifier(com.github.javaparser.ast.Modifier.Keyword.STATIC)) mods.add("static");
        if (node.hasModifier(com.github.javaparser.ast.Modifier.Keyword.FINAL)) mods.add("final");
        if (node.hasModifier(com.github.javaparser.ast.Modifier.Keyword.ABSTRACT)) mods.add("abstract");
        return mods;
    }

    static boolean isPublic(TypeDeclaration<?> type) {
        return type.isPublic();
    }

    static boolean isPublicOrProtected(CallableDeclaration<?> cd) {
        return cd.isPublic() || cd.isProtected();
    }

    static boolean isPublicOrProtected(FieldDeclaration fd) {
        return fd.isPublic() || fd.isProtected();
    }

    static Optional<String> getDocString(NodeWithJavadoc<?> node) {
        return node.getJavadoc().map(jd -> {
            String desc = jd.getDescription().toText().trim();
            String firstLine = desc.split("\n")[0].trim();
            if (firstLine.length() > 120) firstLine = firstLine.substring(0, 117) + "...";
            return firstLine.isEmpty() ? null : firstLine;
        });
    }

    static String detectPackageName(Path root) throws Exception {
        // Check pom.xml
        Path pom = root.resolve("pom.xml");
        if (Files.exists(pom)) {
            String content = Files.readString(pom);
            int idx = content.indexOf("<artifactId>");
            if (idx > 0) {
                int end = content.indexOf("</artifactId>", idx);
                if (end > idx) {
                    return content.substring(idx + 12, end).trim();
                }
            }
        }
        return root.getFileName().toString();
    }

    static String formatStubs(Map<String, Object> api) {
        StringBuilder sb = new StringBuilder();
        sb.append("// ").append(api.get("package")).append(" - Public API Surface\n");
        sb.append("// Extracted by ApiExtractor.Java\n\n");

        @SuppressWarnings("unchecked")
        List<Map<String, Object>> packages = (List<Map<String, Object>>) api.get("packages");

        for (Map<String, Object> pkg : packages) {
            sb.append("package ").append(pkg.get("name")).append(";\n\n");

            formatTypes(sb, pkg, "interfaces", "interface");
            formatTypes(sb, pkg, "classes", "class");
            formatEnums(sb, pkg);
        }

        return sb.toString();
    }

    @SuppressWarnings("unchecked")
    static void formatTypes(StringBuilder sb, Map<String, Object> pkg, String key, String keyword) {
        List<Map<String, Object>> types = (List<Map<String, Object>>) pkg.get(key);
        if (types == null) return;

        for (Map<String, Object> type : types) {
            // Doc
            if (type.get("doc") != null) {
                sb.append("/** ").append(type.get("doc")).append(" */\n");
            }

            // Declaration
            List<String> mods = (List<String>) type.get("modifiers");
            if (mods != null) sb.append(String.join(" ", mods)).append(" ");
            sb.append(keyword).append(" ").append(type.get("name"));
            if (type.get("typeParams") != null) sb.append("<").append(type.get("typeParams")).append(">");
            if (type.get("extends") != null) sb.append(" extends ").append(type.get("extends"));
            List<String> impl = (List<String>) type.get("implements");
            if (impl != null) sb.append(" implements ").append(String.join(", ", impl));
            sb.append(" {\n");

            // Fields
            List<Map<String, Object>> fields = (List<Map<String, Object>>) type.get("fields");
            if (fields != null) {
                for (Map<String, Object> f : fields) {
                    sb.append("    ");
                    List<String> fm = (List<String>) f.get("modifiers");
                    if (fm != null) sb.append(String.join(" ", fm)).append(" ");
                    sb.append(f.get("type")).append(" ").append(f.get("name"));
                    if (f.get("value") != null) sb.append(" = ").append(f.get("value"));
                    sb.append(";\n");
                }
            }

            // Constructors
            List<Map<String, Object>> ctors = (List<Map<String, Object>>) type.get("constructors");
            if (ctors != null) {
                for (Map<String, Object> c : ctors) {
                    formatMethod(sb, type.get("name").toString(), c, true);
                }
            }

            // Methods
            List<Map<String, Object>> methods = (List<Map<String, Object>>) type.get("methods");
            if (methods != null) {
                for (Map<String, Object> m : methods) {
                    formatMethod(sb, m.get("name").toString(), m, false);
                }
            }

            sb.append("}\n\n");
        }
    }

    @SuppressWarnings("unchecked")
    static void formatEnums(StringBuilder sb, Map<String, Object> pkg) {
        List<Map<String, Object>> enums = (List<Map<String, Object>>) pkg.get("enums");
        if (enums == null) return;

        for (Map<String, Object> e : enums) {
            if (e.get("doc") != null) {
                sb.append("/** ").append(e.get("doc")).append(" */\n");
            }
            sb.append("public enum ").append(e.get("name")).append(" {\n");

            List<String> values = (List<String>) e.get("values");
            if (values != null) {
                sb.append("    ").append(String.join(", ", values)).append(";\n");
            }

            List<Map<String, Object>> methods = (List<Map<String, Object>>) e.get("methods");
            if (methods != null) {
                for (Map<String, Object> m : methods) {
                    formatMethod(sb, m.get("name").toString(), m, false);
                }
            }

            sb.append("}\n\n");
        }
    }

    @SuppressWarnings("unchecked")
    static void formatMethod(StringBuilder sb, String name, Map<String, Object> m, boolean isCtor) {
        if (m.get("doc") != null) {
            sb.append("    /** ").append(m.get("doc")).append(" */\n");
        }
        sb.append("    ");
        List<String> mods = (List<String>) m.get("modifiers");
        if (mods != null) sb.append(String.join(" ", mods)).append(" ");
        if (m.get("typeParams") != null) sb.append("<").append(m.get("typeParams")).append("> ");
        if (!isCtor && m.get("ret") != null) sb.append(m.get("ret")).append(" ");
        sb.append(name).append("(").append(m.get("sig")).append(")");
        List<String> thr = (List<String>) m.get("throws");
        if (thr != null) sb.append(" throws ").append(String.join(", ", thr));
        sb.append(";\n");
    }
}
