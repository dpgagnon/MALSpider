using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MALSpider.Models;
using MALSpider.Services;
using MALSpider.Graph;

namespace MALSpider;

public partial class MainWindow : Window
{
    private readonly JikanService _jikanService = new();
    private readonly GraphRenderer _renderer;
    private readonly GraphCarousel _carousel;

    private Point _lastMousePosition;
    private Point _mmbAnchorPoint;
    private bool _isLmbPanning;
    private bool _isMmbPanning;
    private bool _isCrawling;
    private bool _showLN = true;
    private bool _showManga = true;
    private bool _showAnime = true;
    private CancellationTokenSource? _crawlCts;
    private const string SettingsFile = MALSpiderConstants.SettingsFile;

    private double HorizontalGap => MALSpiderConstants.HorizontalGap;
    private double VerticalGap => MALSpiderConstants.VerticalGap;

    public MainWindow()
    {
        InitializeComponent();
        _renderer = new GraphRenderer(GraphCanvas, HeaderCanvas, TimeCanvas);
        _renderer.OnNodeRetryRequested = async node => await RetryNode(node);
        _renderer.OnLaneToggleRequested = (lane, isVisible) =>
        {
            if (lane == "LIGHT NOVEL") _showLN = isVisible;
            else if (lane == "MANGA") _showManga = isVisible;
            else if (lane == "ANIME") _showAnime = isVisible;
            RenderGraph(null);
            SaveSettings();
        };
        _carousel = new GraphCarousel(LoadingCanvas, _renderer);
        _jikanService.OnNodeImageLoaded = node => Dispatcher.Invoke(() => RenderGraph(node));
        LoadSettings();
        CompositionTarget.Rendering += CompositionTarget_Rendering;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int useDarkMode = 1;

        // Apply dark theme to title bar (Windows 10 1809+ and Windows 11)
        if (DwmSetWindowAttribute(hwnd, MALSpiderConstants.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, MALSpiderConstants.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDarkMode, sizeof(int));
        }
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        if (_isMmbPanning)
        {
            Point currentPosition = Mouse.GetPosition(MainScrollViewer);
            double deltaX = currentPosition.X - _mmbAnchorPoint.X;
            double deltaY = currentPosition.Y - _mmbAnchorPoint.Y;

            double sensitivity = 0.05;
            double deadzone = 10;

            if (Math.Abs(deltaX) > deadzone)
            {
                double speedX = (deltaX - (deltaX > 0 ? deadzone : -deadzone)) * sensitivity;
                MainScrollViewer.ScrollToHorizontalOffset(MainScrollViewer.HorizontalOffset + speedX);
            }

            if (Math.Abs(deltaY) > deadzone)
            {
                double speedY = (deltaY - (deltaY > 0 ? deadzone : -deadzone)) * sensitivity;
                MainScrollViewer.ScrollToVerticalOffset(MainScrollViewer.VerticalOffset + speedY);
            }
        }

        if (_isCrawling)
        {
            _carousel.Update(MainScrollViewer.ActualWidth, MainScrollViewer.ActualHeight);
        }
    }


    private void LoadSettings()
    {
        if (File.Exists(SettingsFile))
        {
            try
            {
                var json = File.ReadAllText(SettingsFile);
                var settings = JsonSerializer.Deserialize<WindowSettings>(json);
                if (settings != null)
                {
                    if (settings.Width > 0 && settings.Height > 0)
                    {
                        Width = settings.Width;
                        Height = settings.Height;
                        Left = settings.Left;
                        Top = settings.Top;
                    }
                    WindowState = settings.WindowState;
                    if (settings.SearchHistory != null)
                    {
                        SearchBox.ItemsSource = settings.SearchHistory;
                        if (settings.SearchHistory.Count > 0)
                        {
                            SearchBox.Text = settings.SearchHistory[0];
                        }
                    }
                    _showLN = settings.ShowLN;
                    _showManga = settings.ShowManga;
                    _showAnime = settings.ShowAnime;
                    if (settings.Zoom >= MALSpiderConstants.MinZoom && settings.Zoom <= MALSpiderConstants.MaxZoom)
                    {
                        ZoomSlider.Value = settings.Zoom;
                    }
                }
            }
            catch { }
        }
    }

