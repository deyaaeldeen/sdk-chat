#!/usr/bin/env python3
"""
Extract public API surface from Python packages.
Outputs JSON with classes, methods, functions, and their signatures.
"""

import ast
import json
import sys
import os
from pathlib import Path
from typing import Any

def get_docstring(node: ast.AST) -> str | None:
    """Extract first line of docstring."""
    doc = ast.get_docstring(node)
    if not doc:
        return None
    first_line = doc.split('\n')[0].strip()
    return first_line[:150] + '...' if len(first_line) > 150 else first_line

def format_annotation(ann: ast.expr | None) -> str | None:
    """Convert annotation AST to string."""
    if ann is None:
        return None
    return ast.unparse(ann)

def extract_function(node: ast.FunctionDef | ast.AsyncFunctionDef) -> dict[str, Any]:
    """Extract function/method info."""
    args = []
    for arg in node.args.args:
        arg_str = arg.arg
        if arg.annotation:
            arg_str += f": {ast.unparse(arg.annotation)}"
        args.append(arg_str)

    # Handle *args, **kwargs
    if node.args.vararg:
        va = node.args.vararg
        va_str = f"*{va.arg}"
        if va.annotation:
            va_str += f": {ast.unparse(va.annotation)}"
        args.append(va_str)
    if node.args.kwarg:
        kw = node.args.kwarg
        kw_str = f"**{kw.arg}"
        if kw.annotation:
            kw_str += f": {ast.unparse(kw.annotation)}"
        args.append(kw_str)

    sig = ", ".join(args)

    result: dict[str, Any] = {
        "name": node.name,
        "sig": sig,
    }

    ret = format_annotation(node.returns)
    if ret:
        result["ret"] = ret

    doc = get_docstring(node)
    if doc:
        result["doc"] = doc

    if isinstance(node, ast.AsyncFunctionDef):
        result["async"] = True

    # Check decorators
    for dec in node.decorator_list:
        if isinstance(dec, ast.Name):
            if dec.id == "classmethod":
                result["classmethod"] = True
            elif dec.id == "staticmethod":
                result["staticmethod"] = True
            elif dec.id == "property":
                result["property"] = True

    return result

def extract_class(node: ast.ClassDef) -> dict[str, Any]:
    """Extract class info."""
    bases = [ast.unparse(b) for b in node.bases if isinstance(b, (ast.Name, ast.Attribute, ast.Subscript))]

    result: dict[str, Any] = {
        "name": node.name,
    }

    if bases:
        result["base"] = ", ".join(bases)

    doc = get_docstring(node)
    if doc:
        result["doc"] = doc

    methods = []
    properties = []

    for item in node.body:
        if isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef)):
            # Skip private (single underscore) but keep dunder methods
            if item.name.startswith('_') and not item.name.startswith('__'):
                continue
            # Skip private dunders (double underscore not ending with double)
            if item.name.startswith('__') and not item.name.endswith('__'):
                continue

            func_info = extract_function(item)
            if func_info.get("property"):
                del func_info["property"]
                prop_type = func_info.get("ret")
                if not prop_type:
                    sig = func_info.get("sig", "")
                    prop_type = sig.split(" -> ")[-1] if " -> " in sig else None
                properties.append({"name": func_info["name"], "type": prop_type, "doc": func_info.get("doc")})
            else:
                methods.append(func_info)

    if methods:
        result["methods"] = methods
    if properties:
        result["properties"] = properties

    return result

def extract_module(file_path: Path, root_path: Path) -> dict[str, Any]:
    """Extract module info."""
    try:
        code = file_path.read_text(encoding='utf-8')
        tree = ast.parse(code)
    except (SyntaxError, UnicodeDecodeError):
        return {}

    # Calculate module name
    rel_path = file_path.relative_to(root_path)
    module_name = str(rel_path).replace('/', '.').replace('\\', '.').replace('.py', '')
    if module_name.endswith('.__init__'):
        module_name = module_name[:-9]

    classes = []
    functions = []

    for node in ast.iter_child_nodes(tree):
        if isinstance(node, ast.ClassDef):
            if not node.name.startswith('_'):
                classes.append(extract_class(node))
        elif isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
            if not node.name.startswith('_'):
                functions.append(extract_function(node))

    if not classes and not functions:
        return {}

    result: dict[str, Any] = {"name": module_name}
    if classes:
        result["classes"] = classes
    if functions:
        result["functions"] = functions

    return result

