# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

PolygonOptimizer is a C# WPF 3D visualization and mesh optimization tool built on **HelixToolkit.Wpf.SharpDX**. It provides triangle-level selection, view-based occlusion selection, and material visibility control for polygon optimization workflows.

## Build & Run Commands

```bash
# Build entire solution
dotnet build

# Build release
dotnet build -c Release

# Run the main application
dotnet run --project PolygonOptimizerCore

# Run the demo app
dotnet run --project FileLoadDemo
```

No test framework is currently configured.

## Solution Structure

Three projects in `PolygonOptimizer.sln`:

- **DemoCore** — Shared class library (targets net8.0-windows + net48). Contains `BaseViewModel` (abstract MVVM base class using CommunityToolkit.Mvvm) and WPF converters.
- **FileLoadDemo** — Simpler WPF app for loading/rendering 3D models with animation playback. Based on HelixToolkit examples.
- **PolygonOptimizerCore** — Main production app. Adds triangle selection modes, view-based selection with occlusion testing, octahedron view iteration, material visibility toggling, and X-Ray mode.

## Architecture

**MVVM pattern** throughout: `MainWindow.xaml` binds to `MainViewModel.cs` which manages the HelixToolkit 3D scene graph.

Key architectural concepts in PolygonOptimizerCore:
- **Selection modes** (Navigate/Single/View): Single mode picks individual triangles via hit-testing; View mode selects all front-facing unoccluded triangles in the viewport.
- **Octahedron iteration**: Geodesic dome vertices define camera positions. `IterateViewsCommand` async-loops through vertices, selecting visible triangles at each view.
- **Material visibility**: `MaterialItem` list controls per-material show/hide. Toggling rebuilds the visible mesh and clears selection state for hidden geometry.
- **Scene node hierarchy**: `AttachedNodeViewModel` wraps each `SceneNode` for tree-view display with selection highlighting.

**Threading model**: Model loading runs on background threads via `Task.Run`. Animation uses `CompositionTargetEx` for frame-sync. View iteration uses async/await with configurable render delays.

## Key Dependencies

- **HelixToolkit.Wpf.SharpDX** (v3.1.1) — 3D rendering engine (DirectX/SharpDX)
- **HelixToolkit.SharpDX.Assimp** (v3.1.1) — Model import/export (FBX, OBJ, GLTF, etc.)
- **CommunityToolkit.Mvvm** (v8.3.2) — Observable properties, RelayCommand

## Global Usings

`GlobalUsing.cs` at solution root defines shared type aliases:
```csharp
global using Matrix = System.Numerics.Matrix4x4;
```
This alias is used across all projects — do not introduce conflicting Matrix types.
