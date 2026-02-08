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

// =============================================================================
// API Models
// =============================================================================

type ApiIndex struct {
	Package      string           `json:"package"`
	Packages     []PackageApi     `json:"packages"`
	Dependencies []DependencyInfo `json:"dependencies,omitempty"`
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

type DependencyInfo struct {
	Package    string      `json:"package"`
	Structs    []StructApi `json:"structs,omitempty"`
	Interfaces []IfaceApi  `json:"interfaces,omitempty"`
	Types      []TypeApi   `json:"types,omitempty"`
}

type StructApi struct {
	Name           string     `json:"name"`
	Doc            string     `json:"doc,omitempty"`
	Fields         []FieldApi `json:"fields,omitempty"`
	Methods        []FuncApi  `json:"methods,omitempty"`
	EntryPoint     bool       `json:"entryPoint,omitempty"`
	ReExportedFrom string     `json:"reExportedFrom,omitempty"`
}

type IfaceApi struct {
	Name           string    `json:"name"`
	Doc            string    `json:"doc,omitempty"`
	Methods        []FuncApi `json:"methods,omitempty"`
	EntryPoint     bool      `json:"entryPoint,omitempty"`
	ReExportedFrom string    `json:"reExportedFrom,omitempty"`
}

type FuncApi struct {
	Name           string `json:"name"`
	EntryPoint     bool   `json:"entryPoint,omitempty"`
	ReExportedFrom string `json:"reExportedFrom,omitempty"`
	Sig            string `json:"sig"`
	Ret            string `json:"ret,omitempty"`
	Doc            string `json:"doc,omitempty"`
	IsMethod       bool   `json:"method,omitempty"`
	Receiver       string `json:"recv,omitempty"`
}

type FieldApi struct {
	Name string `json:"name"`
	Type string `json:"type"`
	Tag  string `json:"tag,omitempty"`
	Doc  string `json:"doc,omitempty"`
}

type TypeApi struct {
	Name           string `json:"name"`
	Type           string `json:"type"`
	Doc            string `json:"doc,omitempty"`
	ReExportedFrom string `json:"reExportedFrom,omitempty"`
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

// =============================================================================
// Builtin Type Detection
// =============================================================================

// Go standard library packages (not external dependencies)
var goStdlibPackages = map[string]bool{
	"archive": true, "bufio": true, "builtin": true, "bytes": true,
	"compress": true, "container": true, "context": true, "crypto": true,
	"database": true, "debug": true, "embed": true, "encoding": true,
	"errors": true, "expvar": true, "flag": true, "fmt": true,
	"go": true, "hash": true, "html": true, "image": true,
	"index": true, "internal": true, "io": true, "log": true,
	"maps": true, "math": true, "mime": true, "net": true,
	"os": true, "path": true, "plugin": true, "reflect": true,
	"regexp": true, "runtime": true, "slices": true, "sort": true,
	"strconv": true, "strings": true, "sync": true, "syscall": true,
	"testing": true, "text": true, "time": true, "unicode": true,
	"unsafe": true,
}

// Go builtin types
var goBuiltinTypes = map[string]bool{
	// Basic types
	"bool": true, "string": true,
	"int": true, "int8": true, "int16": true, "int32": true, "int64": true,
	"uint": true, "uint8": true, "uint16": true, "uint32": true, "uint64": true,
	"uintptr": true, "byte": true, "rune": true,
	"float32": true, "float64": true,
	"complex64": true, "complex128": true,
	"error": true, "any": true, "comparable": true,
}

func isBuiltinType(typeName string) bool {
	// Remove pointer, slice, map prefixes
	typeName = strings.TrimPrefix(typeName, "*")
	typeName = strings.TrimPrefix(typeName, "[]")
	if strings.HasPrefix(typeName, "map[") {
		return true // map types use builtins
	}

	// Check if it's a stdlib package reference
	if strings.Contains(typeName, ".") {
		parts := strings.SplitN(typeName, ".", 2)
		if goStdlibPackages[parts[0]] {
			return true
		}
	}

	return goBuiltinTypes[typeName]
}

func isStdlibPackage(pkgPath string) bool {
	if pkgPath == "" {
		return false
	}
	// Get the first path component
	parts := strings.SplitN(pkgPath, "/", 2)
	return goStdlibPackages[parts[0]]
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
		// Root types: entry points (from package exports) with methods, or unreferenced types with operations
		if (s.EntryPoint && len(s.Methods) > 0) || (!isReferenced && (len(s.Methods) > 0 || referencesOperations)) {
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

	// Build set of known client type names for local type inference
	clientNames := make(map[string]bool)
	for name := range clientMethods {
		clientNames[name] = true
	}

	// Build method and function return type maps from API data for precise factory/getter resolution
	methodReturnTypeMap := buildMethodReturnTypeMap(usageStructs, usageInterfaces, clientNames)
	functionReturnTypeMap := buildFunctionReturnTypeMap(&apiIndex, clientNames)
	fieldTypeMap := buildFieldTypeMap(usageStructs, clientNames)

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

		// Build variable → client type map for this file
		varTypes := buildVarTypeMap(f, clientNames, methodReturnTypeMap, functionReturnTypeMap, fieldTypeMap)

		// Walk AST looking for method calls - resolve receiver type via var tracking first
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

			// Strategy 1: Resolve receiver type from local variable tracking
			var resolvedClient string
			if ident, ok := sel.X.(*ast.Ident); ok {
				if varType, exists := varTypes[ident.Name]; exists {
					if methods, ok := clientMethods[varType]; ok {
						if _, hasMethod := methods[methodName]; hasMethod {
							resolvedClient = varType
						}
					}
				}
			}

			// Strategy 1b: Chained call — receiver.GetSubClient().Method()
			if resolvedClient == "" {
				if innerCall, ok := sel.X.(*ast.CallExpr); ok {
					if innerSel, ok := innerCall.Fun.(*ast.SelectorExpr); ok {
						innerMethodName := innerSel.Sel.Name

						// Static factory: ClientType.Create().Method()
						if ident, ok := innerSel.X.(*ast.Ident); ok && clientNames[ident.Name] {
							staticKey := ident.Name + "." + innerMethodName
							if retType, exists := methodReturnTypeMap[staticKey]; exists {
								if methods, ok := clientMethods[retType]; ok {
									if _, hasMethod := methods[methodName]; hasMethod {
										resolvedClient = retType
									}
								}
							}
						}

						// Instance method: service.GetSubClient().Method()
						if resolvedClient == "" {
							if ident, ok := innerSel.X.(*ast.Ident); ok {
								if receiverType, exists := varTypes[ident.Name]; exists {
									methodKey := receiverType + "." + innerMethodName
									if retType, exists := methodReturnTypeMap[methodKey]; exists {
										if methods, ok := clientMethods[retType]; ok {
											if _, hasMethod := methods[methodName]; hasMethod {
												resolvedClient = retType
											}
										}
									}
								}
							}
						}
					}
				}
			}

			if resolvedClient != "" {
				key := resolvedClient + "." + methodName
				if !seenOps[key] {
					seenOps[key] = true
					pos := fset.Position(call.Pos())
					covered = append(covered, CoveredOp{
						Client: resolvedClient,
						Method: methodName,
						File:   relPath,
						Line:   pos.Line,
					})
				}
			}

			return true
		})

		// Detect patterns using purely structural AST analysis — no keyword matching
		ast.Inspect(f, func(n ast.Node) bool {
			switch n.(type) {
			case *ast.DeferStmt:
				patterns["defer-cleanup"] = true
			case *ast.GoStmt:
				patterns["goroutine"] = true
			case *ast.SelectStmt:
				patterns["channel-select"] = true
			}
			return true
		})
	}

	// Build bidirectional interface ↔ struct mapping for coverage cross-referencing
	ifaceToImplNames := make(map[string][]string)
	implToIfaceNames := make(map[string][]string)
	for ifaceName, impls := range interfaceImplementers {
		for _, impl := range impls {
			ifaceToImplNames[ifaceName] = append(ifaceToImplNames[ifaceName], impl.Name)
			implToIfaceNames[impl.Name] = append(implToIfaceNames[impl.Name], ifaceName)
		}
	}

	// Build uncovered list with interface/implementation cross-referencing
	uncovered := []UncoveredOp{}
	for clientName, methods := range clientMethods {
		for method, sig := range methods {
			key := clientName + "." + method
			if seenOps[key] {
				continue
			}

			// Check if covered through an interface/implementation relationship
			coveredViaRelated := false

			// If this is an implementation, check if any of its interfaces has the method covered
			for _, ifaceName := range implToIfaceNames[clientName] {
				if seenOps[ifaceName+"."+method] {
					coveredViaRelated = true
					break
				}
			}

			// If this is an interface, check if any implementation has the method covered
			if !coveredViaRelated {
				for _, implName := range ifaceToImplNames[clientName] {
					if seenOps[implName+"."+method] {
						coveredViaRelated = true
						break
					}
				}
			}

			if !coveredViaRelated {
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

// =============================================================================
// Variable Tracking — API-data-driven type resolution
// =============================================================================

// unwrapGoReturnType strips pointer, slice, and multi-return from Go return types.
// E.g., "*BlobClient" → "BlobClient", "(*BlobClient, error)" → "BlobClient"
func unwrapGoReturnType(ret string) string {
	// Handle multi-return: "(Type, error)" → "Type"
	ret = strings.TrimSpace(ret)
	if strings.HasPrefix(ret, "(") && strings.HasSuffix(ret, ")") {
		inner := ret[1 : len(ret)-1]
		parts := strings.Split(inner, ",")
		if len(parts) > 0 {
			ret = strings.TrimSpace(parts[0])
		}
	}
	// Strip pointer and slice prefixes
	ret = strings.TrimPrefix(ret, "*")
	ret = strings.TrimPrefix(ret, "[]")
	ret = strings.TrimPrefix(ret, "*")
	// Strip generic type args
	if idx := strings.Index(ret, "["); idx > 0 {
		ret = ret[:idx]
	}
	return ret
}

// buildMethodReturnTypeMap builds a map of "OwnerType.MethodName" → return type
// from API method data, only for methods that return a known client type.
func buildMethodReturnTypeMap(structs []StructApi, ifaces []IfaceApi, clientNames map[string]bool) map[string]string {
	m := make(map[string]string)
	for _, s := range structs {
		for _, method := range s.Methods {
			if method.Ret != "" {
				retType := unwrapGoReturnType(method.Ret)
				if clientNames[retType] {
					m[s.Name+"."+method.Name] = retType
				}
			}
		}
	}
	for _, iface := range ifaces {
		for _, method := range iface.Methods {
			if method.Ret != "" {
				retType := unwrapGoReturnType(method.Ret)
				if clientNames[retType] {
					m[iface.Name+"."+method.Name] = retType
				}
			}
		}
	}
	return m
}

// buildFunctionReturnTypeMap builds a map of "FunctionName" → return type
// from API function data, only for functions that return a known client type.
func buildFunctionReturnTypeMap(api *ApiIndex, clientNames map[string]bool) map[string]string {
	m := make(map[string]string)
	for _, pkg := range api.Packages {
		for _, fn := range pkg.Functions {
			if fn.Ret != "" {
				retType := unwrapGoReturnType(fn.Ret)
				if clientNames[retType] {
					m[fn.Name] = retType
				}
			}
		}
	}
	return m
}

// buildFieldTypeMap builds a map of "OwnerType.FieldName" → client type
// from API field data, for fields whose type is a known client type.
func buildFieldTypeMap(structs []StructApi, clientNames map[string]bool) map[string]string {
	m := make(map[string]string)
	for _, s := range structs {
		for _, f := range s.Fields {
			fieldType := strings.TrimPrefix(f.Type, "*")
			fieldType = strings.TrimPrefix(fieldType, "[]")
			if idx := strings.Index(fieldType, "["); idx > 0 {
				fieldType = fieldType[:idx]
			}
			if clientNames[fieldType] {
				m[s.Name+"."+f.Name] = fieldType
			}
		}
	}
	return m
}

// buildVarTypeMap walks a Go AST file and builds a variable → client type map.
//
// Tracks patterns:
//   - client := NewBlobClient(...)       → client maps to BlobClient (constructor via function return type map)
//   - client := svc.GetBlobClient(...)   → client maps to BlobClient (method return type map)
//   - var client BlobClient              → client maps to BlobClient (type annotation)
//   - client := svc.BlobField            → client maps to BlobClient (field type map)
//
// All type resolution is driven by API index data — no name-based heuristics.
func buildVarTypeMap(f *ast.File, clientNames map[string]bool, methodRetMap, funcRetMap, fieldTypeMap map[string]string) map[string]string {
	varTypes := make(map[string]string)

	ast.Inspect(f, func(n ast.Node) bool {
		switch node := n.(type) {
		case *ast.GenDecl:
			// Handle: var client BlobClient
			for _, spec := range node.Specs {
				vs, ok := spec.(*ast.ValueSpec)
				if !ok {
					continue
				}
				// Type annotation: var client BlobClient
				if vs.Type != nil {
					typeName := unwrapGoReturnType(formatExpr(vs.Type))
					if clientNames[typeName] {
						for _, name := range vs.Names {
							varTypes[name.Name] = typeName
						}
						continue
					}
				}
				// Initializer: var client = NewBlobClient(...)
				if len(vs.Values) > 0 && len(vs.Names) > 0 {
					for i, val := range vs.Values {
						if i >= len(vs.Names) {
							break
						}
						resolved := resolveExprType(val, clientNames, varTypes, methodRetMap, funcRetMap, fieldTypeMap)
						if resolved != "" {
							varTypes[vs.Names[i].Name] = resolved
						}
					}
				}
			}

		case *ast.AssignStmt:
			// Handle: client := NewBlobClient(...) or client = svc.GetBlobClient(...)
			for i, rhs := range node.Rhs {
				if i >= len(node.Lhs) {
					break
				}
				ident, ok := node.Lhs[i].(*ast.Ident)
				if !ok {
					continue
				}
				resolved := resolveExprType(rhs, clientNames, varTypes, methodRetMap, funcRetMap, fieldTypeMap)
				if resolved != "" {
					varTypes[ident.Name] = resolved
				}
			}
		}
		return true
	})

	return varTypes
}

// resolveExprType resolves the client type of an expression using API data.
func resolveExprType(expr ast.Expr, clientNames map[string]bool, varTypes, methodRetMap, funcRetMap, fieldTypeMap map[string]string) string {
	switch e := expr.(type) {
	case *ast.CallExpr:
		// Function call: NewBlobClient(...)
		if ident, ok := e.Fun.(*ast.Ident); ok {
			if retType, exists := funcRetMap[ident.Name]; exists {
				return retType
			}
		}
		// Method call: svc.GetBlobClient(...)
		if sel, ok := e.Fun.(*ast.SelectorExpr); ok {
			methodName := sel.Sel.Name
			if ident, ok := sel.X.(*ast.Ident); ok {
				// Static factory: ClientType.Create(...)
				if clientNames[ident.Name] {
					staticKey := ident.Name + "." + methodName
					if retType, exists := methodRetMap[staticKey]; exists {
						return retType
					}
				}
				// Instance method: service.GetSubClient(...)
				if receiverType, exists := varTypes[ident.Name]; exists {
					methodKey := receiverType + "." + methodName
					if retType, exists := methodRetMap[methodKey]; exists {
						return retType
					}
				}
			}
		}

	case *ast.UnaryExpr:
		// Address-of: &BlobClient{} or dereference
		if e.Op.String() == "&" {
			return resolveExprType(e.X, clientNames, varTypes, methodRetMap, funcRetMap, fieldTypeMap)
		}

	case *ast.CompositeLit:
		// Struct literal: BlobClient{...}
		if e.Type != nil {
			typeName := unwrapGoReturnType(formatExpr(e.Type))
			if clientNames[typeName] {
				return typeName
			}
		}

	case *ast.SelectorExpr:
		// Field access: svc.BlobField
		if ident, ok := e.X.(*ast.Ident); ok {
			if receiverType, exists := varTypes[ident.Name]; exists {
				fieldKey := receiverType + "." + e.Sel.Name
				if fieldType, exists := fieldTypeMap[fieldKey]; exists {
					return fieldType
				}
			}
		}

	case *ast.Ident:
		// Direct identifier reference to a known client type
		if clientNames[e.Name] {
			return e.Name
		}
	}
	return ""
}

func extractPackage(rootPath string) (*ApiIndex, error) {
	absPath, err := filepath.Abs(rootPath)
	if err != nil {
		return nil, err
	}

	packageName := detectPackageName(absPath)
	packages := make(map[string]*PackageApi)

	// Reset the type collector and import map for this extraction
	typeCollector = NewTypeReferenceCollector()
	importMap = make(map[string]string)

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

		// Collect import mappings from all files in the package
		for _, astPkg := range pkgs {
			for _, file := range astPkg.Files {
				collectImports(file)
			}
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

	// Mark entry points: types in the root package are the primary entry points
	// The root package is the one whose relDir is "." or empty (directly in the
	// module root), matching how Go users import the module.
	for _, pkgApi := range packages {
		if pkgApi.Name == "." || pkgApi.Name == "" || pkgApi.Name == filepath.Base(absPath) {
			for i := range pkgApi.Structs {
				pkgApi.Structs[i].EntryPoint = true
			}
			for i := range pkgApi.Interfaces {
				pkgApi.Interfaces[i].EntryPoint = true
			}
			for i := range pkgApi.Functions {
				pkgApi.Functions[i].EntryPoint = true
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

	api := &ApiIndex{
		Package:  packageName,
		Packages: sortedPkgs,
	}

	// Resolve transitive dependencies
	deps := resolveTransitiveDependencies()
	if len(deps) > 0 {
		api.Dependencies = deps
	}

	return api, nil
}

// =============================================================================
// Transitive Dependency Resolution (AST-Based)
// =============================================================================

// TypeReferenceCollector collects type references from AST expressions
type TypeReferenceCollector struct {
	refs         map[string]bool
	definedTypes map[string]bool
}

func NewTypeReferenceCollector() *TypeReferenceCollector {
	return &TypeReferenceCollector{
		refs:         make(map[string]bool),
		definedTypes: make(map[string]bool),
	}
}

func (c *TypeReferenceCollector) AddDefinedType(name string) {
	c.definedTypes[name] = true
}

// CollectFromExpr walks an AST expression and collects all type references.
// This is a rigorous AST-based approach that properly handles:
// - Identifiers: MyType
// - Selectors: pkg.Type
// - Pointers: *Type
// - Slices: []Type
// - Maps: map[Key]Value
// - Generics: Type[Arg]
// - Function types: func(A) B
func (c *TypeReferenceCollector) CollectFromExpr(expr ast.Expr) {
	if expr == nil {
		return
	}
	switch e := expr.(type) {
	case *ast.Ident:
		// Simple type name like "MyType"
		if isExported(e.Name) && !isBuiltinType(e.Name) {
			c.refs[e.Name] = true
		}
	case *ast.SelectorExpr:
		// Qualified name like "pkg.Type"
		if ident, ok := e.X.(*ast.Ident); ok {
			fullName := ident.Name + "." + e.Sel.Name
			if !isBuiltinType(fullName) && !isStdlibPackage(ident.Name) {
				c.refs[fullName] = true
			}
		}
	case *ast.StarExpr:
		// Pointer type like *Type
		c.CollectFromExpr(e.X)
	case *ast.ArrayType:
		// Slice/array type like []Type
		c.CollectFromExpr(e.Elt)
	case *ast.MapType:
		// Map type like map[Key]Value
		c.CollectFromExpr(e.Key)
		c.CollectFromExpr(e.Value)
	case *ast.ChanType:
		// Channel type like chan Type
		c.CollectFromExpr(e.Value)
	case *ast.FuncType:
		// Function type like func(A) B
		if e.Params != nil {
			for _, field := range e.Params.List {
				c.CollectFromExpr(field.Type)
			}
		}
		if e.Results != nil {
			for _, field := range e.Results.List {
				c.CollectFromExpr(field.Type)
			}
		}
	case *ast.IndexExpr:
		// Generic type like Type[Arg]
		c.CollectFromExpr(e.X)
		c.CollectFromExpr(e.Index)
	case *ast.IndexListExpr:
		// Multi-parameter generic like Type[A, B]
		c.CollectFromExpr(e.X)
		for _, idx := range e.Indices {
			c.CollectFromExpr(idx)
		}
	case *ast.Ellipsis:
		// Variadic like ...Type
		c.CollectFromExpr(e.Elt)
	case *ast.InterfaceType:
		// interface{} - no types to collect
	case *ast.StructType:
		// Anonymous struct type
		if e.Fields != nil {
			for _, field := range e.Fields.List {
				c.CollectFromExpr(field.Type)
			}
		}
	}
}

// CollectFromFieldList collects type references from a list of fields (params, results).
func (c *TypeReferenceCollector) CollectFromFieldList(fl *ast.FieldList) {
	if fl == nil {
		return
	}
	for _, field := range fl.List {
		c.CollectFromExpr(field.Type)
	}
}

// GetExternalRefs returns type references that are not locally defined.
func (c *TypeReferenceCollector) GetExternalRefs() map[string]bool {
	result := make(map[string]bool)
	for typeName := range c.refs {
		baseName := typeName
		if strings.Contains(typeName, ".") {
			parts := strings.SplitN(typeName, ".", 2)
			baseName = parts[1]
		}
		if !c.definedTypes[baseName] && !isBuiltinType(typeName) {
			result[typeName] = true
		}
	}
	return result
}

// Global collector (reset per extraction)
var typeCollector = NewTypeReferenceCollector()

// importMap maps package aliases to full import paths (collected during extraction)
var importMap = make(map[string]string)

// collectImports extracts import alias -> path mappings from a Go file's AST.
// This enables rigorous resolution of package aliases to their full import paths.
func collectImports(file *ast.File) {
	for _, imp := range file.Imports {
		// Get the import path (strip quotes)
		importPath := strings.Trim(imp.Path.Value, "\"")

		// Determine the alias used in code
		var alias string
		if imp.Name != nil {
			// Explicit alias: import foo "github.com/example/pkg"
			alias = imp.Name.Name
			if alias == "_" || alias == "." {
				continue // blank import or dot import, skip
			}
		} else {
			// Default alias is the last path component
			parts := strings.Split(importPath, "/")
			alias = parts[len(parts)-1]
		}

		// Map alias to full path (later imports override earlier ones, which is correct Go behavior)
		importMap[alias] = importPath
	}
}

func collectTypeReferences() map[string]bool {
	// Use the AST-collected references instead
	return typeCollector.GetExternalRefs()
}

func resolveTransitiveDependencies() []DependencyInfo {
	refs := collectTypeReferences()
	if len(refs) == 0 {
		return nil
	}

	// Group external types by their resolved import path
	depTypes := make(map[string][]string) // full import path -> type names

	for typeName := range refs {
		if strings.Contains(typeName, ".") {
			parts := strings.SplitN(typeName, ".", 2)
			pkgAlias := parts[0]
			typeBaseName := parts[1]

			// Resolve alias to full import path using collected imports
			fullPath := pkgAlias
			if resolved, ok := importMap[pkgAlias]; ok {
				fullPath = resolved
			}

			if !isStdlibPackage(fullPath) {
				depTypes[fullPath] = append(depTypes[fullPath], typeBaseName)
			}
		}
	}

	// Convert to DependencyInfo list
	var deps []DependencyInfo
	for pkgPath, types := range depTypes {
		dep := DependencyInfo{Package: pkgPath}
		for _, t := range types {
			dep.Types = append(dep.Types, TypeApi{Name: t})
		}
		deps = append(deps, dep)
	}

	// Sort by package name
	sort.Slice(deps, func(i, j int) bool {
		return deps[i].Package < deps[j].Package
	})

	return deps
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
				// Type alias - collect type reference and register as defined
				typeCollector.AddDefinedType(t.Name)
				typeCollector.CollectFromExpr(ts.Type)
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

	// Register as defined type
	typeCollector.AddDefinedType(t.Name)

	// Fields
	for _, field := range st.Fields.List {
		// Collect type references from AST
		typeCollector.CollectFromExpr(field.Type)

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

	// Register as defined type
	typeCollector.AddDefinedType(t.Name)

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
			// Collect type references from method params and results
			typeCollector.CollectFromFieldList(ft.Params)
			typeCollector.CollectFromFieldList(ft.Results)

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
	// Collect type references from params and results
	typeCollector.CollectFromFieldList(decl.Type.Params)
	typeCollector.CollectFromFieldList(decl.Type.Results)

	f := FuncApi{
		Name: decl.Name.Name,
		Sig:  formatParams(decl.Type.Params),
		Ret:  formatResults(decl.Type.Results),
		Doc:  firstLine(docStr),
	}

	if decl.Recv != nil && len(decl.Recv.List) > 0 {
		f.IsMethod = true
		f.Receiver = formatExpr(decl.Recv.List[0].Type)
		// Collect receiver type reference
		typeCollector.CollectFromFieldList(decl.Recv)
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
