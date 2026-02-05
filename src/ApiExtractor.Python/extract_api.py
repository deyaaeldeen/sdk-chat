#!/usr/bin/env python3
"""
Extract public API surface from Python packages.
Outputs JSON with classes, methods, functions, and their signatures.
"""

import ast
import json
import sys
import os
import re
from pathlib import Path
from typing import Any


# =============================================================================
# Builtin Type Detection
# =============================================================================

# Comprehensive set of Python builtin types (from builtins, typing, collections.abc, etc.)
PYTHON_BUILTINS = frozenset({
    # Python builtins
    "int", "float", "str", "bool", "bytes", "bytearray", "complex",
    "list", "dict", "set", "frozenset", "tuple",
    "None", "NoneType", "type", "object", "super",
    "range", "slice", "memoryview",
    "property", "classmethod", "staticmethod",
    "Exception", "BaseException", "StopIteration", "GeneratorExit",
    "SystemExit", "KeyboardInterrupt", "ImportError", "ModuleNotFoundError",
    "OSError", "IOError", "EnvironmentError", "EOFError",
    "RuntimeError", "RecursionError", "NotImplementedError",
    "NameError", "UnboundLocalError", "AttributeError", "SyntaxError",
    "IndentationError", "TabError", "TypeError", "ValueError",
    "UnicodeError", "UnicodeEncodeError", "UnicodeDecodeError", "UnicodeTranslateError",
    "AssertionError", "IndexError", "KeyError", "OverflowError", "ZeroDivisionError",
    "FloatingPointError", "ArithmeticError", "LookupError",
    "ConnectionError", "BrokenPipeError", "ConnectionAbortedError",
    "ConnectionRefusedError", "ConnectionResetError",
    "FileExistsError", "FileNotFoundError", "InterruptedError",
    "IsADirectoryError", "NotADirectoryError", "PermissionError",
    "ProcessLookupError", "TimeoutError", "BlockingIOError", "ChildProcessError",
    "Warning", "UserWarning", "DeprecationWarning", "PendingDeprecationWarning",
    "SyntaxWarning", "RuntimeWarning", "FutureWarning", "ImportWarning",
    "UnicodeWarning", "BytesWarning", "ResourceWarning",
    
    # typing module
    "Any", "Union", "Optional", "List", "Dict", "Set", "FrozenSet", "Tuple",
    "Type", "Callable", "Iterable", "Iterator", "Generator", "AsyncGenerator",
    "Sequence", "MutableSequence", "Mapping", "MutableMapping",
    "Collection", "Reversible", "Container", "Hashable", "Sized",
    "Awaitable", "Coroutine", "AsyncIterable", "AsyncIterator",
    "ContextManager", "AsyncContextManager",
    "Pattern", "Match", "IO", "TextIO", "BinaryIO",
    "NoReturn", "Never", "Self", "LiteralString", "TypeAlias",
    "Final", "Literal", "ClassVar", "TypeVar", "TypeVarTuple", "ParamSpec",
    "Generic", "Protocol", "Annotated", "TypedDict", "NamedTuple",
    "NewType", "cast", "overload", "final", "dataclass_transform",
    "Concatenate", "TypeGuard", "Required", "NotRequired", "Unpack",
    
    # collections.abc
    "ABC", "ABCMeta", "abstractmethod", "abstractproperty",
    "MappingView", "KeysView", "ItemsView", "ValuesView",
    "ByteString", "Buffer",
    
    # io module  
    "IOBase", "RawIOBase", "BufferedIOBase", "TextIOBase",
    "FileIO", "BytesIO", "StringIO", "BufferedReader", "BufferedWriter",
    "BufferedRandom", "BufferedRWPair", "TextIOWrapper",
    
    # pathlib
    "Path", "PurePath", "PurePosixPath", "PureWindowsPath",
    "PosixPath", "WindowsPath",
    
    # datetime
    "date", "time", "datetime", "timedelta", "timezone", "tzinfo",
    
    # uuid
    "UUID",
    
    # decimal
    "Decimal",
    
    # fractions
    "Fraction",
    
    # enum
    "Enum", "IntEnum", "Flag", "IntFlag", "auto", "unique", "StrEnum",
    
    # dataclasses
    "dataclass", "field", "fields",
    
    # re
    "RegexFlag",
    
    # logging
    "Logger", "Handler", "Formatter", "Filter", "LogRecord",
    
    # concurrent.futures
    "Future", "Executor", "ThreadPoolExecutor", "ProcessPoolExecutor",
    
    # asyncio
    "Task", "Event", "Lock", "Semaphore", "BoundedSemaphore",
    "Condition", "Queue", "LifoQueue", "PriorityQueue",
    "StreamReader", "StreamWriter",
})