def find_package_name(root_path: Path) -> str:
    """Detect package name."""
    # Check pyproject.toml
    pyproject = root_path / "pyproject.toml"
    if pyproject.exists():
        import re
        content = pyproject.read_text()
        match = re.search(r'name\s*=\s*["\']([^"\']+)["\']', content)
        if match:
            return match.group(1)

    # Check setup.py
    setup_py = root_path / "setup.py"
    if setup_py.exists():
        import re
        content = setup_py.read_text()
        match = re.search(r'name\s*=\s*["\']([^"\']+)["\']', content)
        if match:
            return match.group(1)

    # Find first package with __init__.py
    for init in sorted(root_path.rglob("__init__.py"), key=lambda p: len(str(p))):
        if "test" not in str(init).lower() and "_generated" not in str(init):
            return init.parent.name

    return root_path.name

def extract_package(root_path: Path) -> dict[str, Any]:
    """Extract entire package API."""
    package_name = find_package_name(root_path)

    modules = []
    for py_file in sorted(root_path.rglob("*.py")):
        # Skip tests, caches, venvs, and build artifacts
        path_str = str(py_file)
        # Allow TestFixtures (used for testing extractors themselves)
        if 'TestFixtures' not in path_str and any(skip in path_str for skip in [
            '__pycache__',
            'venv', '.venv',
            'test_', '_test.py', '/tests/', '\\tests\\',
            '.tox', '.nox',
            'site-packages',
            '/build/', '\\build\\',
            '/dist/', '\\dist\\',
            '.eggs', '.egg-info',
            'node_modules'
        ]):
            continue
        # Skip private modules except __init__
        if py_file.name.startswith('_') and py_file.name != '__init__.py':
            continue

        module = extract_module(py_file, root_path)
        if module:
            modules.append(module)

    return {
        "package": package_name,
        "modules": modules
    }

def format_python_stubs(api: dict[str, Any]) -> str:
    """Format as Python stub syntax."""
    lines = [
        f"# {api['package']} - Public API Surface",
        f"# Extracted by ApiExtractor.Python",
        "",
    ]

    for module in api.get("modules", []):
        lines.append(f"# Module: {module['name']}")
        lines.append("")

        for func in module.get("functions", []):
            if func.get("doc"):
                lines.append(f'"""{func["doc"]}"""')
            async_prefix = "async " if func.get("async") else ""
            ret_type = f' -> {func["ret"]}' if func.get("ret") else ""
            lines.append(f'{async_prefix}def {func["name"]}({func["sig"]}){ret_type}: ...')
            lines.append("")

        for cls in module.get("classes", []):
            base = f'({cls["base"]})' if cls.get("base") else ""
            lines.append(f'class {cls["name"]}{base}:')
            if cls.get("doc"):
                lines.append(f'    """{cls["doc"]}"""')

            for prop in cls.get("properties", []):
                type_hint = f": {prop['type']}" if prop.get("type") else ""
                lines.append(f'    {prop["name"]}{type_hint}')

            for method in cls.get("methods", []):
                if method.get("doc"):
                    lines.append(f'    """{method["doc"]}"""')
                decorators = []
                if method.get("classmethod"):
                    decorators.append("@classmethod")
                if method.get("staticmethod"):
                    decorators.append("@staticmethod")
                for dec in decorators:
                    lines.append(f'    {dec}')
                async_prefix = "async " if method.get("async") else ""
                ret_type = f' -> {method["ret"]}' if method.get("ret") else ""
                lines.append(f'    {async_prefix}def {method["name"]}({method["sig"]}){ret_type}: ...')

            if not cls.get("methods") and not cls.get("properties"):
                lines.append("    ...")
            lines.append("")

    return "\n".join(lines)


