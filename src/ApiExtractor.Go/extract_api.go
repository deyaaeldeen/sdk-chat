//go:build ignore

package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"go/ast"
	"go/doc"
	"go/parser"
	"go/token"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"unicode"
)

// API Models
type ApiIndex struct {
	Package  string       `json:"package"`
	Packages []PackageApi `json:"packages"`
}

type PackageApi struct {
	Name       string      `json:"name"`
	Doc        string      `json:"doc,omitempty"`
	Structs    []StructApi `json:"structs,omitempty"`
	Interfaces []IfaceApi  `json:"interfaces,omitempty"`
	Functions  []FuncApi   `json:"functions,omitempty"`
	Types      []TypeApi   `json:"types,omitempty"`
	Constants  []ConstApi  `json:"constants,omitempty"`
	Variables  []VarApi    `json:"variables,omitempty"`
}

type StructApi struct {
	Name    string     `json:"name"`
	Doc     string     `json:"doc,omitempty"`
	Fields  []FieldApi `json:"fields,omitempty"`
	Methods []FuncApi  `json:"methods,omitempty"`
}

type IfaceApi struct {
	Name    string    `json:"name"`
	Doc     string    `json:"doc,omitempty"`
	Methods []FuncApi `json:"methods,omitempty"`
}

type FuncApi struct {
	Name     string `json:"name"`
	Sig      string `json:"sig"`
	Ret      string `json:"ret,omitempty"`
	Doc      string `json:"doc,omitempty"`
	IsMethod bool   `json:"method,omitempty"`
	Receiver string `json:"recv,omitempty"`
}

type FieldApi struct {
	Name string `json:"name"`
	Type string `json:"type"`
	Tag  string `json:"tag,omitempty"`
	Doc  string `json:"doc,omitempty"`
}

type TypeApi struct {
	Name string `json:"name"`
	Type string `json:"type"`
	Doc  string `json:"doc,omitempty"`
}

type ConstApi struct {
	Name  string `json:"name"`
	Type  string `json:"type,omitempty"`
	Value string `json:"value,omitempty"`
	Doc   string `json:"doc,omitempty"`
}

type VarApi struct {
	Name string `json:"name"`
	Type string `json:"type"`
	Doc  string `json:"doc,omitempty"`
}

func main() {
	var outputJson, outputStub, pretty bool
	var usageApiFile string
	flag.BoolVar(&outputJson, "json", false, "Output JSON")
	flag.BoolVar(&outputStub, "stub", false, "Output Go stubs")
	flag.BoolVar(&pretty, "pretty", false, "Pretty print JSON")
	flag.StringVar(&usageApiFile, "usage", "", "Analyze samples usage: -usage <api_json_file> <samples_path>")
	flag.Parse()

	// Handle --usage mode
	if usageApiFile != "" {
		if flag.NArg() < 1 {
			fmt.Fprintln(os.Stderr, "Usage: go run extract_api.go -usage <api_json_file> <samples_path>")
			os.Exit(1)
		}
		analyzeUsage(usageApiFile, flag.Arg(0))
		return
	}

	if flag.NArg() < 1 {
		fmt.Fprintln(os.Stderr, "Usage: go run extract_api.go <path> [--json] [--stub] [--pretty]")
		fmt.Fprintln(os.Stderr, "       go run extract_api.go -usage <api_json_file> <samples_path>")
		os.Exit(1)
	}

	rootPath := flag.Arg(0)
	if !outputJson && !outputStub {
		outputStub = true
	}

	api, err := extractPackage(rootPath)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Error: %v\n", err)
		os.Exit(1)
	}

	if outputJson {
		var output []byte
		if pretty {
			output, _ = json.MarshalIndent(api, "", "  ")
		} else {
			output, _ = json.Marshal(api)
		}
		fmt.Println(string(output))
	} else {
		fmt.Println(formatStubs(api))
	}
}

// ===== Usage Analysis Types =====
type UsageResult struct {
	FileCount int           `json:"fileCount"`
	Covered   []CoveredOp   `json:"covered"`
	Uncovered []UncoveredOp `json:"uncovered"`
	Patterns  []string      `json:"patterns"`
}

type CoveredOp struct {
	Client string `json:"client"`
	Method string `json:"method"`
	File   string `json:"file"`
	Line   int    `json:"line"`
}