    private void SaveSettings()
    {
        try
        {
            var history = SearchBox.ItemsSource as List<string> ?? new List<string>();
            var settings = new WindowSettings
            {
                Width = WindowState == WindowState.Normal ? Width : RestoreBounds.Width,
                Height = WindowState == WindowState.Normal ? Height : RestoreBounds.Height,
                Left = WindowState == WindowState.Normal ? Left : RestoreBounds.Left,
                Top = WindowState == WindowState.Normal ? Top : RestoreBounds.Top,
                WindowState = WindowState,
                SearchHistory = history,
                ShowLN = _showLN,
                ShowManga = _showManga,
                ShowAnime = _showAnime,
                Zoom = ZoomSlider.Value
            };
            var json = JsonSerializer.Serialize(settings);
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveSettings();
        base.OnClosing(e);
    }

    public class WindowSettings
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public WindowState WindowState { get; set; }
        public List<string> SearchHistory { get; set; } = new();
        public bool ShowLN { get; set; } = true;
        public bool ShowManga { get; set; } = true;
        public bool ShowAnime { get; set; } = true;
        public double Zoom { get; set; } = 1.0;
    }

    private async void Spider_Click(object sender, RoutedEventArgs e)
    {
        await StartSpider();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _jikanService.ClearCache();
        await StartSpider();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _crawlCts?.Cancel();
        StatusLabel.Text = "Stopping...";
        StopButton.IsEnabled = false;
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await StartSpider();
        }
    }


    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isMmbPanning)
        {
            _isMmbPanning = false;
            MainScrollViewer.ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
        }
        base.OnKeyDown(e);
    }

    private async Task StartSpider()
    {
        string input = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(input)) return;

        var history = SearchBox.ItemsSource as List<string> ?? new List<string>();
        history.Remove(input);
        history.Insert(0, input);
        if (history.Count > 50) history.RemoveAt(50);
        SearchBox.ItemsSource = null;
        SearchBox.ItemsSource = history;
        SearchBox.Text = input;

        SearchBox.IsEnabled = false;
        SpiderButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        StopButton.IsEnabled = true;
        StatusLabel.Text = "Crawling...";
        WorkProgressBar.Visibility = Visibility.Visible;
        WorkProgressBar.Value = 0;
        _isCrawling = true;
        LoadingCanvas.Visibility = Visibility.Visible;
        _renderer.Clear();
        _carousel.Clear();
        _currentRoot = null;
        _subGraphRoot = null;
        _isSubGraphMode = false;
        BackButton.Visibility = Visibility.Collapsed;
        lock (_progressiveNodes)
        {
            _progressiveNodes.Clear();
        }

        _crawlCts = new CancellationTokenSource();

        try
        {
            var rootNode = await _jikanService.GetFullHierarchy(input, s => Dispatcher.Invoke(() =>
            {
                StatusLabel.Text = s;
                if (s.Contains("/"))
                {
                    var parts = s.Split(':')[0].Split('/');
                    if (parts.Length == 2 && double.TryParse(parts[0], out var visitedCount) && double.TryParse(parts[1], out var totalCount) && totalCount > 0)
                    {
                        WorkProgressBar.Value = (visitedCount / totalCount) * 100;
                    }
                }
            }), node => Dispatcher.Invoke(() => {
                RenderGraph(node);
            }), _crawlCts.Token);

            _isCrawling = false;
            LoadingCanvas.Visibility = Visibility.Collapsed;

            if (rootNode != null)
            {
                _currentRoot = rootNode;
                StatusLabel.Text = "Rendering...";
                RenderGraph(rootNode);
                StatusLabel.Text = "Done.";
            }
            else
            {
                StatusLabel.Text = "Not found.";
            }
        }
        catch (OperationCanceledException)
        {
            _isCrawling = false;
            StatusLabel.Text = "Stopped.";
            // Render what we have so far
            RenderGraph(null);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}");
            StatusLabel.Text = "Error.";
        }
        finally
        {
            _isCrawling = false;
            SearchBox.IsEnabled = true;
            SpiderButton.Visibility = Visibility.Visible;
            StopButton.Visibility = Visibility.Collapsed;
            WorkProgressBar.Visibility = Visibility.Collapsed;
            LoadingCanvas.Visibility = Visibility.Collapsed;
            _crawlCts?.Dispose();
            _crawlCts = null;
        }
    }

    private EntryNode? _currentRoot;
    private EntryNode? _subGraphRoot;
    private bool _isSubGraphMode;
    private readonly HashSet<EntryNode> _progressiveNodes = new();
    private DateTime _lastRenderTime = DateTime.MinValue;

    private async Task RetryNode(EntryNode node)
    {
        if (node.IsRetrying) return;
        RenderGraph(null); // Show loading state

        await _jikanService.RefreshNode(node, s => Dispatcher.Invoke(() =>
        {
            StatusLabel.Text = s;
        }), fetchedNode => Dispatcher.Invoke(() =>
        {
            RenderGraph(fetchedNode);
        }));

        RenderGraph(null);
        StatusLabel.Text = "Graph rebuilt.";
    }

    private void RenderGraph(EntryNode? fetchedNode)
    {
        if (fetchedNode != null)
        {
            _carousel.AddNode(fetchedNode);

            lock (_progressiveNodes)
            {
                _progressiveNodes.Add(fetchedNode);
                if (fetchedNode.IsInputRoot) _currentRoot = fetchedNode;
            }
        }

        if (_currentRoot == null) return;

        var now = DateTime.Now;
        bool isBusy = StatusLabel.Text == "Crawling..." || StatusLabel.Text.Contains("/") || StatusLabel.Text == "Rendering...";
        if (_isCrawling && isBusy && (now - _lastRenderTime).TotalMilliseconds < 500) return;
        _lastRenderTime = now;

        var allNodesSet = new HashSet<EntryNode>();
        lock (_progressiveNodes)
        {
            foreach (var node in _progressiveNodes)
            {
                if (node != null) allNodesSet.Add(node);
            }
        }
        var traversedNodes = GraphLayoutEngine.GetAllNodes(_currentRoot);
        foreach (var node in traversedNodes) allNodesSet.Add(node);

        List<EntryNode> nodesToLayout;
        if (_isSubGraphMode && _subGraphRoot != null)
        {
            nodesToLayout = GraphLayoutEngine.GetConnectedNodes(_subGraphRoot);
            BackButton.Visibility = Visibility.Visible;
            SpiderButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            nodesToLayout = allNodesSet.ToList();
            BackButton.Visibility = Visibility.Collapsed;
            if (!_isCrawling) SpiderButton.Visibility = Visibility.Visible;
        }

        var layout = GraphLayoutEngine.ComputeLayout(nodesToLayout, _renderer.NodeWidth, _renderer.NodeHeight, MALSpiderConstants.VerticalSpacing, _showLN, _showManga, _showAnime);
        _renderer.DrawGraph(layout);
        _renderer.UpdateHeaderPositions(MainScrollViewer.HorizontalOffset, MainScrollViewer.ViewportWidth, GraphScale.ScaleX);

        if (layout.RootPos.X > 0 || layout.RootPos.Y > 0) ScrollToNode(layout.RootPos);
    }

    private void MainScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _renderer?.UpdateHeaderPositions(e.HorizontalOffset, e.ViewportWidth, GraphScale.ScaleX);
        if (TimeTransform != null) TimeTransform.Y = -e.VerticalOffset;
    }

    private void ScrollToNode(Point pos)
    {
        MainScrollViewer.ScrollToHorizontalOffset(pos.X - MainScrollViewer.ViewportWidth / 2 + _renderer.NodeWidth / 2);
        MainScrollViewer.ScrollToVerticalOffset(pos.Y - MainScrollViewer.ViewportHeight / 2 + _renderer.NodeHeight / 2);
    }

    private Point _mouseDownPosition;
    private bool _isClickingNode;
    private DependencyObject? _clickedNode;

    private void ScrollViewer_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Ignore clicks on scroll bars
        if (e.OriginalSource is DependencyObject dep &&
            (VisualTreeHelper.GetParent(dep) is System.Windows.Controls.Primitives.ScrollBar ||
             dep is System.Windows.Controls.Primitives.ScrollBar ||
             FindParent<System.Windows.Controls.Primitives.ScrollBar>(dep) != null))
        {
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            _lastMousePosition = e.GetPosition(MainScrollViewer);
            _mouseDownPosition = _lastMousePosition;
            _isLmbPanning = true;
            MainScrollViewer.CaptureMouse();
            Cursor = Cursors.SizeAll;
            if (_isMmbPanning) _isMmbPanning = false;

            // Check if we started clicking on a node
            _clickedNode = GetNodeAtPosition(e.GetPosition(GraphCanvas));
            _isClickingNode = _clickedNode != null;

            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Middle)
        {
            _isMmbPanning = !_isMmbPanning;
            if (_isMmbPanning) { _mmbAnchorPoint = e.GetPosition(MainScrollViewer); MainScrollViewer.CaptureMouse(); Cursor = Cursors.ScrollAll; }
            else { MainScrollViewer.ReleaseMouseCapture(); Cursor = Cursors.Arrow; }
            e.Handled = true;
        }
        else if (_isMmbPanning && (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Right))
        {
            _isMmbPanning = false;
            MainScrollViewer.ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
            e.Handled = true;
        }
    }

    private bool IsMouseOverNode(Point position)
    {
        HitTestResult result = VisualTreeHelper.HitTest(GraphCanvas, position);
        if (result == null) return false;
        DependencyObject obj = result.VisualHit;
        while (obj != null && obj != GraphCanvas)
        {
            if (obj is Border b && b.Cursor == Cursors.Hand) return true;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return false;
    }

    private DependencyObject? GetNodeAtPosition(Point position)
    {
        HitTestResult result = VisualTreeHelper.HitTest(GraphCanvas, position);
        if (result == null) return null;
        DependencyObject obj = result.VisualHit;
        while (obj != null && obj != GraphCanvas)
        {
            if (obj is Border b && b.Cursor == Cursors.Hand) return b;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return null;
    }

    private void ScrollViewer_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isLmbPanning)
        {
            Point currentPosition = e.GetPosition(MainScrollViewer);
            double deltaX = currentPosition.X - _lastMousePosition.X;
            double deltaY = currentPosition.Y - _lastMousePosition.Y;

            if (Math.Abs(deltaX) > 0 || Math.Abs(deltaY) > 0)
            {
                MainScrollViewer.ScrollToHorizontalOffset(MainScrollViewer.HorizontalOffset - deltaX);
                MainScrollViewer.ScrollToVerticalOffset(MainScrollViewer.VerticalOffset - deltaY);
                // We don't update _lastMousePosition here because currentPosition relative to MainScrollViewer
                // changes when we scroll, but we want the delta relative to the VIEWPORT.
                // Wait! Actually, if we use GetPosition(MainScrollViewer), the viewport DOES NOT move relative to its own coordinate system.
                // So currentPosition relative to MainScrollViewer only changes if the mouse moves.
                _lastMousePosition = currentPosition;
            }
        }
    }

    private void ScrollViewer_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _isLmbPanning)
        {
            _isLmbPanning = false;
            MainScrollViewer.ReleaseMouseCapture();
            Cursor = Cursors.Arrow;

            // Check if this was a click on a node
            if (_isClickingNode && _clickedNode is Border nodeBorder)
            {
                Point currentPos = e.GetPosition(MainScrollViewer);
                double distance = Point.Subtract(currentPos, _mouseDownPosition).Length;

                // Threshold for click vs drag (5 pixels is usually plenty)
                if (distance < 5)
                {
                    if (nodeBorder.Tag is EntryNode node)
                    {
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                        {
                            if (_currentRoot != null) _currentRoot.IsInputRoot = false;
                            if (_subGraphRoot != null) _subGraphRoot.IsInputRoot = false; // Cleanup previous subgraph root if any

                            _subGraphRoot = node;
                            _subGraphRoot.IsInputRoot = true;
                            _isSubGraphMode = true;
                            RenderGraph(null);
                        }
                        else if (!string.IsNullOrEmpty(node.MalUrl))
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo(node.MalUrl) { UseShellExecute = true });
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Failed to open URL: {ex.Message}");
                            }
                        }
                    }
                }
            }

            _isClickingNode = false;
            _clickedNode = null;
            e.Handled = true;
        }
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            double zoomDelta = e.Delta > 0 ? 0.1 : -0.1;
            double newZoom = Math.Clamp(ZoomSlider.Value + zoomDelta, MALSpiderConstants.MinZoom, MALSpiderConstants.MaxZoom);

            if (Math.Abs(newZoom - ZoomSlider.Value) > 0.001)
            {
                Point mousePos = e.GetPosition(GraphCanvas);

                // Get mouse position relative to ScrollViewer before zoom
                Point mouseInViewport = e.GetPosition(MainScrollViewer);

                ZoomSlider.Value = newZoom;

                // Forces layout update so we can scroll to the correct position
                GraphCanvas.UpdateLayout();

                // Adjust scroll to keep mouse position fixed
                double newX = (mousePos.X * newZoom) - mouseInViewport.X;
                double newY = (mousePos.Y * newZoom) - mouseInViewport.Y;

                MainScrollViewer.ScrollToHorizontalOffset(newX);
                MainScrollViewer.ScrollToVerticalOffset(newY);
            }

            e.Handled = true;
        }
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GraphScale != null)
        {
            GraphScale.ScaleX = e.NewValue;
            GraphScale.ScaleY = e.NewValue;

            // Forces layout update so ScrollViewer properties are current
            GraphCanvas.UpdateLayout();
            MainScrollViewer.UpdateLayout();

            _renderer?.UpdateHeaderPositions(MainScrollViewer.HorizontalOffset, MainScrollViewer.ViewportWidth, e.NewValue);
        }
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        ZoomSlider.Value = 1.0;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_subGraphRoot != null) _subGraphRoot.IsInputRoot = false;
        if (_currentRoot != null) _currentRoot.IsInputRoot = true;

        _isSubGraphMode = false;
        _subGraphRoot = null;
        RenderGraph(null);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject parentObject = VisualTreeHelper.GetParent(child);
        if (parentObject == null) return null;
        if (parentObject is T parent) return parent;
        return FindParent<T>(parentObject);
    }
}