# Packages that are part of Python stdlib (not external dependencies)
PYTHON_STDLIB_PACKAGES = frozenset({
    "abc", "aifc", "argparse", "array", "ast", "asynchat", "asyncio", "asyncore",
    "atexit", "audioop", "base64", "bdb", "binascii", "binhex", "bisect",
    "builtins", "bz2", "calendar", "cgi", "cgitb", "chunk", "cmath", "cmd",
    "code", "codecs", "codeop", "collections", "colorsys", "compileall",
    "concurrent", "configparser", "contextlib", "contextvars", "copy", "copyreg",
    "cProfile", "crypt", "csv", "ctypes", "curses", "dataclasses", "datetime",
    "dbm", "decimal", "difflib", "dis", "distutils", "doctest", "email",
    "encodings", "enum", "errno", "faulthandler", "fcntl", "filecmp", "fileinput",
    "fnmatch", "fractions", "ftplib", "functools", "gc", "getopt", "getpass",
    "gettext", "glob", "graphlib", "grp", "gzip", "hashlib", "heapq", "hmac",
    "html", "http", "idlelib", "imaplib", "imghdr", "imp", "importlib", "inspect",
    "io", "ipaddress", "itertools", "json", "keyword", "lib2to3", "linecache",
    "locale", "logging", "lzma", "mailbox", "mailcap", "marshal", "math",
    "mimetypes", "mmap", "modulefinder", "multiprocessing", "netrc", "nis",
    "nntplib", "numbers", "operator", "optparse", "os", "ossaudiodev", "parser",
    "pathlib", "pdb", "pickle", "pickletools", "pipes", "pkgutil", "platform",
    "plistlib", "poplib", "posix", "posixpath", "pprint", "profile", "pstats",
    "pty", "pwd", "py_compile", "pyclbr", "pydoc", "queue", "quopri", "random",
    "re", "readline", "reprlib", "resource", "rlcompleter", "runpy", "sched",
    "secrets", "select", "selectors", "shelve", "shlex", "shutil", "signal",
    "site", "smtpd", "smtplib", "sndhdr", "socket", "socketserver", "spwd",
    "sqlite3", "ssl", "stat", "statistics", "string", "stringprep", "struct",
    "subprocess", "sunau", "symtable", "sys", "sysconfig", "syslog", "tabnanny",
    "tarfile", "telnetlib", "tempfile", "termios", "test", "textwrap", "threading",
    "time", "timeit", "tkinter", "token", "tokenize", "tomllib", "trace",
    "traceback", "tracemalloc", "tty", "turtle", "turtledemo", "types", "typing",
    "unicodedata", "unittest", "urllib", "uu", "uuid", "venv", "warnings",
    "wave", "weakref", "webbrowser", "winreg", "winsound", "wsgiref", "xdrlib",
    "xml", "xmlrpc", "zipapp", "zipfile", "zipimport", "zlib", "zoneinfo",
    "_thread", "typing_extensions",
})