type UncoveredOp struct {
	Client string `json:"client"`
	Method string `json:"method"`
	Sig    string `json:"sig"`
}

// ===== Usage Analysis =====
func analyzeUsage(apiJsonFile, samplesPath string) {
	// Load API index
	apiData, err := os.ReadFile(apiJsonFile)
	if err != nil {
		fmt.Fprintln(os.Stderr, "Error reading API file:", err)
		os.Exit(1)
	}

	var apiIndex ApiIndex
	if err := json.Unmarshal(apiData, &apiIndex); err != nil {
		fmt.Fprintln(os.Stderr, "Error parsing API JSON:", err)
		os.Exit(1)
	}

	allStructs := []StructApi{}
	allInterfaces := []IfaceApi{}
	allTypeNames := make(map[string]bool)
	for _, pkg := range apiIndex.Packages {
		for _, s := range pkg.Structs {
			allStructs = append(allStructs, s)
			allTypeNames[s.Name] = true
		}
		for _, iface := range pkg.Interfaces {
			allInterfaces = append(allInterfaces, iface)
			allTypeNames[iface.Name] = true
		}
	}

	interfaceMethods := make(map[string]map[string]bool)
	for _, iface := range allInterfaces {
		methods := make(map[string]bool)
		for _, m := range iface.Methods {
			methods[m.Name] = true
		}
		if len(methods) > 0 {
			interfaceMethods[iface.Name] = methods
		}
	}

	interfaceImplementers := make(map[string][]StructApi)
	for ifaceName, methods := range interfaceMethods {
		for _, s := range allStructs {
			structMethods := make(map[string]bool)
			for _, m := range s.Methods {
				structMethods[m.Name] = true
			}
			implements := true
			for methodName := range methods {
				if !structMethods[methodName] {
					implements = false
					break
				}
			}
			if implements {
				interfaceImplementers[ifaceName] = append(interfaceImplementers[ifaceName], s)
			}
		}
	}

	references := make(map[string]map[string]bool)
	for _, s := range allStructs {
		references[s.Name] = getReferencedTypes(s, allTypeNames)
	}
	for _, iface := range allInterfaces {
		references[iface.Name] = getReferencedTypesForInterface(iface, allTypeNames)
	}

	referencedBy := make(map[string]int)
	for _, refs := range references {
		for ref := range refs {
			referencedBy[ref] = referencedBy[ref] + 1
		}
	}

	operationTypes := make(map[string]bool)
	for _, s := range allStructs {
		if len(s.Methods) > 0 {
			operationTypes[s.Name] = true
		}
	}
	for _, iface := range allInterfaces {
		if len(iface.Methods) > 0 {
			operationTypes[iface.Name] = true
		}
	}

	rootStructs := []StructApi{}
	for _, s := range allStructs {
		_, isReferenced := referencedBy[s.Name]
		refs := references[s.Name]
		referencesOperations := false
		for ref := range refs {
			if operationTypes[ref] {
				referencesOperations = true
				break
			}
		}
		// Client types are always roots - they're SDK entry points even if referenced by options/builders
		isClientType := strings.HasSuffix(s.Name, "Client")
		if isClientType || (!isReferenced && (len(s.Methods) > 0 || referencesOperations)) {
			rootStructs = append(rootStructs, s)
		}
	}

	if len(rootStructs) == 0 {
		for _, s := range allStructs {
			refs := references[s.Name]
			referencesOperations := false
			for ref := range refs {
				if operationTypes[ref] {
					referencesOperations = true
					break
				}
			}
			if len(s.Methods) > 0 || referencesOperations {
				rootStructs = append(rootStructs, s)
			}
		}
	}

	reachable := make(map[string]bool)
	queue := []string{}
	for _, s := range rootStructs {
		if !reachable[s.Name] {
			reachable[s.Name] = true
			queue = append(queue, s.Name)
		}
	}

	for len(queue) > 0 {
		current := queue[0]
		queue = queue[1:]

		if refs, ok := references[current]; ok {
			for ref := range refs {
				if !reachable[ref] {
					reachable[ref] = true
					queue = append(queue, ref)
				}
			}
		}

		for _, impl := range interfaceImplementers[current] {
			if !reachable[impl.Name] {
				reachable[impl.Name] = true
				queue = append(queue, impl.Name)
			}
		}
	}

	usageStructs := []StructApi{}
	for _, s := range allStructs {
		if reachable[s.Name] && len(s.Methods) > 0 {
			usageStructs = append(usageStructs, s)
		}
	}

	usageInterfaces := []IfaceApi{}
	for _, iface := range allInterfaces {
		if reachable[iface.Name] && len(iface.Methods) > 0 {
			usageInterfaces = append(usageInterfaces, iface)
		}
	}

	// Build client methods map
	clientMethods := make(map[string]map[string]string) // client -> method -> signature
	for _, s := range usageStructs {
		methods := make(map[string]string)
		for _, m := range s.Methods {
			methods[m.Name] = m.Sig
		}
		if len(methods) > 0 {
			if _, exists := clientMethods[s.Name]; !exists {
				clientMethods[s.Name] = methods
			}
		}
	}

	for _, iface := range usageInterfaces {
		methods := make(map[string]string)
		for _, m := range iface.Methods {
			methods[m.Name] = m.Sig
		}
		if len(methods) > 0 {
			if _, exists := clientMethods[iface.Name]; !exists {
				clientMethods[iface.Name] = methods
			}
		}
	}

	if len(clientMethods) == 0 {
		result := UsageResult{FileCount: 0, Covered: []CoveredOp{}, Uncovered: []UncoveredOp{}, Patterns: []string{}}
		output, _ := json.Marshal(result)
		fmt.Println(string(output))
		return
	}

	// Find Go files in samples path
	absPath, _ := filepath.Abs(samplesPath)
	var goFiles []string
	filepath.Walk(absPath, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() {
			return nil
		}
		if strings.HasSuffix(path, ".go") && !strings.HasSuffix(path, "_test.go") {
			goFiles = append(goFiles, path)
		}
		return nil
	})

	covered := []CoveredOp{}
	seenOps := make(map[string]bool)
	patterns := make(map[string]bool)

	fset := token.NewFileSet()
	for _, file := range goFiles {
		src, err := os.ReadFile(file)
		if err != nil {
			continue
		}

		f, err := parser.ParseFile(fset, file, src, parser.ParseComments)
		if err != nil {
			continue
		}

		relPath, _ := filepath.Rel(absPath, file)

		// Walk AST looking for method calls
		ast.Inspect(f, func(n ast.Node) bool {
			call, ok := n.(*ast.CallExpr)
			if !ok {
				return true
			}

			// Check for selector expression: receiver.Method()
			sel, ok := call.Fun.(*ast.SelectorExpr)
			if !ok {
				return true
			}

			methodName := sel.Sel.Name
			receiverStr := formatExpr(sel.X)

			for clientName, methods := range clientMethods {
				clientBase := strings.TrimSuffix(clientName, "Client")
				if strings.Contains(strings.ToLower(receiverStr), strings.ToLower(clientBase)) ||
					strings.Contains(strings.ToLower(receiverStr), "client") {
					if _, exists := methods[methodName]; exists {
						key := clientName + "." + methodName
						if !seenOps[key] {
							seenOps[key] = true
							pos := fset.Position(call.Pos())
							covered = append(covered, CoveredOp{
								Client: clientName,
								Method: methodName,
								File:   relPath,
								Line:   pos.Line,
							})
						}
					}
				}
			}

			return true
		})

		// Detect patterns by inspecting AST
		srcStr := string(src)
		srcLower := strings.ToLower(srcStr)

		ast.Inspect(f, func(n ast.Node) bool {
			switch n.(type) {
			case *ast.DeferStmt:
				patterns["defer-cleanup"] = true
			case *ast.GoStmt:
				patterns["goroutine"] = true
			case *ast.SelectStmt:
				patterns["channel-select"] = true
			case *ast.RangeStmt:
				// Check for pagination patterns
				if strings.Contains(srcLower, "page") || strings.Contains(srcLower, "pager") {
					patterns["pagination"] = true
				}
			}
			return true
		})

		if strings.Contains(srcStr, "context.") {
			patterns["context"] = true
		}
		if strings.Contains(srcLower, "credential") || strings.Contains(srcLower, "authenticate") {
			patterns["authentication"] = true
		}
		if strings.Contains(srcLower, "retry") || strings.Contains(srcLower, "backoff") {
			patterns["retry"] = true
		}
		if strings.Contains(srcStr, "err != nil") {
			patterns["error-handling"] = true
		}
	}

	// Build uncovered list
	uncovered := []UncoveredOp{}
	for clientName, methods := range clientMethods {
		for method, sig := range methods {
			key := clientName + "." + method
			if !seenOps[key] {
				uncovered = append(uncovered, UncoveredOp{
					Client: clientName,
					Method: method,
					Sig:    sig,
				})
			}
		}
	}

	// Convert patterns map to slice
	patternList := []string{}
	for p := range patterns {
		patternList = append(patternList, p)
	}
	sort.Strings(patternList)

	result := UsageResult{
		FileCount: len(goFiles),
		Covered:   covered,
		Uncovered: uncovered,
		Patterns:  patternList,
	}

	output, _ := json.Marshal(result)
	fmt.Println(string(output))
}

