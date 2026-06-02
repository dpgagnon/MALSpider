using System.IO;
using System.Text.Json;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MALSpider.Models;
using MALSpider.Services;

namespace MALSpider;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly JikanService _jikanService = new();
    private const double NodeWidth = 220;
    private const double NodeHeight = 180;
    private const double HorizontalGap = 100;
    private const double VerticalGap = 50;

    private Point _lastMousePosition;
    private Point _mmbAnchorPoint;
    private bool _isLmbPanning;
    private bool _isMmbPanning;
    private const string SettingsFile = "settings.json";

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        CompositionTarget.Rendering += CompositionTarget_Rendering;
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        if (_isMmbPanning)
        {
            Point currentPosition = Mouse.GetPosition(MainScrollViewer);
            double deltaX = currentPosition.X - _mmbAnchorPoint.X;
            double deltaY = currentPosition.Y - _mmbAnchorPoint.Y;

            // Sensitivity factor and deadzone
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
                    Width = settings.Width;
                    Height = settings.Height;
                    WindowState = settings.WindowState;
                    if (settings.SearchHistory != null)
                    {
                        SearchBox.ItemsSource = settings.SearchHistory;
                        if (settings.SearchHistory.Count > 0)
                        {
                            SearchBox.Text = settings.SearchHistory[0];
                        }
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
                Width = Width,
                Height = Height,
                WindowState = WindowState,
                SearchHistory = history
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
        public WindowState WindowState { get; set; }
        public List<string> SearchHistory { get; set; } = new();
    }

    private async void Spider_Click(object sender, RoutedEventArgs e)
    {
        await StartSpider();
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

        // Update history
        var history = SearchBox.ItemsSource as List<string> ?? new List<string>();
        history.Remove(input);
        history.Insert(0, input);
        if (history.Count > 50) history.RemoveAt(50);
        SearchBox.ItemsSource = null;
        SearchBox.ItemsSource = history;
        SearchBox.Text = input;

        SearchBox.IsEnabled = false;
        StatusLabel.Text = "Crawling...";
        WorkProgressBar.Visibility = Visibility.Visible;
        WorkProgressBar.Value = 0;
        GraphCanvas.Children.Clear();

        try
        {
            var root = await _jikanService.GetFullHierarchy(input, s => Dispatcher.Invoke(() =>
            {
                StatusLabel.Text = s;
                // Update progress bar if possible. s looks like "visited/total: status"
                if (s.Contains("/"))
                {
                    var parts = s.Split(':')[0].Split('/');
                    if (parts.Length == 2 && double.TryParse(parts[0], out var visited) && double.TryParse(parts[1], out var total))
                    {
                        WorkProgressBar.Value = (visited / total) * 100;
                    }
                }
            }));

            if (root != null)
            {
                StatusLabel.Text = "Rendering...";
                RenderGraph(root);
                StatusLabel.Text = "Done.";
            }
            else
            {
                StatusLabel.Text = "Not found.";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}");
            StatusLabel.Text = "Error.";
        }
        finally
        {
            SearchBox.IsEnabled = true;
            WorkProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderGraph(EntryNode root)
    {
        var visitedNodes = new Dictionary<EntryNode, Point>();
        var allNodes = GetAllNodes(root);

        // Group nodes by "Main Story" chain and spinoffs
        var clusters = ClusterNodes(allNodes);

        // Separate clusters into Anime, Manga, and Light Novel groups
        var animeClusters = clusters.Where(c => c.Any(n => n.Type == "anime")).ToList();
        var mangaClusters = clusters.Where(c => c.Any(n => n.Type == "manga" && n.SourceType != "Light Novel")).ToList();
        var lnClusters = clusters.Where(c => c.Any(n => n.Type == "manga" && n.SourceType == "Light Novel")).ToList();

        double currentX = 20;
        double maxBottom = 0;
        Point rootPos = new Point(0, 0);

        // Draw Light Novel Column(s)
        if (lnClusters.Any())
        {
            DrawHeader("LIGHT NOVEL", currentX);
            foreach (var cluster in PositionClusters(lnClusters))
            {
                double columnBottom = DrawCluster(cluster, currentX, visitedNodes, ref rootPos);
                maxBottom = Math.Max(maxBottom, columnBottom);
                currentX += NodeWidth + HorizontalGap;
            }
            currentX += HorizontalGap;
        }

        // Draw Manga Column(s)
        if (mangaClusters.Any())
        {
            DrawHeader("MANGA", currentX);
            foreach (var cluster in PositionClusters(mangaClusters))
            {
                double columnBottom = DrawCluster(cluster, currentX, visitedNodes, ref rootPos);
                maxBottom = Math.Max(maxBottom, columnBottom);
                currentX += NodeWidth + HorizontalGap;
            }
            currentX += HorizontalGap; // Extra gap between types
        }

        // Draw Anime Column(s)
        if (animeClusters.Any())
        {
            DrawHeader("ANIME", currentX);
            foreach (var cluster in PositionClusters(animeClusters))
            {
                double columnBottom = DrawCluster(cluster, currentX, visitedNodes, ref rootPos);
                maxBottom = Math.Max(maxBottom, columnBottom);
                currentX += NodeWidth + HorizontalGap;
            }
        }

        // Draw connections
        foreach (var sourceNode in allNodes)
        {
            if (!visitedNodes.TryGetValue(sourceNode, out var sourcePos)) continue;

            foreach (var rel in sourceNode.Relations)
            {
                if (visitedNodes.TryGetValue(rel.Target, out var targetPos))
                {
                    DrawConnection(sourcePos, targetPos, rel.RelationType);
                }
            }
        }

        GraphCanvas.Width = currentX + 40;
        GraphCanvas.Height = maxBottom + 40;

        // Center on root node
        if (rootPos.X > 0 || rootPos.Y > 0)
        {
            ScrollToNode(rootPos);
        }
    }

    private List<List<EntryNode>> PositionClusters(List<List<EntryNode>> clusters)
    {
        var rootCluster = clusters.FirstOrDefault(c => c.Any(n => n.IsInputRoot));
        if (rootCluster != null)
        {
            clusters.Remove(rootCluster);
        }

        var sortedOtherClusters = clusters.OrderByDescending(c => c.Count).ToList();
        var positionedClusters = new List<List<EntryNode>>();

        if (rootCluster != null) positionedClusters.Add(rootCluster);

        for (int i = 0; i < sortedOtherClusters.Count; i++)
        {
            if (i % 2 == 0) positionedClusters.Add(sortedOtherClusters[i]); // Right
            else positionedClusters.Insert(0, sortedOtherClusters[i]); // Left
        }

        // Simpler positioning for now to ensure it works
        return positionedClusters.Count > 0 ? positionedClusters : clusters;
    }

    private double DrawCluster(List<EntryNode> cluster, double x, Dictionary<EntryNode, Point> visitedNodes, ref Point rootPos)
    {
        var orderedCluster = OrderByDateAndRelation(cluster);
        double currentY = 80; // Start below header
        foreach (var node in orderedCluster)
        {
            var pos = new Point(x, currentY);
            visitedNodes[node] = pos;
            if (node.IsInputRoot) rootPos = pos;
            DrawNode(node, pos);
            currentY += NodeHeight + VerticalGap;
        }
        return currentY;
    }

    private void DrawHeader(string text, double x)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(187, 134, 252)),
            Width = NodeWidth,
            TextAlignment = TextAlignment.Center
        };
        Canvas.SetLeft(textBlock, x);
        Canvas.SetTop(textBlock, 20);
        GraphCanvas.Children.Add(textBlock);

        var line = new Line
        {
            X1 = x,
            Y1 = 60,
            X2 = x + NodeWidth,
            Y2 = 60,
            Stroke = new SolidColorBrush(Color.FromRgb(187, 134, 252)),
            StrokeThickness = 2
        };
        GraphCanvas.Children.Add(line);
    }

    private List<EntryNode> GetAllNodes(EntryNode root)
    {
        var visited = new HashSet<EntryNode>();
        var stack = new Stack<EntryNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (visited.Add(node))
            {
                foreach (var rel in node.Relations) stack.Push(rel.Target);
            }
        }
        return visited.ToList();
    }

    private List<List<EntryNode>> ClusterNodes(List<EntryNode> allNodes)
    {
        // Group nodes into clusters based on "close" relationships.
        // Initially, we group Prequels, Sequels, and Full Stories into the same column.
        var clusters = new List<List<EntryNode>>();
        var remaining = new HashSet<EntryNode>(allNodes);

        while (remaining.Count > 0)
        {
            var seed = remaining.First();
            var cluster = new List<EntryNode>();
            var queue = new Queue<EntryNode>();
            queue.Enqueue(seed);
            remaining.Remove(seed);

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                cluster.Add(node);

                var relatedInCluster = allNodes.Where(n => remaining.Contains(n) && IsCloselyRelated(node, n)).ToList();
                foreach (var r in relatedInCluster)
                {
                    remaining.Remove(r);
                    queue.Enqueue(r);
                }
            }
            clusters.Add(cluster);
        }

        // Put cluster with root first.
        var rootCluster = clusters.FirstOrDefault(c => c.Any(n => n.IsInputRoot));
        if (rootCluster != null)
        {
            clusters.Remove(rootCluster);
            clusters.Insert(0, rootCluster);
        }

        return clusters;
    }

    private bool IsCloselyRelated(EntryNode a, EntryNode b)
    {
        // Prequels and sequels are "closely related" and should be in the same column
        // Spinoffs, Side stories, Parent stories, etc. should be in their own column
        return a.Relations.Any(r => r.Target == b && (r.RelationType == "Prequel" || r.RelationType == "Sequel" || r.RelationType == "Full story"))
            || b.Relations.Any(r => r.Target == a && (r.RelationType == "Prequel" || r.RelationType == "Sequel" || r.RelationType == "Full story"));
    }

    private List<EntryNode> OrderByDateAndRelation(List<EntryNode> cluster)
    {
        // Sort by date descending (Newest at top)
        // If dates are equal or missing, use Prequel/Sequel hint
        // A Sequel should be ABOVE a Prequel (since Newest is at Top)

        return cluster.OrderByDescending(n => n.ReleaseDate ?? DateTime.MinValue)
                      .ThenBy(n => n.Title)
                      .ToList();
    }

    private void ScrollToNode(Point pos)
    {
        if (GraphCanvas.Parent is ScrollViewer sv)
        {
            sv.ScrollToHorizontalOffset(pos.X - sv.ViewportWidth / 2 + NodeWidth / 2);
            sv.ScrollToVerticalOffset(pos.Y - sv.ViewportHeight / 2 + NodeHeight / 2);
        }
    }

    private void DrawNode(EntryNode node, Point pos)
    {
        var border = new Border
        {
            Width = NodeWidth,
            Height = NodeHeight,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            BorderBrush = node.IsInputRoot ? new SolidColorBrush(Color.FromRgb(187, 134, 252)) : new SolidColorBrush(Color.FromRgb(64, 64, 64)),
            BorderThickness = node.IsInputRoot ? new Thickness(4) : new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Cursor = Cursors.Hand,
            ToolTip = new ToolTip { Content = $"{node.Title}\n{node.TitleEnglish}\n{node.TitleJapanese}\nReleased: {node.ReleaseDate?.ToShortDateString() ?? "Unknown"}", FontSize = 14 }
        };

        border.MouseDown += (s, e) =>
        {
            if (!string.IsNullOrEmpty(node.MalUrl))
            {
                Process.Start(new ProcessStartInfo(node.MalUrl) { UseShellExecute = true });
            }
        };

        var stack = new StackPanel();

        if (!string.IsNullOrEmpty(node.ImageUrl))
        {
            var img = new Image
            {
                Source = new BitmapImage(new Uri(node.ImageUrl)),
                Height = 90,
                Margin = new Thickness(0, 0, 0, 8),
                Stretch = Stretch.Uniform
            };
            stack.Children.Add(img);
        }

        if (!string.IsNullOrEmpty(node.SourceType))
        {
            stack.Children.Add(new TextBlock
            {
                Text = node.SourceType,
                FontSize = 10,
                TextAlignment = TextAlignment.Center,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 0, 0, 4)
            });
        }

        var titleBlock = new TextBlock
        {
            Text = node.Title,
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 40
        };
        stack.Children.Add(titleBlock);

        if (!string.IsNullOrEmpty(node.TitleEnglish) && node.TitleEnglish != node.Title)
        {
            stack.Children.Add(new TextBlock
            {
                Text = node.TitleEnglish,
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        border.Child = stack;
        Canvas.SetLeft(border, pos.X);
        Canvas.SetTop(border, pos.Y);
        GraphCanvas.Children.Add(border);
    }

    private void DrawConnection(Point start, Point end, string label)
    {
        bool isDownward = Math.Abs(end.Y - start.Y) > 10 && end.Y > start.Y;
        bool isUpward = Math.Abs(end.Y - start.Y) > 10 && end.Y < start.Y;
        bool isHorizontal = Math.Abs(end.Y - start.Y) <= 10;

        Point p1, p2;

        if (isHorizontal)
        {
            // Connect sides
            if (end.X > start.X)
            {
                p1 = new Point(start.X + NodeWidth, start.Y + NodeHeight / 2);
                p2 = new Point(end.X, end.Y + NodeHeight / 2);
            }
            else
            {
                p1 = new Point(start.X, start.Y + NodeHeight / 2);
                p2 = new Point(end.X + NodeWidth, end.Y + NodeHeight / 2);
            }
        }
        else if (isDownward)
        {
            p1 = new Point(start.X + NodeWidth / 2, start.Y + NodeHeight);
            p2 = new Point(end.X + NodeWidth / 2, end.Y);
        }
        else // isUpward
        {
            p1 = new Point(start.X + NodeWidth / 2, start.Y);
            p2 = new Point(end.X + NodeWidth / 2, end.Y + NodeHeight);
        }

        var path = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
            StrokeThickness = 3,
            Data = new PathGeometry(new[]
            {
                new PathFigure(p1, new[]
                {
                    new BezierSegment(
                        new Point(p1.X, (p1.Y + p2.Y) / 2),
                        new Point(p2.X, (p1.Y + p2.Y) / 2),
                        p2, true)
                }, false)
            })
        };
        GraphCanvas.Children.Add(path);

        // Optional: Draw relation label mid-line
        var midX = (p1.X + p2.X) / 2;
        var midY = (p1.Y + p2.Y) / 2;
        var text = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Brushes.LightGray,
            Background = new SolidColorBrush(Color.FromArgb(180, 18, 18, 18)),
            Padding = new Thickness(2)
        };
        Canvas.SetLeft(text, midX - 30);
        Canvas.SetTop(text, midY - 10);
        GraphCanvas.Children.Add(text);
    }

    private void GraphCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Don't do anything here, let ScrollViewer handle it unless we need specific logic
    }

    private void ScrollViewer_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !IsMouseOverNode(e.GetPosition(GraphCanvas)))
        {
            _lastMousePosition = e.GetPosition(this); // Use Window coordinates to avoid jitter
            _isLmbPanning = true;
            MainScrollViewer.CaptureMouse();
            Cursor = Cursors.SizeAll;

            // If MMB panning was active, deactivate it
            if (_isMmbPanning)
            {
                _isMmbPanning = false;
            }
        }
        else if (e.ChangedButton == MouseButton.Middle)
        {
            _isMmbPanning = !_isMmbPanning;
            if (_isMmbPanning)
            {
                _mmbAnchorPoint = e.GetPosition(MainScrollViewer);
                MainScrollViewer.CaptureMouse();
                Cursor = Cursors.ScrollAll;
            }
            else
            {
                MainScrollViewer.ReleaseMouseCapture();
                Cursor = Cursors.Arrow;
            }
        }
        else if (_isMmbPanning && (e.ChangedButton == MouseButton.Left || e.ChangedButton == MouseButton.Right))
        {
            // Stop MMB panning on any other click
            _isMmbPanning = false;
            MainScrollViewer.ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
        }
    }

    private bool IsMouseOverNode(Point position)
    {
        // Simple hit test for nodes
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

    private void ScrollViewer_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isLmbPanning)
        {
            Point currentPosition = e.GetPosition(this);
            double deltaX = currentPosition.X - _lastMousePosition.X;
            double deltaY = currentPosition.Y - _lastMousePosition.Y;

            MainScrollViewer.ScrollToHorizontalOffset(MainScrollViewer.HorizontalOffset - deltaX);
            MainScrollViewer.ScrollToVerticalOffset(MainScrollViewer.VerticalOffset - deltaY);

            _lastMousePosition = currentPosition;
        }
    }

    private void ScrollViewer_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _isLmbPanning)
        {
            _isLmbPanning = false;
            MainScrollViewer.ReleaseMouseCapture();
            Cursor = Cursors.Arrow;
        }
    }
}