def is_builtin_type(type_name: str) -> bool:
    """Check if a type name is a Python builtin."""
    # Strip generic parameters (e.g., List[str] -> List)
    base_type = type_name.split("[")[0].strip()
    # Handle qualified names (e.g., typing.List -> List)
    if "." in base_type:
        parts = base_type.split(".")
        # Check if module is stdlib
        if parts[0] in PYTHON_STDLIB_PACKAGES:
            return True
        base_type = parts[-1]
    return base_type in PYTHON_BUILTINS


def is_stdlib_package(package_name: str) -> bool:
    """Check if a package is part of Python stdlib."""
    # Handle subpackages (e.g., collections.abc -> collections)
    root_pkg = package_name.split(".")[0]
    return root_pkg in PYTHON_STDLIB_PACKAGES


# =============================================================================
# Type Reference Extraction (AST-Based)
# =============================================================================

def collect_types_from_annotation(ann: ast.expr | None, refs: set[str]) -> None:
    """
    Recursively collect type names from an annotation AST node.
    This is a rigorous AST-based approach that properly handles:
    - Simple names: int, MyClass
    - Attribute access: module.Type
    - Generic subscripts: List[str], Dict[str, int]
    - Union types: Union[A, B], A | B
    - Optional types: Optional[X]
    """
    if ann is None:
        return
    
    if isinstance(ann, ast.Name):
        # Simple type name like "int" or "MyClass"
        name = ann.id
        if not is_builtin_type(name):
            refs.add(name)
    
    elif isinstance(ann, ast.Attribute):
        # Qualified name like "module.Type"
        # Only collect the full path if the root is an external module
        full_name = _get_attribute_name(ann)
        if full_name and not is_builtin_type(full_name):
            refs.add(full_name)
    
    elif isinstance(ann, ast.Subscript):
        # Generic type like List[str], Dict[str, int], Optional[MyClass]
        # Collect the base type
        collect_types_from_annotation(ann.value, refs)
        # Collect type arguments
        if isinstance(ann.slice, ast.Tuple):
            # Multiple type args: Dict[str, int]
            for elt in ann.slice.elts:
                collect_types_from_annotation(elt, refs)
        else:
            # Single type arg: List[str]
            collect_types_from_annotation(ann.slice, refs)
    
    elif isinstance(ann, ast.BinOp) and isinstance(ann.op, ast.BitOr):
        # Union type using | operator: A | B
        collect_types_from_annotation(ann.left, refs)
        collect_types_from_annotation(ann.right, refs)
    
    elif isinstance(ann, ast.Constant):
        # String annotation or literal
        if isinstance(ann.value, str):
            # Forward reference as string - parse it
            try:
                parsed = ast.parse(ann.value, mode='eval')
                collect_types_from_annotation(parsed.body, refs)
            except SyntaxError:
                pass
    
    elif isinstance(ann, ast.Tuple):
        # Tuple of types (e.g., in function params)
        for elt in ann.elts:
            collect_types_from_annotation(elt, refs)
    
    elif isinstance(ann, ast.List):
        # List of types
        for elt in ann.elts:
            collect_types_from_annotation(elt, refs)


def _get_attribute_name(node: ast.Attribute) -> str | None:
    """Get the full dotted name from an Attribute node."""
    parts = []
    current = node
    while isinstance(current, ast.Attribute):
        parts.append(current.attr)
        current = current.value
    if isinstance(current, ast.Name):
        parts.append(current.id)
        return ".".join(reversed(parts))
    return None