func getReferencedTypes(s StructApi, allTypeNames map[string]bool) map[string]bool {
	refs := make(map[string]bool)

	for _, m := range s.Methods {
		for typeName := range allTypeNames {
			if strings.Contains(m.Sig, typeName) || (m.Ret != "" && strings.Contains(m.Ret, typeName)) {
				refs[typeName] = true
			}
		}
	}

	for _, f := range s.Fields {
		for typeName := range allTypeNames {
			if strings.Contains(f.Type, typeName) {
				refs[typeName] = true
			}
		}
	}

	return refs
}

func getReferencedTypesForInterface(i IfaceApi, allTypeNames map[string]bool) map[string]bool {
	refs := make(map[string]bool)

	for _, m := range i.Methods {
		for typeName := range allTypeNames {
			if strings.Contains(m.Sig, typeName) || (m.Ret != "" && strings.Contains(m.Ret, typeName)) {
				refs[typeName] = true
			}
		}
	}

	return refs
}

func extractPackage(rootPath string) (*ApiIndex, error) {
	absPath, err := filepath.Abs(rootPath)
	if err != nil {
		return nil, err
	}

	packageName := detectPackageName(absPath)
	packages := make(map[string]*PackageApi)

	err = filepath.Walk(absPath, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return nil
		}
		if info.IsDir() {
			// Skip vendor, testdata, internal, examples
			name := info.Name()
			if name == "vendor" || name == "testdata" || name == "internal" ||
				name == "examples" || name == "_test" || strings.HasPrefix(name, ".") {
				return filepath.SkipDir
			}
			return nil
		}
		return nil
	})

	// Find all Go packages
	fset := token.NewFileSet()
	pkgDirs := make(map[string]bool)

	filepath.Walk(absPath, func(path string, info os.FileInfo, err error) error {
		if err != nil || info.IsDir() {
			return nil
		}
		if strings.HasSuffix(path, ".go") && !strings.HasSuffix(path, "_test.go") {
			pkgDirs[filepath.Dir(path)] = true
		}
		return nil
	})

	for dir := range pkgDirs {
		// Skip internal, vendor, etc
		relDir, _ := filepath.Rel(absPath, dir)
		if strings.Contains(relDir, "internal") || strings.Contains(relDir, "vendor") ||
			strings.Contains(relDir, "testdata") || strings.Contains(relDir, "examples") {
			continue
		}

		pkgs, err := parser.ParseDir(fset, dir, func(fi os.FileInfo) bool {
			return !strings.HasSuffix(fi.Name(), "_test.go")
		}, parser.ParseComments)
		if err != nil {
			continue
		}

		for pkgName, astPkg := range pkgs {
			if strings.HasSuffix(pkgName, "_test") {
				continue
			}

			docPkg := doc.New(astPkg, dir, doc.AllDecls)

			pkgApi := extractPkg(docPkg, fset)
			pkgApi.Name = relDir
			if pkgApi.Name == "" || pkgApi.Name == "." {
				pkgApi.Name = pkgName
			}

			if len(pkgApi.Structs) > 0 || len(pkgApi.Interfaces) > 0 ||
				len(pkgApi.Functions) > 0 || len(pkgApi.Types) > 0 ||
				len(pkgApi.Constants) > 0 || len(pkgApi.Variables) > 0 {
				packages[pkgApi.Name] = pkgApi
			}
		}
	}

	// Sort packages by name
	var sortedPkgs []PackageApi
	for _, p := range packages {
		sortedPkgs = append(sortedPkgs, *p)
	}
	sort.Slice(sortedPkgs, func(i, j int) bool {
		return sortedPkgs[i].Name < sortedPkgs[j].Name
	})

	return &ApiIndex{
		Package:  packageName,
		Packages: sortedPkgs,
	}, nil
}

