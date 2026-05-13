package main

import (
	"embed"
	"encoding/json"
	"fmt"
	"io/fs"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

//go:embed Dockerfile entrypoint.sh .dockerignore src/*
var dockerContext embed.FS

const (
	defaultImage = "ghcr.io/onixldlc/csstubgen:latest"
	localImage   = "csstubgen"
)

type Config struct {
	Image string `json:"image,omitempty"`
}

func main() {
	if len(os.Args) < 2 {
		printUsage()
		os.Exit(1)
	}

	switch os.Args[1] {
	case "dll":
		cmdDll(os.Args[2:])
	case "generate", "gen":
		cmdGenerate(os.Args[2:])
	case "list", "ls":
		listAvailable()
	case "image":
		cmdImage(os.Args[2:])
	case "build":
		mustBuildImage()
	case "help", "-h", "--help":
		printUsage()
	default:
		fmt.Fprintf(os.Stderr, "Unknown command: %s\n", os.Args[1])
		printUsage()
		os.Exit(1)
	}
}

// --- config ---

func configDir() string {
	if xdg := os.Getenv("XDG_CONFIG_HOME"); xdg != "" {
		return filepath.Join(xdg, "csstubgen")
	}
	home, _ := os.UserHomeDir()
	return filepath.Join(home, ".config", "csstubgen")
}

func configPath() string {
	return filepath.Join(configDir(), "config.json")
}

func loadConfig() Config {
	data, err := os.ReadFile(configPath())
	if err != nil {
		return Config{}
	}
	var cfg Config
	json.Unmarshal(data, &cfg)
	return cfg
}

func saveConfig(cfg Config) {
	dir := configDir()
	os.MkdirAll(dir, 0755)
	data, _ := json.MarshalIndent(cfg, "", "  ")
	if err := os.WriteFile(configPath(), data, 0644); err != nil {
		fatal("can't write config: %v", err)
	}
}

func resolveImage(override string) string {
	if override != "" {
		return override
	}
	cfg := loadConfig()
	if cfg.Image != "" {
		return cfg.Image
	}
	return defaultImage
}

// --- image: manage container images ---

func cmdImage(args []string) {
	if len(args) > 0 && args[0] != "ls" {
		cfg := loadConfig()
		cfg.Image = args[0]
		saveConfig(cfg)
		fmt.Printf("[csstubgen] Image set to: %s\n", args[0])
		return
	}

	rt := findRuntime()
	cfg := loadConfig()
	active := cfg.Image
	if active == "" {
		active = defaultImage
	}

	fmt.Println("Container images:")
	fmt.Println()

	if imageExists(rt, defaultImage) {
		marker := "  "
		if active == defaultImage {
			marker = "* "
		}
		fmt.Printf("  %s%-50s (default)\n", marker, defaultImage)
	} else {
		marker := "  "
		if active == defaultImage {
			marker = "* "
		}
		fmt.Printf("  %s%-50s (default, not pulled)\n", marker, defaultImage)
	}

	if imageExists(rt, localImage) {
		marker := "  "
		if active == localImage {
			marker = "* "
		}
		fmt.Printf("  %s%-50s (local build)\n", marker, localImage)
	}

	fmt.Println()
	fmt.Printf("Active: %s\n", active)
	fmt.Println()
	fmt.Println("Set image:   csstubgen image <name>")
	fmt.Println("Build local: csstubgen build")
}

// --- dll: strip game DLLs and store ---

func cmdDll(args []string) {
	var name, dllDir, image string

	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--name", "-n":
			i++
			name = args[i]
		case "-d":
			i++
			dllDir = args[i]
		case "--image", "-i":
			i++
			image = args[i]
		case "-h", "--help":
			fmt.Println("Usage: csstubgen dll --name <name> [-d <dll-dir>] [--image <image>]")
			fmt.Println("  Strips game DLLs and stores in ~/.local/share/csstubgen/<name>/")
			fmt.Println("  -d defaults to current directory")
			fmt.Println("  --image overrides configured container image")
			return
		}
	}

	if name == "" {
		fatal("--name required\nUsage: csstubgen dll --name <name> [-d <dll-dir>]")
	}
	if dllDir == "" {
		dllDir, _ = os.Getwd()
	}

	dllDir, _ = filepath.Abs(dllDir)
	if _, err := os.Stat(dllDir); os.IsNotExist(err) {
		fatal("DLL directory not found: %s", dllDir)
	}

	matches, _ := filepath.Glob(filepath.Join(dllDir, "*.dll"))
	if len(matches) == 0 {
		fatal("No .dll files in %s", dllDir)
	}
	fmt.Printf("[csstubgen] Found %d DLLs in %s\n", len(matches), dllDir)

	storeDir := filepath.Join(dataDir(), name)
	os.MkdirAll(storeDir, 0755)

	rt := findRuntime()
	img := resolveImage(image)
	ensureImage(rt, img)

	cmd := exec.Command(rt, "run", "--rm",
		"-v", dllDir+":/input:ro,z",
		"-v", storeDir+":/output:z",
		img, "strip",
	)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr

	if err := cmd.Run(); err != nil {
		fatal("strip failed: %v", err)
	}

	fmt.Printf("[csstubgen] Stripped DLLs stored in %s\n", storeDir)
}

