using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Animations;
using HelixToolkit.SharpDX.Assimp;
using HelixToolkit.SharpDX.Model;
using HelixToolkit.SharpDX.Model.Scene;
using HelixToolkit.Wpf.SharpDX;
using HelixToolkit.Wpf.SharpDX.Controls;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;
using Point3D = System.Windows.Media.Media3D.Point3D;

namespace FileLoadDemo;

public enum SelectionMode
{
    Navigate,
    Single,
    View
}

public partial class MainViewModel : DemoCore.BaseViewModel
{
    private const int RenderWaitMs = 25;

    private readonly string OpenFileFilter = $"{HelixToolkit.SharpDX.Assimp.Importer.SupportedFormatsString}";
    private readonly string ExportFileFilter = $"{HelixToolkit.SharpDX.Assimp.Exporter.SupportedFormatsString}";

    [ObservableProperty]
    private MeshGeometry3D selectionMesh = new MeshGeometry3D()
    {
        Positions = new Vector3Collection(),
        Indices = new IntCollection()
    };

    [ObservableProperty]
    private Material selectionMaterial = new DiffuseMaterial() { DiffuseColor = Color.Yellow };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewSelectionMode))]
    private SelectionMode selectionMode = SelectionMode.Single;

    public bool IsViewSelectionMode => SelectionMode == SelectionMode.View;

    [ObservableProperty]
    private float occlusionThreshold = 2.0f;

    [ObservableProperty]
    private bool showOctahedron = false;

    [ObservableProperty]
    private int octahedronResolution = 4;

    partial void OnOctahedronResolutionChanged(int value)
    {
        RebuildOctahedron();
    }

    [ObservableProperty]
    private LineGeometry3D? octahedronLines;

    private List<Vector3> octahedronWorldVertices = new();
    private Vector3 octahedronCenter;

    // Key: (MeshNode, triangleLoc), Value: 3 world-space vertex positions (slightly offset to avoid z-fighting)
    private readonly Dictionary<(MeshNode, int), (Vector3, Vector3, Vector3)> selectedTriangles = new();

    [ObservableProperty]
    private int selectedTriangleCount = 0;

    [ObservableProperty]
    private int totalTriangleCount = 0;

    public string SelectionStats => TotalTriangleCount > 0
        ? string.Format("Selected: {0} / {1} ({2:F1}%)", SelectedTriangleCount, TotalTriangleCount, 100.0 * SelectedTriangleCount / TotalTriangleCount)
        : "Selected: 0 / 0 (0.0%)";

    partial void OnSelectedTriangleCountChanged(int value) => OnPropertyChanged(nameof(SelectionStats));
    partial void OnTotalTriangleCountChanged(int value) => OnPropertyChanged(nameof(SelectionStats));

    [ObservableProperty]
    private bool showWireframe = false;

    partial void OnShowWireframeChanged(bool value)
    {
        ShowWireframeFunct(value);
    }

    [ObservableProperty]
    private bool renderFlat = false;

    partial void OnRenderFlatChanged(bool value)
    {
        RenderFlatFunct(value);
    }

    [ObservableProperty]
    private bool xRayMode = false;

    partial void OnXRayModeChanged(bool value)
    {
        XRayModeFunct(value);
    }

    [ObservableProperty]
    private bool isLoading = false;

    [ObservableProperty]
    private bool isPlaying = false;

    [ObservableProperty]
    private float startTime;

    [ObservableProperty]
    private float endTime;

    [ObservableProperty]
    private float currAnimationTime = 0;

    partial void OnCurrAnimationTimeChanged(float value)
    {
        if (EndTime == 0)
        {
            return;
        }

        CurrAnimationTime = value % EndTime + StartTime;
        animationUpdater?.Update(value, 1);
    }

    public ObservableCollection<IAnimationUpdater> Animations { get; } = new();

    public ObservableCollection<MaterialItem> MaterialItems { get; } = new();

    public SceneNodeGroupModel3D GroupModel { get; } = new SceneNodeGroupModel3D();

    [ObservableProperty]
    private IAnimationUpdater? selectedAnimation = null;

    partial void OnSelectedAnimationChanged(IAnimationUpdater? value)
    {
        StopAnimation();
        CurrAnimationTime = 0;
        if (value is not null)
        {
            animationUpdater = value;
            animationUpdater.Reset();
            animationUpdater.RepeatMode = AnimationRepeatMode.Loop;
            StartTime = value.StartTime;
            EndTime = value.EndTime;
        }
        else
        {
            animationUpdater = null;
            StartTime = EndTime = 0;
        }
    }

    [ObservableProperty]
    private float speed = 1.0f;

    [ObservableProperty]
    private Point3D modelCentroid = default;

    [ObservableProperty]
    private BoundingBox modelBound = new();
    public TextureModel? EnvironmentMap { get; }

    private readonly SynchronizationContext? context = SynchronizationContext.Current;
    private HelixToolkitScene? scene;
    private IAnimationUpdater? animationUpdater;
    private List<BoneSkinMeshNode> boneSkinNodes = new();
    private List<BoneSkinMeshNode> skeletonNodes = new();
    private CompositionTargetEx compositeHelper = new();
    private long initTimeStamp = 0;

    private MainWindow? mainWindow = null;

    public MainViewModel(MainWindow? window)
    {
        mainWindow = window;

        EffectsManager = new DefaultEffectsManager();

        Camera = new OrthographicCamera()
        {
            LookDirection = new System.Windows.Media.Media3D.Vector3D(0, -10, -10),
            Position = new System.Windows.Media.Media3D.Point3D(0, 10, 10),
            UpDirection = new System.Windows.Media.Media3D.Vector3D(0, 1, 0),
            FarPlaneDistance = 5000,
            NearPlaneDistance = 0.1f
        };

        EnvironmentMap = TextureModel.Create("Cubemap_Grandcanyon.dds");
    }

    [RelayCommand]
    private void ResetCamera()
    {
        if (Camera is not OrthographicCamera c)
        {
            return;
        }

        c.Reset();
        c.FarPlaneDistance = 5000;
        c.NearPlaneDistance = 0.1f;
    }

    [RelayCommand]
    private void Play()
    {
        if (!IsPlaying && SelectedAnimation != null)
        {
            StartAnimation();
        }
        else
        {
            StopAnimation();
        }
    }

    [RelayCommand]
    private void CopyAsBitmap()
    {
        Viewport3DX? viewport = mainWindow?.view;

        if (viewport is null)
        {
            return;
        }

        var bitmap = ViewportExtensions.RenderBitmap(viewport);
        try
        {
            Clipboard.Clear();
            Clipboard.SetImage(bitmap);
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
    }

    [RelayCommand]
    private void CopyAsHiResBitmap()
    {
        Viewport3DX? viewport = mainWindow?.view;

        if (viewport is null)
        {
            return;
        }

        var stopwatch = new Stopwatch();
        stopwatch.Start();

        var bitmap = ViewportExtensions.RenderBitmap(viewport, 1920, 1080);
        try
        {
            Clipboard.Clear();
            Clipboard.SetImage(bitmap);
            stopwatch.Stop();
            Debug.WriteLine($"creating bitmap needs {stopwatch.ElapsedMilliseconds} ms");
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
        }
    }

    [RelayCommand]
    private void CloseFile()
    {
        StopAnimation();
        SelectedAnimation = null;
        Animations.Clear();
        ClearTriangleSelection();

        var oldNodes = GroupModel.SceneNode.Items.ToArray();
        GroupModel.Clear(false);
        Task.Run(() =>
        {
            foreach (var node in oldNodes)
                node.Dispose();
        });

        scene = null;
        MaterialItems.Clear();
        OctahedronLines = null;
        octahedronWorldVertices.Clear();

        ModelBound = new BoundingBox();
        ModelCentroid = default;

        ShowWireframe = false;
        RenderFlat = false;
        XRayMode = false;
        ShowOctahedron = false;
        OctahedronResolution = 4;
        SelectionMode = SelectionMode.Single;
        OcclusionThreshold = 2.0f;

        ResetCamera();
    }

    [RelayCommand]
    private void OpenFile()
    {
        if (IsLoading)
        {
            return;
        }

        string? path = OpenFileDialog(OpenFileFilter);
        if (path is null)
        {
            return;
        }

        StopAnimation();
        var syncContext = SynchronizationContext.Current;
        IsLoading = true;
        Task.Run(() =>
        {
            var loader = new Importer();
            var scene = loader.Load(path);

            if (scene is null)
            {
                return null;
            }

            scene.Root.Attach(EffectsManager); // Pre attach scene graph
            scene.Root.UpdateAllTransformMatrix();
            if (scene.Root.TryGetBound(out var bound))
            {
                // Must use UI thread to set value back.
                syncContext?.Post((o) => { ModelBound = bound; }, null);
            }
            if (scene.Root.TryGetCentroid(out var centroid))
            {
                // Must use UI thread to set value back.
                syncContext?.Post((o) => { ModelCentroid = centroid.ToPoint3D(); }, null);
            }
            return scene;
        }).ContinueWith((result) =>
        {
            IsLoading = false;
            if (result.IsCompleted)
            {
                scene = result.Result;

                if (scene is null)
                {
                    return;
                }

                Animations.Clear();
                ClearTriangleSelection();
                var oldNode = GroupModel.SceneNode.Items.ToArray();
                GroupModel.Clear(false);
                Task.Run(() =>
                {
                    foreach (var node in oldNode)
                    { node.Dispose(); }
                });
                if (scene is not null)
                {
                    if (scene.Root is not null)
                    {
                        foreach (var node in scene.Root.Traverse())
                        {
                            if (node is MaterialGeometryNode m)
                            {
                                //m.Geometry.SetAsTransient();
                                if (m.Material is PBRMaterialCore pbr)
                                {
                                    pbr.RenderEnvironmentMap = true;
                                }
                                else if (m.Material is PhongMaterialCore phong)
                                {
                                    phong.RenderEnvironmentMap = true;
                                }
                            }
                        }
                    }

                    if (scene.Root is not null)
                    {
                        GroupModel.AddNode(scene.Root);
                    }

                    if (scene.HasAnimation && scene.Animations is not null)
                    {
                        var dict = scene.Animations.CreateAnimationUpdaters();
                        foreach (var ani in dict.Values)
                        {
                            Animations.Add(ani);
                        }
                    }

                    if (scene.Root is not null)
                    {
                        foreach (var n in scene.Root.Traverse())
                        {
                            n.Tag = new AttachedNodeViewModel(n);
                        }
                    }

                    CollectMaterials();
                    RebuildOctahedron();

                    if (XRayMode)
                        XRayModeFunct(true);

                    FocusCameraToScene();
                }
            }
            else if (result.IsFaulted && result.Exception != null)
            {
                MessageBox.Show(result.Exception.Message);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void StartAnimation()
    {
        initTimeStamp = Stopwatch.GetTimestamp();
        compositeHelper.Rendering += CompositeHelper_Rendering;
        IsPlaying = true;
    }

    public void StopAnimation()
    {
        IsPlaying = false;
        compositeHelper.Rendering -= CompositeHelper_Rendering;
    }

    private void CompositeHelper_Rendering(object? sender, System.Windows.Media.RenderingEventArgs e)
    {
        if (animationUpdater is not null)
        {
            var elapsed = (Stopwatch.GetTimestamp() - initTimeStamp) * Speed;
            CurrAnimationTime = elapsed / Stopwatch.Frequency;
        }
    }

    private void FocusCameraToScene()
    {
        if (Camera is null)
        {
            return;
        }

        var maxWidth = Math.Max(Math.Max(ModelBound.Width, ModelBound.Height), ModelBound.Depth);
        var pos = ModelBound.Center + new Vector3(0, 0, maxWidth);
        Camera.Position = pos.ToPoint3D();
        Camera.LookDirection = (ModelBound.Center - pos).ToVector3D();
        Camera.UpDirection = Vector3.UnitY.ToVector3D();

        if (Camera is OrthographicCamera orthCam)
        {
            orthCam.Width = maxWidth;
        }
    }

    [RelayCommand]
    private void Export()
    {
        var index = SaveFileDialog(ExportFileFilter, out var path);

        if (!string.IsNullOrEmpty(path) && index >= 0)
        {
            var id = HelixToolkit.SharpDX.Assimp.Exporter.SupportedFormats[index].FormatId;
            var exporter = new HelixToolkit.SharpDX.Assimp.Exporter();
            exporter.ExportToFile(path, scene, id);
            return;
        }
    }


    private string? OpenFileDialog(string filter)
    {
        var d = new OpenFileDialog();
        d.CustomPlaces.Clear();

        d.Filter = filter;

        if (d.ShowDialog() == true)
        {
            return d.FileName;
        }

        return null;
    }

    private int SaveFileDialog(string filter, out string path)
    {
        var d = new SaveFileDialog
        {
            Filter = filter
        };

        if (d.ShowDialog() == true)
        {
            path = d.FileName;
            return d.FilterIndex - 1; //This is tarting from 1. So must minus 1
        }
        else
        {
            path = "";
            return -1;
        }
    }

    private void ShowWireframeFunct(bool show)
    {
        foreach (var node in GroupModel.GroupNode.Items.PreorderDFT(node => node.IsRenderable))
        {
            if (node is MeshNode m)
                m.RenderWireframe = show || XRayMode;
        }
    }

    private void RenderFlatFunct(bool show)
    {
        foreach (var node in GroupModel.GroupNode.Items.PreorderDFT(node => node.IsRenderable))
        {
            if (node is MeshNode m)
            {
                if (m.Material is PhongMaterialCore phong)
                {
                    phong.EnableFlatShading = show;
                }
                else if (m.Material is PBRMaterialCore pbr)
                {
                    pbr.EnableFlatShading = show;
                }
            }
        }
    }

    public void HandleTriangleSelection(HitTestResult hit, bool shiftDown = false)
    {
        if (hit.ModelHit is not MeshNode meshNode || hit.Tag is not int loc || hit.TriangleIndices is null)
            return;

        if (meshNode.Geometry is not MeshGeometry3D srcMesh || srcMesh.Positions is null)
            return;

        // Shift+click deselects in any mode; try both key conventions (octree Tag=idx*3, non-octree Tag=idx/3)
        if (shiftDown)
        {
            var key1 = (meshNode, loc);
            var key2 = (meshNode, loc * 3);
            if (selectedTriangles.Remove(key1) || selectedTriangles.Remove(key2))
                RebuildSelectionMesh();
            return;
        }

        if (SelectionMode != SelectionMode.Single)
            return;

        var key = (meshNode, loc);

        if (selectedTriangles.ContainsKey(key))
        {
            selectedTriangles.Remove(key);
        }
        else
        {
            var i0 = hit.TriangleIndices.Item1;
            var i1 = hit.TriangleIndices.Item2;
            var i2 = hit.TriangleIndices.Item3;
            var transform = meshNode.TotalModelMatrix;

            var p0 = Vector3.Transform(srcMesh.Positions[i0], transform);
            var p1 = Vector3.Transform(srcMesh.Positions[i1], transform);
            var p2 = Vector3.Transform(srcMesh.Positions[i2], transform);

            // Backface check: skip if triangle faces away from camera
            var normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
            if (Camera is null) return;
            var camPos = Camera.Position.ToVector3();
            var viewDir = Vector3.Normalize((p0 + p1 + p2) / 3f - camPos);
            if (Vector3.Dot(normal, viewDir) > 0)
                return;

            // Offset slightly along the triangle normal to avoid z-fighting
            var offset = normal * 0.001f;
            selectedTriangles[key] = (p0 + offset, p1 + offset, p2 + offset);
        }

        RebuildSelectionMesh();
    }

    [RelayCommand]
    private void InvertVisibleSelection()
    {
        var visibleNodes = new HashSet<MeshNode>();
        foreach (var mat in MaterialItems)
        {
            if (mat.IsVisible)
            {
                foreach (var node in mat.MeshNodes)
                    visibleNodes.Add(node);
            }
        }

        foreach (var meshNode in visibleNodes)
        {
            var geom = meshNode.Geometry as MeshGeometry3D;
            if (geom?.Positions is null || geom.Indices is null)
                continue;

            var transform = meshNode.TotalModelMatrix;
            var indices = geom.Indices;

            for (int i = 0; i < indices.Count; i += 3)
            {
                var key = (meshNode, i);
                if (selectedTriangles.ContainsKey(key))
                {
                    selectedTriangles.Remove(key);
                }
                else
                {
                    var p0 = Vector3.Transform(geom.Positions[indices[i]], transform);
                    var p1 = Vector3.Transform(geom.Positions[indices[i + 1]], transform);
                    var p2 = Vector3.Transform(geom.Positions[indices[i + 2]], transform);
                    var normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
                    var offset = normal * 0.001f;
                    selectedTriangles[key] = (p0 + offset, p1 + offset, p2 + offset);
                }
            }
        }

        RebuildSelectionMesh();
    }

    [RelayCommand]
    private void ClearVisibleSelection()
    {
        var visibleNodes = new HashSet<MeshNode>();
        foreach (var mat in MaterialItems)
        {
            if (mat.IsVisible)
            {
                foreach (var node in mat.MeshNodes)
                    visibleNodes.Add(node);
            }
        }

        var toRemove = selectedTriangles.Keys.Where(k => visibleNodes.Contains(k.Item1)).ToList();
        foreach (var key in toRemove)
            selectedTriangles.Remove(key);

        RebuildSelectionMesh();
    }

    [RelayCommand]
    private void SelectCurrentView()
    {
        var viewport = mainWindow?.view;
        if (viewport is null || Camera is null)
            return;

        var camPos = Camera.Position.ToVector3();
        var vpWidth = (float)viewport.ActualWidth;
        var vpHeight = (float)viewport.ActualHeight;

        // Collect all front-facing, in-viewport triangle candidates
        var candidates = new List<(MeshNode node, int loc, Vector3 p0, Vector3 p1, Vector3 p2, Vector2 screenCenter)>();

        foreach (var node in GroupModel.GroupNode.Items.PreorderDFT(n => n.IsRenderable))
        {
            if (node is not MeshNode meshNode || !meshNode.Visible)
                continue;

            var geom = meshNode.Geometry as MeshGeometry3D;
            if (geom?.Positions is null || geom.Indices is null)
                continue;

            var transform = meshNode.TotalModelMatrix;
            var indices = geom.Indices;

            for (int i = 0; i < indices.Count; i += 3)
            {
                var p0 = Vector3.Transform(geom.Positions[indices[i]], transform);
                var p1 = Vector3.Transform(geom.Positions[indices[i + 1]], transform);
                var p2 = Vector3.Transform(geom.Positions[indices[i + 2]], transform);

                // Backface cull
                var normal = Vector3.Cross(p1 - p0, p2 - p0);
                var centroid = (p0 + p1 + p2) / 3f;
                var viewDir = centroid - camPos;
                if (Vector3.Dot(normal, viewDir) > 0)
                    continue;

                // Project centroid to screen
                var screenPt = viewport.Project(centroid);
                if (screenPt.X < 0 || screenPt.X >= vpWidth || screenPt.Y < 0 || screenPt.Y >= vpHeight)
                    continue;

                candidates.Add((meshNode, i, p0, p1, p2, screenPt));
            }
        }

        // For each candidate, hit test at its screen position to check occlusion
        foreach (var (meshNode, loc, p0, p1, p2, screenCenter) in candidates)
        {
            var key = (meshNode, loc);
            if (selectedTriangles.ContainsKey(key))
                continue;

            var hits = viewport.FindHits(new System.Windows.Point(screenCenter.X, screenCenter.Y));
            if (hits.Count == 0)
                continue;

            // Compare distance: if nearest hit is at roughly the same distance as our triangle centroid, it's not occluded
            var nearest = hits[0];
            var centroid = (p0 + p1 + p2) / 3f;
            var centroidDist = (centroid - camPos).Length();
            var hitDist = (nearest.PointHit - camPos).Length();
            var tolerance = centroidDist * (OcclusionThreshold / 100f);
            if (hitDist >= centroidDist - tolerance)
            {
                var normal = Vector3.Normalize(Vector3.Cross(p1 - p0, p2 - p0));
                var offset = normal * 0.001f;
                selectedTriangles[key] = (p0 + offset, p1 + offset, p2 + offset);
            }
        }

        RebuildSelectionMesh();
    }

    [RelayCommand]
    private async Task IterateViews()
    {
        if (Camera is null || octahedronWorldVertices.Count == 0)
            return;

        // Save camera state
        var savedPos = Camera.Position;
        var savedLook = Camera.LookDirection;
        var savedUp = Camera.UpDirection;
        var savedWidth = (Camera is OrthographicCamera oc) ? oc.Width : 0;

        var lookTarget = octahedronCenter;
        var maxWidth = Math.Max(Math.Max(ModelBound.Width, ModelBound.Height), ModelBound.Depth);

        foreach (var vertexPos in octahedronWorldVertices)
        {
            // Move camera to octahedron vertex, look at octahedron bottom center
            Camera.Position = vertexPos.ToPoint3D();
            Camera.LookDirection = (lookTarget - vertexPos).ToVector3D();

            // Compute a stable up vector that isn't parallel to the look direction
            var look = Vector3.Normalize(lookTarget - vertexPos);
            var up = Math.Abs(Vector3.Dot(look, Vector3.UnitY)) > 0.99f
                ? Vector3.UnitZ
                : Vector3.UnitY;
            Camera.UpDirection = up.ToVector3D();

            if (Camera is OrthographicCamera orthCam)
                orthCam.Width = maxWidth;

            // Wait for the viewport to render the new view
            await Task.Delay(RenderWaitMs);

            SelectCurrentView();
        }

        // Restore camera state
        Camera.Position = savedPos;
        Camera.LookDirection = savedLook;
        Camera.UpDirection = savedUp;
        if (Camera is OrthographicCamera orthCam2)
            orthCam2.Width = savedWidth;

        MessageBox.Show("Selection done!");
    }

    private void XRayModeFunct(bool enable)
    {
        foreach (var node in GroupModel.GroupNode.Items.PreorderDFT(n => n.IsRenderable))
        {
            if (node is MeshNode m)
            {
                m.RenderWireframe = enable || ShowWireframe;
                m.IsTransparent = enable;
                if (m.Material is PhongMaterialCore phong)
                {
                    var c = phong.DiffuseColor;
                    phong.DiffuseColor = new Color4(c.Red, c.Green, c.Blue, enable ? 0.3f : 1.0f);
                }
                else if (m.Material is PBRMaterialCore pbr)
                {
                    var c = pbr.AlbedoColor;
                    pbr.AlbedoColor = new Color4(c.Red, c.Green, c.Blue, enable ? 0.3f : 1.0f);
                }
            }
        }
    }

    private void RebuildSelectionMesh()
    {
        var positions = new Vector3Collection();
        var indices = new IntCollection();
        int idx = 0;
        foreach (var entry in selectedTriangles)
        {
            if (!entry.Key.Item1.Visible)
                continue;

            var (p0, p1, p2) = entry.Value;
            positions.Add(p0);
            positions.Add(p1);
            positions.Add(p2);
            indices.Add(idx++);
            indices.Add(idx++);
            indices.Add(idx++);
        }
        SelectionMesh = new MeshGeometry3D { Positions = positions, Indices = indices };
        UpdateTriangleCounts();
    }

    private void CollectMaterials()
    {
        MaterialItems.Clear();
        var materialMap = new Dictionary<Guid, MaterialItem>();

        foreach (var node in GroupModel.GroupNode.Items.PreorderDFT(n => true))
        {
            if (node is MeshNode m && m.Material is MaterialCore mat)
            {
                if (!materialMap.TryGetValue(mat.Guid, out var item))
                {
                    item = new MaterialItem(mat.Name) { VisibilityChanged = RebuildSelectionMesh };
                    materialMap[mat.Guid] = item;
                    MaterialItems.Add(item);
                }
                item.MeshNodes.Add(m);
            }
        }
        UpdateTriangleCounts();
    }

    private void UpdateTriangleCounts()
    {
        SelectedTriangleCount = selectedTriangles.Count;

        int total = 0;
        foreach (var node in GroupModel.GroupNode.Items.PreorderDFT(n => n.IsRenderable))
        {
            if (node is MeshNode m && m.Geometry is MeshGeometry3D geom && geom.Indices is not null)
                total += geom.Indices.Count / 3;
        }
        TotalTriangleCount = total;
    }

    private void ClearTriangleSelection()
    {
        selectedTriangles.Clear();
        SelectionMesh = new MeshGeometry3D { Positions = new Vector3Collection(), Indices = new IntCollection() };
        UpdateTriangleCounts();
    }

    private void RebuildOctahedron()
    {
        var halfExtents = (ModelBound.Maximum - ModelBound.Minimum) / 2f;
        // Octahedron vertices at ±axis scaled so all bounding box corners are inside
        var r = halfExtents.X + halfExtents.Y + halfExtents.Z;
        // Shift center so octahedron's equator (Y=0 vertices) aligns with mesh's min Y
        var center = new Vector3(ModelBound.Center.X, ModelBound.Minimum.Y, ModelBound.Center.Z);
        octahedronCenter = center;
        if (r <= 0)
        {
            OctahedronLines = null;
            return;
        }

        // Build subdivided upper-hemisphere octahedron (geodesic) projected onto sphere of radius r
        var vertices = new List<Vector3>
        {
            new(0, 1, 0),
            new(1, 0, 0), new(-1, 0, 0),
            new(0, 0, 1), new(0, 0, -1)
        };
        var triangles = new List<(int, int, int)>
        {
            (0, 3, 1), (0, 1, 4), (0, 4, 2), (0, 2, 3)
        };

        int subdivisions = Math.Max(0, OctahedronResolution - 1);
        for (int s = 0; s < subdivisions; s++)
        {
            var edgeMidpoints = new Dictionary<(int, int), int>();
            var newTriangles = new List<(int, int, int)>();

            int GetMidpoint(int a, int b)
            {
                var key = a < b ? (a, b) : (b, a);
                if (edgeMidpoints.TryGetValue(key, out var mid))
                    return mid;
                var midVert = Vector3.Normalize((vertices[a] + vertices[b]) / 2f);
                mid = vertices.Count;
                vertices.Add(midVert);
                edgeMidpoints[key] = mid;
                return mid;
            }

            foreach (var (i0, i1, i2) in triangles)
            {
                var m01 = GetMidpoint(i0, i1);
                var m12 = GetMidpoint(i1, i2);
                var m02 = GetMidpoint(i0, i2);
                newTriangles.Add((i0, m01, m02));
                newTriangles.Add((m01, i1, m12));
                newTriangles.Add((m02, m12, i2));
                newTriangles.Add((m01, m12, m02));
            }
            triangles = newTriangles;
        }

        // Build line geometry from triangle edges
        var edges = new HashSet<(int, int)>();
        foreach (var (i0, i1, i2) in triangles)
        {
            edges.Add(i0 < i1 ? (i0, i1) : (i1, i0));
            edges.Add(i1 < i2 ? (i1, i2) : (i2, i1));
            edges.Add(i0 < i2 ? (i0, i2) : (i2, i0));
        }

        var linePositions = new Vector3Collection();
        var lineIndices = new IntCollection();
        foreach (var (a, b) in edges)
        {
            var idx = linePositions.Count;
            linePositions.Add(center + vertices[a] * r);
            linePositions.Add(center + vertices[b] * r);
            lineIndices.Add(idx);
            lineIndices.Add(idx + 1);
        }
        OctahedronLines = new LineGeometry3D { Positions = linePositions, Indices = lineIndices };

        octahedronWorldVertices = new List<Vector3>(vertices.Count);
        foreach (var v in vertices)
            octahedronWorldVertices.Add(center + v * r);
    }
}