func extractPkg(pkg *doc.Package, fset *token.FileSet) *PackageApi {
	api := &PackageApi{
		Doc: firstLine(pkg.Doc),
	}

	// Types (structs and interfaces)
	for _, t := range pkg.Types {
		if !isExported(t.Name) {
			continue
		}

		// Add type-associated functions (constructors like NewXxx) to top-level functions
		for _, f := range t.Funcs {
			if !isExported(f.Name) {
				continue
			}
			api.Functions = append(api.Functions, extractFunc(f.Decl, f.Doc))
		}

		// Add type-associated constants to top-level constants
		for _, c := range t.Consts {
			for _, spec := range c.Decl.Specs {
				vs, ok := spec.(*ast.ValueSpec)
				if !ok {
					continue
				}
				for i, name := range vs.Names {
					if !isExported(name.Name) {
						continue
					}
					cv := ConstApi{
						Name: name.Name,
						Doc:  firstLine(c.Doc),
						Type: t.Name, // Type from the associated type
					}
					if vs.Type != nil {
						cv.Type = formatExpr(vs.Type)
					}
					if i < len(vs.Values) {
						cv.Value = formatExpr(vs.Values[i])
					}
					api.Constants = append(api.Constants, cv)
				}
			}
		}

		for _, spec := range t.Decl.Specs {
			ts, ok := spec.(*ast.TypeSpec)
			if !ok {
				continue
			}

			switch st := ts.Type.(type) {
			case *ast.StructType:
				s := extractStruct(t, st)
				api.Structs = append(api.Structs, s)

			case *ast.InterfaceType:
				i := extractInterface(t, st)
				api.Interfaces = append(api.Interfaces, i)

			default:
				// Type alias
				api.Types = append(api.Types, TypeApi{
					Name: t.Name,
					Type: formatExpr(ts.Type),
					Doc:  firstLine(t.Doc),
				})
			}
		}
	}

	// Functions
	for _, f := range pkg.Funcs {
		if !isExported(f.Name) {
			continue
		}
		api.Functions = append(api.Functions, extractFunc(f.Decl, f.Doc))
	}

	// Constants
	for _, c := range pkg.Consts {
		for _, spec := range c.Decl.Specs {
			vs, ok := spec.(*ast.ValueSpec)
			if !ok {
				continue
			}
			for i, name := range vs.Names {
				if !isExported(name.Name) {
					continue
				}
				cv := ConstApi{
					Name: name.Name,
					Doc:  firstLine(c.Doc),
				}
				if vs.Type != nil {
					cv.Type = formatExpr(vs.Type)
				}
				if i < len(vs.Values) {
					cv.Value = formatExpr(vs.Values[i])
				}
				api.Constants = append(api.Constants, cv)
			}
		}
	}

	// Variables
	for _, v := range pkg.Vars {
		for _, spec := range v.Decl.Specs {
			vs, ok := spec.(*ast.ValueSpec)
			if !ok {
				continue
			}
			for _, name := range vs.Names {
				if !isExported(name.Name) {
					continue
				}
				vv := VarApi{
					Name: name.Name,
					Doc:  firstLine(v.Doc),
				}
				if vs.Type != nil {
					vv.Type = formatExpr(vs.Type)
				}
				api.Variables = append(api.Variables, vv)
			}
		}
	}

	return api
}