class TypeReferenceCollector:
    """
    Collects type references during extraction using proper AST traversal.
    """
    def __init__(self):
        self.refs: set[str] = set()
        self.defined_types: set[str] = set()
        self.resolved_packages: dict[str, str] = {}  # type_name -> package_name
    
    def add_defined_type(self, name: str) -> None:
        """Register a locally defined type."""
        self.defined_types.add(name.split("[")[0])
    
    def collect_from_annotation(self, ann: ast.expr | None) -> None:
        """Collect type references from an annotation AST node."""
        collect_types_from_annotation(ann, self.refs)
    
    def get_external_refs(self) -> set[str]:
        """Get type references that are not locally defined and not builtins."""
        return {
            name for name in self.refs 
            if name.split("[")[0] not in self.defined_types and not is_builtin_type(name)
        }
    
    def resolve_package(self, type_name: str, installed_packages: set[str]) -> str | None:
        """
        Try to resolve the package a type came from.
        Uses import analysis and package lookup.
        """
        if type_name in self.resolved_packages:
            return self.resolved_packages[type_name]
        
        # Check if type_name is qualified (e.g., azure.core.HttpResponse)
        if "." in type_name:
            parts = type_name.split(".")
            # Try progressively shorter prefixes
            for i in range(len(parts) - 1, 0, -1):
                pkg = ".".join(parts[:i])
                if pkg in installed_packages and not is_stdlib_package(pkg):
                    self.resolved_packages[type_name] = pkg
                    return pkg
        
        return None
    
    def clear(self) -> None:
        """Reset the collector for a new extraction."""
        self.refs.clear()
        self.defined_types.clear()
        self.resolved_packages.clear()


# Global collector instance
_type_collector = TypeReferenceCollector()


# =============================================================================
# Core Extraction Functions
# =============================================================================

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
    """Extract function/method info and collect type references."""
    args = []
    for arg in node.args.args:
        arg_str = arg.arg
        if arg.annotation:
            arg_str += f": {ast.unparse(arg.annotation)}"
            # Collect type reference from annotation AST
            _type_collector.collect_from_annotation(arg.annotation)
        args.append(arg_str)

    # Handle *args, **kwargs
    if node.args.vararg:
        va = node.args.vararg
        va_str = f"*{va.arg}"
        if va.annotation:
            va_str += f": {ast.unparse(va.annotation)}"
            _type_collector.collect_from_annotation(va.annotation)
        args.append(va_str)
    if node.args.kwarg:
        kw = node.args.kwarg
        kw_str = f"**{kw.arg}"
        if kw.annotation:
            kw_str += f": {ast.unparse(kw.annotation)}"
            _type_collector.collect_from_annotation(kw.annotation)
        args.append(kw_str)

    sig = ", ".join(args)

    result: dict[str, Any] = {
        "name": node.name,
        "sig": sig,
    }

    ret = format_annotation(node.returns)
    if ret:
        result["ret"] = ret
        # Collect return type reference
        _type_collector.collect_from_annotation(node.returns)

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
    """Extract class info and collect type references."""
    bases = []
    for b in node.bases:
        if isinstance(b, (ast.Name, ast.Attribute, ast.Subscript)):
            bases.append(ast.unparse(b))
            # Collect base class type reference
            _type_collector.collect_from_annotation(b)

    result: dict[str, Any] = {
        "name": node.name,
    }
    
    # Register this class as a defined type
    _type_collector.add_defined_type(node.name)

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
    
    # Clear the type collector for this extraction
    _type_collector.clear()

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

    result: dict[str, Any] = {
        "package": package_name,
        "modules": modules
    }
    
    # Resolve transitive dependencies
    dependencies = resolve_transitive_dependencies(result, root_path)
    if dependencies:
        result["dependencies"] = dependencies
    
    return result


# =============================================================================
# Transitive Dependency Resolution
# =============================================================================