// --- generate: create stubs from source + stripped DLLs ---

func cmdGenerate(args []string) {
	var name, outDir, unityVer, image string
	var sources []string
	var verbose bool
	outDir = "./stubs"
	unityVer = "2022.3.9"

	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--name", "-n":
			i++
			name = args[i]
		case "-s", "--source":
			i++
			sources = append(sources, args[i])
		case "-o", "--out":
			i++
			outDir = args[i]
		case "--unity-version":
			i++
			unityVer = args[i]
		case "--image", "-i":
			i++
			image = args[i]
		case "-v", "--verbose":
			verbose = true
		case "-h", "--help":
			fmt.Println("Usage: csstubgen generate --name <name> [-s <source>...] [-o <output>] [-v] [--image <image>]")
			fmt.Println("  Omit --name to list available stripped DLL sets")
			fmt.Println("  -v  show all buckets (bcl, self, unresolved) in addition to external")
			return
		default:
			if !strings.HasPrefix(args[i], "-") {
				sources = append(sources, args[i])
			}
		}
	}

	if name == "" {
		listAvailable()
		return
	}

	storeDir := filepath.Join(dataDir(), name)
	if _, err := os.Stat(storeDir); os.IsNotExist(err) {
		fatal("No stripped DLLs for '%s'\nRun first: csstubgen dll --name %s -d /path/to/game/dlls", name, name)
	}

	if len(sources) == 0 {
		fatal("No source files specified\nUsage: csstubgen generate --name %s -s <source> [-o <output>]", name)
	}

	cwd, _ := os.Getwd()
	rt := findRuntime()
	img := resolveImage(image)
	ensureImage(rt, img)

	containerArgs := []string{
		"run", "--rm",
		"-v", cwd + ":/work:z",
		"-v", storeDir + ":/ref:ro,z",
		"-w", "/work",
		img, "generate",
	}

	for _, s := range sources {
		containerArgs = append(containerArgs, "-s", s)
	}
	containerArgs = append(containerArgs, "-r", "/ref", "-o", outDir, "--unity-version", unityVer)
	if verbose {
		containerArgs = append(containerArgs, "-v")
	}

	cmd := exec.Command(rt, containerArgs...)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr

	if err := cmd.Run(); err != nil {
		fatal("generate failed: %v", err)
	}
}

// --- list: show available stripped DLL sets ---

func listAvailable() {
	dir := dataDir()
	entries, err := os.ReadDir(dir)
	if err != nil || len(entries) == 0 {
		fmt.Println("No stripped DLL sets found.")
		fmt.Println("Strip game DLLs first: csstubgen dll --name <name> -d /path/to/dlls")
		return
	}

	fmt.Println("Available stripped DLL sets:")
	for _, e := range entries {
		if !e.IsDir() {
			continue
		}
		dlls, _ := filepath.Glob(filepath.Join(dir, e.Name(), "*.dll"))
		fmt.Printf("  %-35s %d DLLs\n", e.Name(), len(dlls))
	}
}