func extractStruct(t *doc.Type, st *ast.StructType) StructApi {
	s := StructApi{
		Name: t.Name,
		Doc:  firstLine(t.Doc),
	}

	// Fields
	for _, field := range st.Fields.List {
		if len(field.Names) == 0 {
			// Embedded field
			s.Fields = append(s.Fields, FieldApi{
				Name: formatExpr(field.Type),
				Type: formatExpr(field.Type),
			})
			continue
		}
		for _, name := range field.Names {
			if !isExported(name.Name) {
				continue
			}
			f := FieldApi{
				Name: name.Name,
				Type: formatExpr(field.Type),
			}
			if field.Tag != nil {
				f.Tag = field.Tag.Value
			}
			s.Fields = append(s.Fields, f)
		}
	}

	// Methods
	for _, m := range t.Methods {
		if !isExported(m.Name) {
			continue
		}
		s.Methods = append(s.Methods, extractFunc(m.Decl, m.Doc))
	}

	// Constructor functions (like NewSampleClient)
	for _, f := range t.Funcs {
		if !isExported(f.Name) {
			continue
		}
		fn := extractFunc(f.Decl, f.Doc)
		fn.IsMethod = false // These are constructors, not methods
		s.Methods = append(s.Methods, fn)
	}

	return s
}