def find_installed_packages(root_path: Path) -> dict[str, Path]:
    """Find installed packages in site-packages or venv."""
    packages: dict[str, Path] = {}
    
    # Common locations for installed packages
    search_paths = [
        root_path / "venv" / "lib",
        root_path / ".venv" / "lib",
    ]
    
    # Also check system site-packages via sys.path
    for path in sys.path:
        if "site-packages" in path:
            search_paths.append(Path(path))
    
    for search_path in search_paths:
        if not search_path.exists():
            continue
        
        # Look for site-packages in Python lib directories
        site_packages_paths = list(search_path.glob("**/site-packages"))
        for site_packages in site_packages_paths:
            if not site_packages.is_dir():
                continue
            
            for item in site_packages.iterdir():
                if item.is_dir() and not item.name.startswith(('_', '.')):
                    # Check if it's a valid Python package
                    if (item / "__init__.py").exists() or (item / "__init__.pyi").exists():
                        pkg_name = item.name
                        if pkg_name not in packages:
                            packages[pkg_name] = item
    
    return packages


def extract_type_from_package(type_name: str, package_path: Path) -> dict[str, Any] | None:
    """Try to extract a type definition from a package."""
    # Look for .pyi stub files first, then .py files
    for pattern in ["**/*.pyi", "**/*.py"]:
        for file_path in package_path.glob(pattern):
            if file_path.name.startswith('_') and file_path.name != '__init__.py' and file_path.name != '__init__.pyi':
                continue
            
            try:
                code = file_path.read_text(encoding='utf-8')
                tree = ast.parse(code)
            except (SyntaxError, UnicodeDecodeError):
                continue
            
            # Look for the type definition
            for node in ast.iter_child_nodes(tree):
                if isinstance(node, ast.ClassDef) and node.name == type_name:
                    return extract_class(node)
    
    return None


def resolve_transitive_dependencies(api: dict[str, Any], root_path: Path) -> list[dict[str, Any]]:
    """
    Resolve types referenced in the API that come from external packages.
    Uses AST-based type collection for accurate dependency tracking.
    Returns a list of DependencyInfo objects.
    """
    # Get externally referenced types from the AST collector
    # (these were collected during extraction via collect_from_annotation)
    external_refs = _type_collector.get_external_refs()
    
    if not external_refs:
        return []
    
    # Find installed packages
    installed_packages = find_installed_packages(root_path)
    installed_package_names = set(installed_packages.keys())
    
    # Group resolved types by package
    dependencies: dict[str, dict[str, Any]] = {}
    
    for type_name in external_refs:
        # First try to resolve via qualified name (module.Type)
        pkg_name = _type_collector.resolve_package(type_name, installed_package_names)
        
        if pkg_name and pkg_name in installed_packages:
            if is_stdlib_package(pkg_name):
                continue
            
            pkg_path = installed_packages[pkg_name]
            type_info = extract_type_from_package(type_name.split(".")[-1], pkg_path)
            if type_info:
                if pkg_name not in dependencies:
                    dependencies[pkg_name] = {"package": pkg_name, "classes": []}
                dependencies[pkg_name]["classes"].append(type_info)
                continue
        
        # Fall back to searching all installed packages
        for pkg_name, pkg_path in installed_packages.items():
            if is_stdlib_package(pkg_name):
                continue
            
            type_info = extract_type_from_package(type_name.split(".")[-1], pkg_path)
            if type_info:
                if pkg_name not in dependencies:
                    dependencies[pkg_name] = {"package": pkg_name, "classes": []}
                dependencies[pkg_name]["classes"].append(type_info)
                break
    
    # Convert to list and clean up empty arrays
    result = []
    for dep_info in dependencies.values():
        if not dep_info.get("classes"):
            del dep_info["classes"]
        if dep_info.get("classes") or dep_info.get("functions"):
            result.append(dep_info)
    
    return sorted(result, key=lambda d: d["package"])


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

    # Add dependency types section
    dependencies = api.get("dependencies", [])
    if dependencies:
        lines.append("")
        lines.append("# " + "=" * 77)
        lines.append("# Dependency Types (from external packages)")
        lines.append("# " + "=" * 77)
        lines.append("")
        
        for dep in dependencies:
            pkg_name = dep.get("package", "unknown")
            lines.append(f"# From: {pkg_name}")
            lines.append("")
            
            for cls in dep.get("classes", []):
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