// --- build: container image management ---

func ensureImage(rt, image string) {
	if imageExists(rt, image) {
		return
	}
	fmt.Printf("[csstubgen] Image %s not found locally, pulling...\n", image)
	cmd := exec.Command(rt, "pull", image)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	if err := cmd.Run(); err != nil {
		fatal("Failed to pull %s: %v\nBuild locally with: csstubgen build", image, err)
	}
}

func mustBuildImage() {
	rt := findRuntime()
	contextDir := extractDockerContext()
	defer os.RemoveAll(contextDir)

	fmt.Println("[csstubgen] Building container image...")
	cmd := exec.Command(rt, "build", "-t", localImage, contextDir)
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr

	if err := cmd.Run(); err != nil {
		fatal("image build failed: %v", err)
	}
	fmt.Println("[csstubgen] Image built ✓")
	fmt.Printf("[csstubgen] Use with: csstubgen image %s\n", localImage)
}

func extractDockerContext() string {
	dir, err := os.MkdirTemp("", "csstubgen-build-*")
	if err != nil {
		fatal("can't create temp dir: %v", err)
	}

	fs.WalkDir(dockerContext, ".", func(path string, d fs.DirEntry, err error) error {
		if err != nil || path == "." {
			return err
		}
		outPath := filepath.Join(dir, path)
		if d.IsDir() {
			return os.MkdirAll(outPath, 0755)
		}
		data, err := dockerContext.ReadFile(path)
		if err != nil {
			return err
		}
		os.MkdirAll(filepath.Dir(outPath), 0755)
		return os.WriteFile(outPath, data, 0644)
	})

	return dir
}

// --- helpers ---

func findRuntime() string {
	for _, r := range []string{"podman", "docker"} {
		if _, err := exec.LookPath(r); err == nil {
			return r
		}
	}
	fatal("podman or docker required — neither found in PATH")
	return ""
}

func imageExists(rt, image string) bool {
	cmd := exec.Command(rt, "image", "inspect", image)
	cmd.Stdout = nil
	cmd.Stderr = nil
	return cmd.Run() == nil
}

func dataDir() string {
	if xdg := os.Getenv("XDG_DATA_HOME"); xdg != "" {
		return filepath.Join(xdg, "csstubgen")
	}
	home, _ := os.UserHomeDir()
	return filepath.Join(home, ".local", "share", "csstubgen")
}

func fatal(format string, args ...any) {
	fmt.Fprintf(os.Stderr, "Error: "+format+"\n", args...)
	os.Exit(1)
}

func printUsage() {
	fmt.Print(`csstubgen - Generate minimal C# stubs for Unity mod CI builds

Usage:
  csstubgen dll --name <name> [-d <dll-dir>] [--image <image>]
    Strip game DLLs, store in ~/.local/share/csstubgen/<name>/
    DLL directory mounted read-only. Defaults to cwd.

  csstubgen generate --name <name> [-s <source>...] [-o <output>] [--image <image>]
    Generate minimal stubs from mod source + stored stripped DLLs.
    Omit --name to list available stripped DLL sets.

  csstubgen list
    List available stripped DLL sets.

  csstubgen image [<name>]
    List available container images, or set active image.

  csstubgen build
    Build container image locally.

Options:
  --name, -n           Name for DLL set (e.g. nuclear-option-v3.3)
  -d                   Game DLL directory (default: cwd)
  -s, --source         Mod source .cs files or directory (repeatable)
  -o, --out            Output directory for stubs (default: ./stubs)
  --unity-version      UnityEngine.Modules version (default: 2022.3.9)
  --image, -i          Container image to use (default: ` + defaultImage + `)

Config: ~/.config/csstubgen/config.json

Examples:
  cd /path/to/game/dll && csstubgen dll --name nuclear-option-v3.3
  cd /path/to/mod && csstubgen generate --name nuclear-option-v3.3 -s ./*.cs
  csstubgen image csstubgen            # switch to local build
  csstubgen image ` + defaultImage + `  # switch back to default
`)
}