func extractInterface(t *doc.Type, it *ast.InterfaceType) IfaceApi {
	i := IfaceApi{
		Name: t.Name,
		Doc:  firstLine(t.Doc),
	}

	for _, m := range it.Methods.List {
		if len(m.Names) == 0 {
			continue // embedded interface
		}
		for _, name := range m.Names {
			if !isExported(name.Name) {
				continue
			}
			ft, ok := m.Type.(*ast.FuncType)
			if !ok {
				continue
			}
			i.Methods = append(i.Methods, FuncApi{
				Name: name.Name,
				Sig:  formatParams(ft.Params),
				Ret:  formatResults(ft.Results),
			})
		}
	}

	return i
}

func extractFunc(decl *ast.FuncDecl, docStr string) FuncApi {
	f := FuncApi{
		Name: decl.Name.Name,
		Sig:  formatParams(decl.Type.Params),
		Ret:  formatResults(decl.Type.Results),
		Doc:  firstLine(docStr),
	}

	if decl.Recv != nil && len(decl.Recv.List) > 0 {
		f.IsMethod = true
		f.Receiver = formatExpr(decl.Recv.List[0].Type)
	}

	return f
}

func formatParams(fl *ast.FieldList) string {
	if fl == nil {
		return ""
	}
	var parts []string
	for _, p := range fl.List {
		typeStr := formatExpr(p.Type)
		if len(p.Names) == 0 {
			parts = append(parts, typeStr)
		} else {
			for _, name := range p.Names {
				parts = append(parts, name.Name+" "+typeStr)
			}
		}
	}
	return strings.Join(parts, ", ")
}

func formatResults(fl *ast.FieldList) string {
	if fl == nil || len(fl.List) == 0 {
		return ""
	}
	var parts []string
	for _, r := range fl.List {
		parts = append(parts, formatExpr(r.Type))
	}
	if len(parts) == 1 {
		return parts[0]
	}
	return "(" + strings.Join(parts, ", ") + ")"
}

func formatExpr(expr ast.Expr) string {
	if expr == nil {
		return ""
	}
	switch e := expr.(type) {
	case *ast.Ident:
		return e.Name
	case *ast.StarExpr:
		return "*" + formatExpr(e.X)
	case *ast.ArrayType:
		return "[]" + formatExpr(e.Elt)
	case *ast.MapType:
		return "map[" + formatExpr(e.Key) + "]" + formatExpr(e.Value)
	case *ast.SelectorExpr:
		return formatExpr(e.X) + "." + e.Sel.Name
	case *ast.InterfaceType:
		return "interface{}"
	case *ast.ChanType:
		return "chan " + formatExpr(e.Value)
	case *ast.FuncType:
		return "func(" + formatParams(e.Params) + ") " + formatResults(e.Results)
	case *ast.Ellipsis:
		return "..." + formatExpr(e.Elt)
	case *ast.BasicLit:
		return e.Value
	case *ast.IndexExpr:
		return formatExpr(e.X) + "[" + formatExpr(e.Index) + "]"
	case *ast.IndexListExpr:
		var indices []string
		for _, idx := range e.Indices {
			indices = append(indices, formatExpr(idx))
		}
		return formatExpr(e.X) + "[" + strings.Join(indices, ", ") + "]"
	default:
		return fmt.Sprintf("%T", expr)
	}
}

func firstLine(s string) string {
	s = strings.TrimSpace(s)
	if s == "" {
		return ""
	}
	lines := strings.SplitN(s, "\n", 2)
	line := strings.TrimSpace(lines[0])
	if len(line) > 120 {
		line = line[:117] + "..."
	}
	return line
}

