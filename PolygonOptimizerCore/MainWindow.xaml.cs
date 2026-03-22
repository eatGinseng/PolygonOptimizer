using HelixToolkit.SharpDX;
using HelixToolkit.SharpDX.Model.Scene;
using HelixToolkit.Wpf.SharpDX;
using System;
using System.Numerics;
using System.Windows;
using System.Windows.Input;

namespace FileLoadDemo;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private Point dragStart;
    private bool dragActive = false;
    private const double DragThreshold = 5.0;
    private readonly MouseBinding panBinding;

    private readonly string? initialFilePath;

    public MainWindow(string? filePath = null)
    {
        initialFilePath = filePath;
        InitializeComponent();

        var vm = new MainViewModel(this);
        this.DataContext = vm;

        panBinding = new MouseBinding(ViewportCommands.Pan, new MouseGesture(MouseAction.LeftClick));
        UpdatePanBinding(vm.SelectionMode);

        vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectionMode))
                UpdatePanBinding(vm.SelectionMode);
        };

        viewportGrid.PreviewMouseLeftButtonDown += View_PreviewMouseLeftButtonDown;
        viewportGrid.PreviewMouseMove += View_PreviewMouseMove;
        viewportGrid.PreviewMouseLeftButtonUp += View_PreviewMouseLeftButtonUp;

        view.AddHandler(Element3D.MouseDown3DEvent, new RoutedEventHandler((s, e) =>
        {
            if (dragActive) return;

            if (e is not MouseDown3DEventArgs arg || arg.HitTestResult is null)
                return;

            if (arg.OriginalInputEventArgs is MouseButtonEventArgs inputArgs && inputArgs.LeftButton == MouseButtonState.Pressed)
            {
                if (arg.HitTestResult.ModelHit is SceneNode node && node.Tag is AttachedNodeViewModel avm)
                    avm.Selected = !avm.Selected;

                if (DataContext is MainViewModel mainVm)
                {
                    bool shiftDown = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
                    if (mainVm.SelectConnected)
                        mainVm.HandleFloodFillSelection(arg.HitTestResult, shiftDown);
                    else
                        mainVm.HandleTriangleSelection(arg.HitTestResult, shiftDown);
                }
            }
        }));

        Closed += (s, e) => (DataContext as IDisposable)?.Dispose();

        if (initialFilePath is not null)
        {
            ContentRendered += (s, e) =>
            {
                if (DataContext is MainViewModel mainVm)
                    mainVm.OpenFileFromPath(initialFilePath);
            };
        }
    }

    private void UpdatePanBinding(SelectionMode mode)
    {
        if (mode == SelectionMode.Single)
            view.InputBindings.Remove(panBinding);
        else if (!view.InputBindings.Contains(panBinding))
            view.InputBindings.Add(panBinding);
    }

    private void View_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.SelectionMode != SelectionMode.Single)
            return;

        dragStart = e.GetPosition(view);
        dragActive = false;
        vm.SelectionRectGeometry = null;
    }

    private void View_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.SelectionMode != SelectionMode.Single)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = e.GetPosition(view);
        var dx = pos.X - dragStart.X;
        var dy = pos.Y - dragStart.Y;
        if (Math.Sqrt(dx * dx + dy * dy) > DragThreshold)
        {
            if (!dragActive)
            {
                dragActive = true;
                viewportGrid.CaptureMouse();
            }
            UpdateSelectionRect(vm, dragStart, pos);
        }
    }

    private void View_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.SelectionMode != SelectionMode.Single)
            return;

        if (dragActive)
        {
            viewportGrid.ReleaseMouseCapture();
            var dragEnd = e.GetPosition(view);
            bool shiftDown = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            vm.HandleRectSelection(dragStart, dragEnd, shiftDown);
        }

        vm.SelectionRectGeometry = null;
        dragActive = false;
    }

    private void UpdateSelectionRect(MainViewModel vm, Point start, Point end)
    {
        if (view.Camera is not HelixToolkit.Wpf.SharpDX.OrthographicCamera cam)
            return;

        var p0 = ScreenToWorld(start, cam);
        var p1 = ScreenToWorld(new Point(end.X, start.Y), cam);
        var p2 = ScreenToWorld(end, cam);
        var p3 = ScreenToWorld(new Point(start.X, end.Y), cam);

        var positions = new Vector3Collection { p0, p1, p2, p3 };
        var indices = new IntCollection { 0, 1, 1, 2, 2, 3, 3, 0 };
        vm.SelectionRectGeometry = new LineGeometry3D { Positions = positions, Indices = indices };
    }

    private Vector3 ScreenToWorld(Point screenPoint, HelixToolkit.Wpf.SharpDX.OrthographicCamera cam)
    {
        var camPos = cam.Position.ToVector3();
        var lookDir = Vector3.Normalize(cam.LookDirection.ToVector3());
        var upDir = Vector3.Normalize(cam.UpDirection.ToVector3());
        var rightDir = Vector3.Normalize(Vector3.Cross(lookDir, upDir));
        upDir = Vector3.Cross(rightDir, lookDir);

        float width = (float)cam.Width;
        float aspect = (float)(view.ActualWidth / view.ActualHeight);
        float height = width / aspect;

        float nx = (float)(screenPoint.X / view.ActualWidth - 0.5);
        float ny = (float)(screenPoint.Y / view.ActualHeight - 0.5);

        return camPos + rightDir * nx * width - upDir * ny * height + lookDir * 0.5f;
    }

    private Point uvDragStart; // in canvas coordinates (with offset)
    private bool uvDragActive = false;
    private bool uvPanning = false;
    private Point uvPanStart;
    private const double UVPadding = 0.1;

    private static Point CanvasToUV(Point canvasPoint) =>
        new(canvasPoint.X - UVPadding, canvasPoint.Y - UVPadding);

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UVPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel)
            return;

        uvDragStart = e.GetPosition(uvCanvas);
        uvDragActive = false;
        uvSelectionRect.Data = null;
        uvGrid.CaptureMouse();
    }

    private void UVPanel_MouseMove(object sender, MouseEventArgs e)
    {
        // Middle-click pan
        if (uvPanning && e.MiddleButton == MouseButtonState.Pressed)
        {
            var panPos = e.GetPosition(uvGrid);
            uvPan.X += panPos.X - uvPanStart.X;
            uvPan.Y += panPos.Y - uvPanStart.Y;
            uvPanStart = panPos;
            return;
        }

        if (DataContext is not MainViewModel || e.LeftButton != MouseButtonState.Pressed)
            return;

        var pos = e.GetPosition(uvCanvas);
        var dx = pos.X - uvDragStart.X;
        var dy = pos.Y - uvDragStart.Y;

        if (Math.Sqrt(dx * dx + dy * dy) > 0.005)
        {
            uvDragActive = true;
            var rect = new System.Windows.Media.RectangleGeometry(
                new Rect(
                    Math.Min(uvDragStart.X, pos.X), Math.Min(uvDragStart.Y, pos.Y),
                    Math.Abs(dx), Math.Abs(dy)));
            rect.Freeze();
            uvSelectionRect.Data = rect;
        }
    }

    private void UVPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        uvGrid.ReleaseMouseCapture();
        bool shiftDown = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (uvDragActive)
        {
            var uvEnd = e.GetPosition(uvCanvas);
            vm.HandleUVRectSelection(CanvasToUV(uvDragStart), CanvasToUV(uvEnd), shiftDown);
        }
        else
        {
            var uvPoint = CanvasToUV(uvDragStart);
            if (vm.SelectConnected)
                vm.HandleUVFloodFillSelection(uvPoint, shiftDown);
            else
                vm.HandleUVSelection(uvPoint, shiftDown);
        }

        uvSelectionRect.Data = null;
        uvDragActive = false;
    }

    private void UVPanel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            uvPanning = true;
            uvPanStart = e.GetPosition(uvGrid);
            uvGrid.CaptureMouse();
            e.Handled = true;
        }
    }

    private void UVPanel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && uvPanning)
        {
            uvPanning = false;
            if (!uvDragActive)
                uvGrid.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void UVPanel_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
        var mousePos = e.GetPosition(uvGrid);
        var gridCenterX = uvGrid.ActualWidth / 2;
        var gridCenterY = uvGrid.ActualHeight / 2;

        // Zoom toward mouse position
        uvPan.X = mousePos.X - gridCenterX + (uvPan.X - mousePos.X + gridCenterX) * factor;
        uvPan.Y = mousePos.Y - gridCenterY + (uvPan.Y - mousePos.Y + gridCenterY) * factor;
        uvZoom.ScaleX *= factor;
        uvZoom.ScaleY *= factor;

        e.Handled = true;
    }
}