def analyze_usage(samples_path: Path, api: dict[str, Any]) -> dict[str, Any]:
    """
    Analyze sample files to find which API operations are used.
    Uses AST to accurately find method calls.
    """
    # Build set of client methods from API (include subclients)
    all_classes: list[dict[str, Any]] = []
    all_type_names: set[str] = set()
    for module in api.get("modules", []):
        for cls in module.get("classes", []):
            all_classes.append(cls)
            all_type_names.add(cls.get("name", "").split("[")[0])

    references: dict[str, set[str]] = {}
    for cls in all_classes:
        name = cls.get("name", "").split("[")[0]
        references[name] = get_referenced_types(cls, all_type_names)

    referenced_by: dict[str, int] = {}
    for refs in references.values():
        for ref in refs:
            referenced_by[ref] = referenced_by.get(ref, 0) + 1

    operation_types: set[str] = set()
    for cls in all_classes:
        if cls.get("methods"):
            operation_types.add(cls.get("name", "").split("[")[0])

    # Client classes are always roots - they're SDK entry points even if referenced by options/builders
    def is_client_class(name: str) -> bool:
        return name.endswith("Client") or name.endswith("AsyncClient")

    root_classes = [
        cls for cls in all_classes
        if is_client_class(cls.get("name", "").split("[")[0]) or (
            cls.get("name", "").split("[")[0] not in referenced_by and (
                cls.get("methods") or
                any(ref in operation_types for ref in references.get(cls.get("name", "").split("[")[0], set()))
            )
        )
    ]

    if not root_classes:
        root_classes = [
            cls for cls in all_classes
            if cls.get("methods") or
               any(ref in operation_types for ref in references.get(cls.get("name", "").split("[")[0], set()))
        ]

    derived_by_base: dict[str, list[dict[str, Any]]] = {}
    for cls in all_classes:
        base_name = cls.get("base")
        if not base_name:
            continue
        base_key = base_name.split("[")[0]
        derived_by_base.setdefault(base_key, []).append(cls)

    reachable: set[str] = set()
    queue: list[str] = []

    for cls in root_classes:
        name = cls.get("name", "").split("[")[0]
        if name not in reachable:
            reachable.add(name)
            queue.append(name)

    while queue:
        current = queue.pop(0)
        current_cls = next(
            (cls for cls in all_classes if cls.get("name", "").split("[")[0] == current),
            None,
        )
        if not current_cls:
            continue

        for ref in references.get(current, set()):
            if ref not in reachable:
                reachable.add(ref)
                queue.append(ref)

        for child in derived_by_base.get(current, []):
            child_name = child.get("name", "").split("[")[0]
            if child_name and child_name not in reachable:
                reachable.add(child_name)
                queue.append(child_name)

    usage_classes = [
        cls for cls in all_classes
        if cls.get("name", "").split("[")[0] in reachable and cls.get("methods")
    ]

    client_methods: dict[str, set[str]] = {}
    for cls in usage_classes:
        name = cls.get("name", "")
        methods = {m["name"] for m in cls.get("methods", [])}
        if methods:
            client_methods[name] = methods

    if not client_methods:
        return {"fileCount": 0, "covered": [], "uncovered": [], "patterns": []}

    covered: list[dict[str, Any]] = []
    seen_ops: set[str] = set()
    patterns: set[str] = set()
    file_count = 0

    # Find all Python files in samples
    for py_file in samples_path.rglob("*.py"):
        path_str = str(py_file)
        if any(skip in path_str for skip in ['__pycache__', 'venv', '.venv', 'test_', '_test.py']):
            continue

        file_count += 1
        try:
            code = py_file.read_text(encoding='utf-8')
            tree = ast.parse(code)
        except (SyntaxError, UnicodeDecodeError):
            continue

        rel_path = str(py_file.relative_to(samples_path))

        # Use AST to find method calls
        for node in ast.walk(tree):
            if isinstance(node, ast.Call):
                method_name, line = extract_call_info(node)
                if method_name:
                    for client_name, methods in client_methods.items():
                        if method_name in methods or method_name.rstrip('_async') in methods:
                            key = f"{client_name}.{method_name}"
                            if key not in seen_ops:
                                seen_ops.add(key)
                                covered.append({
                                    "client": client_name,
                                    "method": method_name,
                                    "file": rel_path,
                                    "line": getattr(node, 'lineno', 0)
                                })

        # Detect patterns using AST
        detect_patterns_ast(tree, code, patterns)

    # Build uncovered list
    uncovered: list[dict[str, str]] = []
    for client_name, methods in client_methods.items():
        for method in methods:
            key = f"{client_name}.{method}"
            if key not in seen_ops:
                uncovered.append({
                    "client": client_name,
                    "method": method,
                    "sig": f"{method}(...)"
                })

    return {
        "fileCount": file_count,
        "covered": covered,
        "uncovered": uncovered,
        "patterns": sorted(patterns)
    }