func isExported(name string) bool {
	if name == "" {
		return false
	}
	r := []rune(name)[0]
	return unicode.IsUpper(r)
}

func detectPackageName(rootPath string) string {
	// Check go.mod
	gomod := filepath.Join(rootPath, "go.mod")
	if data, err := os.ReadFile(gomod); err == nil {
		lines := strings.Split(string(data), "\n")
		for _, line := range lines {
			if strings.HasPrefix(line, "module ") {
				return strings.TrimSpace(strings.TrimPrefix(line, "module "))
			}
		}
	}
	return filepath.Base(rootPath)
}

func formatStubs(api *ApiIndex) string {
	var sb strings.Builder
	sb.WriteString(fmt.Sprintf("// %s - Public API Surface\n", api.Package))
	sb.WriteString("// Extracted by ApiExtractor.Go\n\n")

	for _, pkg := range api.Packages {
		sb.WriteString(fmt.Sprintf("// Package: %s\n", pkg.Name))
		if pkg.Doc != "" {
			sb.WriteString(fmt.Sprintf("// %s\n", pkg.Doc))
		}
		sb.WriteString("\n")

		// Constants
		for _, c := range pkg.Constants {
			if c.Doc != "" {
				sb.WriteString(fmt.Sprintf("// %s\n", c.Doc))
			}
			if c.Type != "" {
				sb.WriteString(fmt.Sprintf("const %s %s = %s\n", c.Name, c.Type, c.Value))
			} else {
				sb.WriteString(fmt.Sprintf("const %s = %s\n", c.Name, c.Value))
			}
		}
		if len(pkg.Constants) > 0 {
			sb.WriteString("\n")
		}

		// Variables
		for _, v := range pkg.Variables {
			if v.Doc != "" {
				sb.WriteString(fmt.Sprintf("// %s\n", v.Doc))
			}
			sb.WriteString(fmt.Sprintf("var %s %s\n", v.Name, v.Type))
		}
		if len(pkg.Variables) > 0 {
			sb.WriteString("\n")
		}

		// Types
		for _, t := range pkg.Types {
			if t.Doc != "" {
				sb.WriteString(fmt.Sprintf("// %s\n", t.Doc))
			}
			sb.WriteString(fmt.Sprintf("type %s = %s\n", t.Name, t.Type))
		}
		if len(pkg.Types) > 0 {
			sb.WriteString("\n")
		}

		// Interfaces
		for _, i := range pkg.Interfaces {
			if i.Doc != "" {
				sb.WriteString(fmt.Sprintf("// %s\n", i.Doc))
			}
			sb.WriteString(fmt.Sprintf("type %s interface {\n", i.Name))
			for _, m := range i.Methods {
				ret := ""
				if m.Ret != "" {
					ret = " " + m.Ret
				}
				sb.WriteString(fmt.Sprintf("    %s(%s)%s\n", m.Name, m.Sig, ret))
			}
			sb.WriteString("}\n\n")
		}

		// Structs
		for _, s := range pkg.Structs {
			if s.Doc != "" {
				sb.WriteString(fmt.Sprintf("// %s\n", s.Doc))
			}
			sb.WriteString(fmt.Sprintf("type %s struct {\n", s.Name))
			for _, f := range s.Fields {
				sb.WriteString(fmt.Sprintf("    %s %s\n", f.Name, f.Type))
			}
			sb.WriteString("}\n")
			for _, m := range s.Methods {
				ret := ""
				if m.Ret != "" {
					ret = " " + m.Ret
				}
				sb.WriteString(fmt.Sprintf("func (%s) %s(%s)%s\n", m.Receiver, m.Name, m.Sig, ret))
			}
			sb.WriteString("\n")
		}

		// Functions
		for _, f := range pkg.Functions {
			if f.Doc != "" {
				sb.WriteString(fmt.Sprintf("// %s\n", f.Doc))
			}
			ret := ""
			if f.Ret != "" {
				ret = " " + f.Ret
			}
			sb.WriteString(fmt.Sprintf("func %s(%s)%s\n", f.Name, f.Sig, ret))
		}
		if len(pkg.Functions) > 0 {
			sb.WriteString("\n")
		}
	}

	return sb.String()
}
