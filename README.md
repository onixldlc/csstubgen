# csstubgen

Generate minimal C# reference stubs for Unity mod CI builds. Strips game DLLs to reference-only assemblies, then analyzes your mod source to produce the smallest possible stubs needed for compilation — no full game API exposure.

## How it works

1. **Strip** — Game DLLs are run through [JetBrains Refasmer](https://github.com/AShifter/refasmer) inside a container, producing reference-only assemblies with method bodies removed.
2. **Analyze** — Your mod `.cs` source is parsed with Roslyn (syntax-only, no compilation) to extract referenced types and members.
3. **Resolve** — Referenced types are matched against stripped DLLs using Mono.Cecil, walking the dependency graph (base types, field types, method signatures).
4. **Generate** — Minimal stub `.cs` files and `.csproj` projects are emitted, containing only the types and members your mod actually uses.

## Requirements

- Go 1.21+
- Podman or Docker

## Install

```sh
go install github.com/onixldlc/csstubgen@latest
```

## Usage

### 1. Strip game DLLs

```sh
cd /path/to/game/Managed
csstubgen dll --name nuclear-option-v3.3
```

Strips all `.dll` files in the current directory (or `-d <dir>`) and stores them in `~/.local/share/csstubgen/nuclear-option-v3.3/`.

### 2. Generate stubs

```sh
cd /path/to/mod
csstubgen generate --name nuclear-option-v3.3 -s ./src/ -o ./ci/stubs/
```

Analyzes source files, resolves against stored stripped DLLs, and writes minimal stubs to the output directory.

### 3. List available DLL sets

```sh
csstubgen list
```

### 4. Force rebuild container image

```sh
csstubgen build
```

## Options

| Flag | Description |
|------|-------------|
| `--name`, `-n` | Name for the DLL set (e.g. `nuclear-option-v3.3`) |
| `-d` | Game DLL directory (default: cwd) |
| `-s`, `--source` | Mod source `.cs` files or directory (repeatable) |
| `-o`, `--out` | Output directory for stubs (default: `./stubs`) |
| `--unity-version` | UnityEngine.Modules NuGet version (default: `2022.3.9`) |

## Architecture

The Go binary is a thin CLI wrapper. It embeds the Dockerfile and C# source, auto-builds the container image on first run, and manages volume mounts. All heavy lifting (Roslyn parsing, Cecil resolution, stub generation) happens inside the container.

```
csstubgen (Go CLI)
├── dll       → container: Refasmer strips DLLs → ~/.local/share/csstubgen/<name>/
└── generate  → container: Roslyn + Cecil → minimal stubs
```

Game DLLs are always mounted read-only.