def get_referenced_types(cls: dict[str, Any], all_type_names: set[str]) -> set[str]:
    refs: set[str] = set()

    base = cls.get("base")
    if base:
        base_name = base.split("[")[0]
        if base_name in all_type_names:
            refs.add(base_name)

    for method in cls.get("methods", []) or []:
        sig = method.get("sig", "")
        for type_name in all_type_names:
            if type_name in sig:
                refs.add(type_name)

    for prop in cls.get("properties", []) or []:
        ptype = prop.get("type") or ""
        for type_name in all_type_names:
            if type_name in ptype:
                refs.add(type_name)

    return refs


def extract_call_info(node: ast.Call) -> tuple[str | None, int]:
    """Extract method name from a Call node using AST."""
    if isinstance(node.func, ast.Attribute):
        return node.func.attr, getattr(node, 'lineno', 0)
    return None, 0


def detect_patterns_ast(tree: ast.AST, code: str, patterns: set[str]) -> None:
    """Detect usage patterns using AST analysis."""
    code_lower = code.lower()

    # Check for async/await
    if any(isinstance(node, ast.Await) for node in ast.walk(tree)):
        patterns.add("async")

    # Check for error handling
    if any(isinstance(node, ast.Try) for node in ast.walk(tree)):
        patterns.add("error-handling")

    # Keyword-based patterns
    if any(keyword in code_lower for keyword in ["credential", "authenticate", "token"]):
        patterns.add("authentication")
    if any(keyword in code_lower for keyword in ["stream", "async", "await"]):
        patterns.add("streaming")
    if any(keyword in code_lower for keyword in ["retry", "backoff", "timeout"]):
        patterns.add("retry")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python extract_api.py <path> [--json] [--stub] [--usage <api_json> <samples_path>]", file=sys.stderr)
        sys.exit(1)

    # Check for usage analysis mode
    if "--usage" in sys.argv:
        usage_idx = sys.argv.index("--usage")
        if len(sys.argv) < usage_idx + 3:
            print("Usage: --usage requires <api_json_path> <samples_path>", file=sys.stderr)
            sys.exit(1)
        api_json_path = Path(sys.argv[usage_idx + 1])
        samples_path = Path(sys.argv[usage_idx + 2])

        # Load API index
        with open(api_json_path, 'r') as f:
            api = json.load(f)

        # Analyze usage
        usage = analyze_usage(samples_path, api)
        print(json.dumps(usage, indent=2))
        sys.exit(0)

    root = Path(sys.argv[1]).resolve()
    output_json = "--json" in sys.argv
    output_stub = "--stub" in sys.argv or not output_json

    api = extract_package(root)

    if output_json:
        print(json.dumps(api, indent=2))
    elif output_stub:
        print(format_python_stubs(api))
