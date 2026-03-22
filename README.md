# Polygon Optimizer

A WPF 3D mesh optimization tool for selecting and removing non-visible triangles. Built on [HelixToolkit.Wpf.SharpDX](https://github.com/helix-toolkit/helix-toolkit).

## Features

- **Triangle selection**: Single click, rectangle drag, flood-fill (connected), and view-based occlusion selection
- **Octahedron view iteration**: Automatically selects visible triangles from multiple camera angles using a geodesic dome
- **UV map view**: Interactive UV viewport with selection, rectangle drag, pan/zoom
- **Triangle deletion**: Delete selected triangles with full undo support
- **Scene graph editing**: Set any node as root to remove unwanted hierarchy
- **Material visibility**: Toggle visibility per material ID with color-coded display
- **Export**: Save optimized mesh in the original format with `_reduced` suffix
- **Batch processing**: Command-line automation for processing single or multiple files

## Build

Requires .NET 8.0 SDK and Windows 10/11.

```bash
dotnet build
dotnet run --project PolygonOptimizerCore
```

## Command Line

### Open a file on startup

```bash
PolygonOptimizerCore.exe model.fbx
```

### Batch processing (automated)

Process files silently with default settings: loads the file, runs Iterate Views to detect visible triangles, inverts selection, deletes non-visible triangles, and exports the result as `<filename>_reduced.<ext>` in the same folder.

```bash
# Single file
PolygonOptimizerCore.exe --batch model.fbx

# Multiple files
PolygonOptimizerCore.exe --batch model1.fbx model2.fbx model3.obj

# Wildcard pattern
PolygonOptimizerCore.exe --batch *.fbx

# All supported files in a folder
PolygonOptimizerCore.exe --batch --folder ./models/

# Combine folder and individual files
PolygonOptimizerCore.exe --batch --folder ./models/ extra.fbx
```

Supported file formats: `.fbx`, `.obj`, `.gltf`, `.glb`, `.dae`, `.3ds`, `.ply`, `.stl`

### Batch output

```
Batch processing 3 file(s)...

[character.fbx]
  Triangles: 12000
  Iterate Views... 8500 visible
  Invert + Delete... 8500 remaining
  Export... ./character_reduced.fbx
[environment.fbx]
  Triangles: 45000
  Iterate Views... 32000 visible
  Invert + Delete... 32000 remaining
  Export... ./environment_reduced.fbx
[prop.obj]
  Triangles: 3000
  Iterate Views... 2200 visible
  Invert + Delete... 2200 remaining
  Export... ./prop_reduced.obj

Done. Processed 3 file(s).
```

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| Delete | Delete selected triangles |
| B / F / U / D / L / R | Back / Front / Up / Down / Left / Right view |
| Ctrl+E | Zoom extents |

## Mouse Controls

### 3D Viewport
| Input | Action |
|-------|--------|
| Left click | Select triangle (Single mode) |
| Left drag | Rectangle selection (Single mode) |
| Shift+click | Deselect triangle |
| Right drag | Rotate camera |
| Middle drag | Zoom |

### UV Viewport
| Input | Action |
|-------|--------|
| Left click | Select triangle |
| Left drag | Rectangle selection |
| Shift+click | Deselect triangle |
| Middle drag | Pan |
| Scroll wheel | Zoom |

## License

MIT
