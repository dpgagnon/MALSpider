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
    private const double NodeHeight = 200;
    private const double HorizontalGap = 100;
    private const double VerticalGap = 50;

    private Point _lastMousePosition;
    private Point _mmbAnchorPoint;
    private bool _isLmbPanning;
    private bool _isMmbPanning;
    private const string SettingsFile = "settings.json";

    // Tracking UI elements for highlighting
    private readonly Dictionary<EntryNode, Border> _nodeToBorder = new();
    private readonly Dictionary<EntryNode, List<Shape>> _nodeToConnections = new();
    private readonly Dictionary<Shape, (EntryNode Source, EntryNode Target, bool IsDownward)> _connectionInfo = new();

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
        _currentRoot = null;
        lock (_progressiveNodes)
        {
            _progressiveNodes.Clear();
        }

        EntryNode? rootNode = null;

        try
        {
            rootNode = await _jikanService.GetFullHierarchy(input, s => Dispatcher.Invoke(() =>
            {
                StatusLabel.Text = s;
                // Update progress bar if possible. s looks like "visited/total: status"
                if (s.Contains("/"))
                {
                    var parts = s.Split(':')[0].Split('/');
                    if (parts.Length == 2 && double.TryParse(parts[0], out var visitedCount) && double.TryParse(parts[1], out var totalCount))
                    {
                        WorkProgressBar.Value = (visitedCount / totalCount) * 100;
                    }
                }
            }), node => Dispatcher.BeginInvoke(() =>
            {
                // Progressive rendering: every time a node is fetched, re-render the graph
                RenderGraph(node); // Pass the newly fetched node to potentially root from it if rootNode is null
            }));

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

    private EntryNode? _currentRoot;
    private readonly HashSet<EntryNode> _progressiveNodes = new();
    private DateTime _lastRenderTime = DateTime.MinValue;

    private void RenderGraph(EntryNode fetchedNode)
    {
        lock (_progressiveNodes)
        {
            _progressiveNodes.Add(fetchedNode);
            if (fetchedNode.IsInputRoot) _currentRoot = fetchedNode;
        }

        if (_currentRoot == null) return;

        // Rate limit rendering during crawl to avoid UI lag
        var now = DateTime.Now;
        if ((StatusLabel.Text == "Crawling..." || StatusLabel.Text.Contains("/")) && (now - _lastRenderTime).TotalMilliseconds < 500)
        {
            return;
        }
        _lastRenderTime = now;

        GraphCanvas.Children.Clear();
        _nodeToBorder.Clear();
        _nodeToConnections.Clear();
        _connectionInfo.Clear();

        var visitedNodes = new Dictionary<EntryNode, Point>();
        var allNodes = new HashSet<EntryNode>();
        lock (_progressiveNodes)
        {
            foreach (var node in _progressiveNodes) allNodes.Add(node);
        }

        // Also include the current root and its reachable nodes (even if not in _progressiveNodes yet)
        var traversedNodes = GetAllNodes(_currentRoot);
        foreach (var node in traversedNodes) allNodes.Add(node);

        var allNodesList = allNodes.ToList();

        // Pre-build sequel/prequel adjacency for redundancy checks
        var sequelAdj = new Dictionary<EntryNode, List<EntryNode>>();
        foreach (var n in allNodesList)
        {
            if (!sequelAdj.ContainsKey(n)) sequelAdj[n] = new List<EntryNode>();
            foreach (var r in n.Relations)
            {
                if (r.RelationType == "Prequel" || r.RelationType == "Sequel" || r.RelationType == "Full story")
                {
                    sequelAdj[n].Add(r.Target);
                    if (!sequelAdj.ContainsKey(r.Target)) sequelAdj[r.Target] = new List<EntryNode>();
                    if (!sequelAdj[r.Target].Contains(n)) sequelAdj[r.Target].Add(n);
                }
            }
        }

        // Group nodes by "Main Story" chain and spinoffs
        var clusters = ClusterNodes(allNodesList);

        // Separate clusters into Anime, Manga, and Light Novel groups
        var animeClusters = clusters.Where(c => c.Any(n => n.Type == "anime")).ToList();
        var mangaClusters = clusters.Where(c => c.Any(n => n.Type == "manga" && n.SourceType != "Light Novel")).ToList();
        var lnClusters = clusters.Where(c => c.Any(n => n.Type == "manga" && n.SourceType == "Light Novel")).ToList();

        // Calculate a global time scale
        var allDates = allNodes.Select(n => n.ReleaseDate).Where(d => d.HasValue).Cast<DateTime>().ToList();
        DateTime minDate = allDates.Any() ? allDates.Min() : DateTime.Now.AddYears(-10);
        DateTime maxDate = allDates.Any() ? allDates.Max() : DateTime.Now;
        if (minDate == maxDate) maxDate = minDate.AddDays(1);

        // We want Oldest at Top (Small Y) and Newest at Bottom (Large Y)
        // Requirement: "invert it so oldest is at the top instead"

        double currentX = 20;
        double maxBottom = 0;
        Point rootPos = new Point(0, 0);

        // Vertical spacing helper: maps date to Y
        double GetYForDate(DateTime? date)
        {
            double startY = 100;
            double maxHeight = 2000; // Flexible

            // Linear scale: (date - minDate) / (maxDate - minDate)
            // Oldest (minDate) -> Small Y (startY)
            // Newest (maxDate) -> Large Y (startY + maxHeight)

            if (!date.HasValue)
            {
                // Requirement: "things with unknown release dates should be sorted at the bottom"
                return startY + maxHeight + NodeHeight;
            }

            double ratio = (date.Value.Ticks - minDate.Ticks) / (double)(maxDate.Ticks - minDate.Ticks);
            return startY + ratio * maxHeight;
        }

        // Draw Columns
        var columnGroups = new[] {
            (Title: "LIGHT NOVEL", Clusters: lnClusters),
            (Title: "MANGA", Clusters: mangaClusters),
            (Title: "ANIME", Clusters: animeClusters)
        };

        foreach (var group in columnGroups)
        {
            if (!group.Clusters.Any()) continue;

            DrawHeader(group.Title, currentX);

            // Keep track of occupied vertical space in this column to prevent overlaps between different clusters
            // occupiedY: (Top, Bottom, X)
            var occupiedY = new List<(double Top, double Bottom, double X)>();

            // To avoid zig-zagging for 1-1 relations, we should try to assign each cluster a "preferred X"
            // and only shift if there's a collision with another cluster's X.

            double columnStartX = currentX;

            foreach (var cluster in group.Clusters)
            {
                var orderedCluster = OrderByDateAndRelation(cluster);

                // For each cluster, we try to find the best lane (X position)
                double clusterPreferredX = columnStartX;
                bool clusterPlaced = false;
                int laneShift = 0;

                while (!clusterPlaced)
                {
                    double testX = columnStartX + laneShift * (NodeWidth + 20);
                    bool collision = false;

                    // Check if any node in this cluster would collide with already placed nodes at this X
                    foreach (var node in orderedCluster)
                    {
                        double targetY = GetYForDate(node.ReleaseDate);
                        foreach (var range in occupiedY)
                        {
                            if (Math.Abs(testX - range.X) < 10 &&
                                targetY < range.Bottom + VerticalGap && targetY + NodeHeight > range.Top - VerticalGap)
                            {
                                collision = true;
                                break;
                            }
                        }
                        if (collision) break;
                    }

                    if (!collision)
                    {
                        clusterPreferredX = testX;
                        clusterPlaced = true;
                    }
                    else
                    {
                        laneShift++;
                    }
                }

                // Place the nodes in the cluster at clusterPreferredX
                foreach (var node in orderedCluster)
                {
                    double targetY = GetYForDate(node.ReleaseDate);
                    double runningY = targetY;

                    // Even within a cluster, if dates are exactly the same, they might need to be pushed vertically
                    // but we keep them in the same X to avoid zig-zagging unless it's a 1-to-N situation?
                    // The user said: "if something is 1-to-N it should generate N stacks from there, however, everything 1-to-1 should be stacked once"

                    // Check if something is ALREADY at this exact (X, Y)
                    bool vCollision = true;
                    while (vCollision)
                    {
                        vCollision = false;
                        foreach (var range in occupiedY)
                        {
                            if (Math.Abs(clusterPreferredX - range.X) < 10 &&
                                runningY < range.Bottom + VerticalGap && runningY + NodeHeight > range.Top - VerticalGap)
                            {
                                vCollision = true;
                                runningY = range.Bottom + VerticalGap;
                                break;
                            }
                        }
                    }

                    var pos = new Point(clusterPreferredX, runningY);
                    visitedNodes[node] = pos;
                    if (node.IsInputRoot) rootPos = pos;
                    DrawNode(node, pos);

                    occupiedY.Add((runningY, runningY + NodeHeight, clusterPreferredX));
                    maxBottom = Math.Max(maxBottom, runningY + NodeHeight);
                }
            }
            // Update currentX based on the widest part of this column group
            double maxLane = occupiedY.Any() ? occupiedY.Max(o => o.X) : currentX;
            currentX = Math.Max(currentX, maxLane) + NodeWidth + HorizontalGap;
        }

        // Draw connections
        // Use a set to track already drawn pairs to avoid duplicate lines
        var drawnPairs = new HashSet<(EntryNode, EntryNode)>();

        // Pre-calculate immediate chains to detect redundant connections
        // If we have A -> B (Prequel) and A -> C (Adaptation) and B -> C (Adaptation),
        // we might want to skip A -> C if B -> C exists?
        // User example: Manga -> S1, Manga -> S2, S1 -> S2. Skip Manga -> S2.

        foreach (var sourceNode in allNodes)
        {
            if (!visitedNodes.TryGetValue(sourceNode, out var sourcePos)) continue;

            // Filter relations to remove redundancies
            var relationsToDraw = new List<EntryRelation>();
            foreach (var rel in sourceNode.Relations)
            {
                var targetNode = rel.Target;

                // Redundancy check:
                // If I am Manga and I'm connecting to S2 (Adaptation),
                // check if I am already connected to some other node X,
                // and there is a path from X to targetNode via Prequel/Sequel chains.
                bool isRedundant = false;
                if (rel.RelationType == "Adaptation" || rel.RelationType == "Parent story" || rel.RelationType == "Other" || rel.RelationType == "Side story")
                {
                    // Pre-fetch all reachable nodes from the target via sequel chains
                    // If any of the source's OTHER relations lead to one of these nodes, then this link is redundant.
                    foreach (var otherRel in sourceNode.Relations)
                    {
                        if (otherRel == rel) continue;

                        // Check if targetNode is reachable from otherRel.Target via Prequel/Sequel chains
                        // OR if otherRel.Target is reachable from targetNode via Prequel/Sequel chains
                        // This covers both Manga -> S1 (with S1 -> S2 existing) and Manga -> S2 (with S1 -> S2 existing)
                        // UPDATE: Also include Side Stories in reachability check when pruning Adaptations
                        if (IsReachable(otherRel.Target, targetNode, sequelAdj, true))
                        {
                            isRedundant = true;
                            break;
                        }
                    }

                    // User feedback: "recurse up the parent nodes in the tree and see if there's already a connection"
                    // If we didn't find a direct sibling-based redundancy, check if ANY of our sequential parents/ancestors
                    // (e.g. if we are Manga, our own Prequels or sequels) already connect to the target.
                    if (!isRedundant)
                    {
                        var visitedAncestors = new HashSet<EntryNode>();
                        var ancestorQueue = new Queue<EntryNode>();
                        ancestorQueue.Enqueue(sourceNode);
                        visitedAncestors.Add(sourceNode);

                        while (ancestorQueue.Count > 0)
                        {
                            var current = ancestorQueue.Dequeue();

                            // Check if 'current' (an ancestor or sequel sibling of source) connects to 'targetNode'
                            if (current != sourceNode)
                            {
                                if (current.Relations.Any(r => r.Target == targetNode && (r.RelationType == "Adaptation" || r.RelationType == "Parent story" || r.RelationType == "Other" || r.RelationType == "Side story")))
                                {
                                    isRedundant = true;
                                    break;
                                }
                            }

                            // Walk further along the sequel chains of the source
                            if (sequelAdj.TryGetValue(current, out var neighbors))
                            {
                                foreach (var neighbor in neighbors)
                                {
                                    if (visitedAncestors.Add(neighbor))
                                    {
                                        ancestorQueue.Enqueue(neighbor);
                                    }
                                }
                            }
                        }
                    }
                }

                if (!isRedundant)
                {
                    relationsToDraw.Add(rel);
                }
            }

            // Group targets into those above and those below (or same level)
            var downwardTargets = new List<(Point Pos, string Label, EntryNode Node)>();
            var upwardTargets = new List<(Point Pos, string Label, EntryNode Node)>();
            var horizontalTargets = new List<(Point Pos, string Label, EntryNode Node)>();

            foreach (var rel in relationsToDraw)
            {
                var targetNode = rel.Target;
                if (!visitedNodes.TryGetValue(targetNode, out var targetPos)) continue;

                // Create a canonical pair key (minId_type, maxId_type) to detect duplicates
                var pairKey = sourceNode.MalId < targetNode.MalId || (sourceNode.MalId == targetNode.MalId && sourceNode.Type.CompareTo(targetNode.Type) < 0)
                    ? (sourceNode, targetNode)
                    : (targetNode, sourceNode);

                if (drawnPairs.Contains(pairKey)) continue;

                bool isDownward = Math.Abs(targetPos.Y - sourcePos.Y) > 10 && targetPos.Y > sourcePos.Y;
                bool isUpward = Math.Abs(targetPos.Y - sourcePos.Y) > 10 && targetPos.Y < sourcePos.Y;

                if (isDownward)
                {
                    downwardTargets.Add((targetPos, rel.RelationType, targetNode));
                    drawnPairs.Add(pairKey);
                }
                else if (isUpward)
                {
                    upwardTargets.Add((targetPos, rel.RelationType, targetNode));
                    drawnPairs.Add(pairKey);
                }
                else
                {
                    horizontalTargets.Add((targetPos, rel.RelationType, targetNode));
                    drawnPairs.Add(pairKey);
                }
            }

            // Draw bundled connections for downward
            if (downwardTargets.Count > 1)
                DrawBundledConnections(sourceNode, sourcePos, downwardTargets, true);
            else if (downwardTargets.Count == 1)
                DrawConnection(sourceNode, downwardTargets[0].Node, sourcePos, downwardTargets[0].Pos, downwardTargets[0].Label);

            // Draw bundled connections for upward
            if (upwardTargets.Count > 1)
                DrawBundledConnections(sourceNode, sourcePos, upwardTargets, false);
            else if (upwardTargets.Count == 1)
                DrawConnection(sourceNode, upwardTargets[0].Node, sourcePos, upwardTargets[0].Pos, upwardTargets[0].Label);

            // Horizontal connections stay direct
            foreach (var horiz in horizontalTargets)
                DrawConnection(sourceNode, horiz.Node, sourcePos, horiz.Pos, horiz.Label);
        }

        GraphCanvas.Width = currentX + 40;
        GraphCanvas.Height = maxBottom + 100;

        // Center on root node
        if (rootPos.X > 0 || rootPos.Y > 0)
        {
            ScrollToNode(rootPos);
        }
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
                lock (node.Relations)
                {
                    foreach (var rel in node.Relations) stack.Push(rel.Target);
                }
            }
        }
        return visited.ToList();
    }

    private List<List<EntryNode>> ClusterNodes(List<EntryNode> allNodes)
    {
        // Group nodes into clusters based on "close" relationships.
        // To satisfy "1-to-N it should generate N stacks, however, everything 1-to-1 should be stacked once",
        // we only group nodes if they have a 1-to-1 sequential relationship.
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
                    // Check if it's a 1-to-1 sequential relationship
                    // A node in a middle of a chain has exactly 1 prequel and 1 sequel relation of type P/S/FS
                    int countForNode = allNodes.Count(n => IsCloselyRelated(node, n));
                    int countForR = allNodes.Count(n => IsCloselyRelated(r, n));

                    if (countForNode <= 2 && countForR <= 2)
                    {
                        remaining.Remove(r);
                        queue.Enqueue(r);
                    }
                    else
                    {
                        // It's a split or merge point. Let 'r' start its own cluster.
                    }
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
        // Full story is also closely related.
        return a.Relations.Any(r => r.Target == b && (r.RelationType == "Prequel" || r.RelationType == "Sequel" || r.RelationType == "Full story"))
            || b.Relations.Any(r => r.Target == a && (r.RelationType == "Prequel" || r.RelationType == "Sequel" || r.RelationType == "Full story"));
    }

    private bool IsReachable(EntryNode start, EntryNode target, Dictionary<EntryNode, List<EntryNode>> sequelAdj, bool includeSideStories)
    {
        if (start == target) return true;

        var visited = new HashSet<EntryNode>();
        var queue = new Queue<EntryNode>();
        queue.Enqueue(start);
        visited.Add(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            // Check neighbors from sequelAdj (Prequel/Sequel/Full Story)
            if (sequelAdj.TryGetValue(current, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    if (neighbor == target) return true;
                    if (visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            // Optionally check Side Story/Parent Story neighbors
            if (includeSideStories)
            {
                lock (current.Relations)
                {
                    foreach (var rel in current.Relations)
                    {
                        if (rel.RelationType == "Side story" || rel.RelationType == "Parent story")
                        {
                            if (rel.Target == target) return true;
                            if (visited.Add(rel.Target))
                            {
                                queue.Enqueue(rel.Target);
                            }
                        }
                    }
                }
            }
        }
        return false;
    }

    private List<EntryNode> OrderByDateAndRelation(List<EntryNode> cluster)
    {
        // Sort by date ascending (Oldest at top)
        return cluster.OrderBy(n => n.ReleaseDate ?? DateTime.MaxValue)
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

    private int GetMalIdFromPoint(Dictionary<EntryNode, Point> visitedNodes, Point p)
    {
        return visitedNodes.FirstOrDefault(kvp => kvp.Value == p).Key?.MalId ?? 0;
    }

    private void DrawNode(EntryNode node, Point pos)
    {
        string tooltipContent = $"{node.Title}\n{node.TitleEnglish}\n{node.TitleJapanese}\nReleased: {node.ReleaseDate?.ToShortDateString() ?? "Unknown"}";
        if (!string.IsNullOrEmpty(node.Synopsis))
        {
            string truncatedSynopsis = node.Synopsis.Length > 300
                ? node.Synopsis.Substring(0, 300).TrimEnd() + "..."
                : node.Synopsis;
            tooltipContent += $"\n\n{truncatedSynopsis}";
        }

        var border = new Border
        {
            Width = NodeWidth,
            Height = NodeHeight,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
            BorderBrush = node.IsInputRoot ? new SolidColorBrush(Color.FromRgb(187, 134, 252)) : new SolidColorBrush(Color.FromRgb(64, 64, 64)),
            BorderThickness = node.IsInputRoot ? new Thickness(4) : new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Tag = node,
            Cursor = Cursors.Hand,
            ToolTip = new ToolTip
            {
                Content = new TextBlock
                {
                    Text = tooltipContent,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 400
                },
                FontSize = 14
            }
        };

        border.MouseDown += (s, e) =>
        {
            if (!string.IsNullOrEmpty(node.MalUrl))
            {
                Process.Start(new ProcessStartInfo(node.MalUrl) { UseShellExecute = true });
            }
        };

        border.MouseEnter += Node_MouseEnter;
        border.MouseLeave += Node_MouseLeave;

        _nodeToBorder[node] = border;

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
            Text = !string.IsNullOrEmpty(node.TitleEnglish) ? node.TitleEnglish : node.Title,
            FontWeight = FontWeights.Bold,
            FontSize = 14,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 40
        };
        stack.Children.Add(titleBlock);

        if (node.ReleaseDate.HasValue)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"({node.ReleaseDate.Value.Year})",
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                Foreground = Brushes.DarkGray,
                Margin = new Thickness(0, 0, 0, 2)
            });
        }

        border.Child = stack;
        Canvas.SetLeft(border, pos.X);
        Canvas.SetTop(border, pos.Y);
        GraphCanvas.Children.Add(border);
    }

    private void Node_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border b && b.Tag is EntryNode node)
        {
            HighlightNode(node, true);
        }
    }

    private void Node_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border b && b.Tag is EntryNode node)
        {
            HighlightNode(node, false);
        }
    }

    private void HighlightNode(EntryNode node, bool highlight)
    {
        if (!_nodeToConnections.TryGetValue(node, out var paths)) return;

        foreach (var shape in paths)
        {
            if (_connectionInfo.TryGetValue(shape, out var info))
            {
                if (highlight)
                {
                    // Connection colors: Upward = Cyan, Downward = Orange/Gold
                    bool isSource = info.Source == node;
                    bool isDownwardRel = isSource ? info.IsDownward : !info.IsDownward;

                    shape.Stroke = isDownwardRel
                        ? new SolidColorBrush(Color.FromRgb(255, 165, 0)) // Orange
                        : new SolidColorBrush(Color.FromRgb(0, 255, 255)); // Cyan
                    shape.StrokeThickness = 5;
                    Panel.SetZIndex(shape, 10);

                    // Highlight the other node
                    EntryNode other = isSource ? info.Target : info.Source;
                    if (_nodeToBorder.TryGetValue(other, out var otherBorder))
                    {
                        otherBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 255, 255)); // White highlight
                        otherBorder.BorderThickness = new Thickness(3);
                    }
                }
                else
                {
                    shape.Stroke = new SolidColorBrush(Color.FromRgb(80, 80, 80));
                    shape.StrokeThickness = 3;
                    Panel.SetZIndex(shape, 0);

                    EntryNode other = info.Source == node ? info.Target : info.Source;
                    if (_nodeToBorder.TryGetValue(other, out var otherBorder))
                    {
                        otherBorder.BorderBrush = other.IsInputRoot ? new SolidColorBrush(Color.FromRgb(187, 134, 252)) : new SolidColorBrush(Color.FromRgb(64, 64, 64));
                        otherBorder.BorderThickness = other.IsInputRoot ? new Thickness(4) : new Thickness(2);
                    }
                }
            }
        }

        // Also highlight the node itself a bit more if hovered
        if (_nodeToBorder.TryGetValue(node, out var nodeBorder))
        {
            nodeBorder.Background = highlight ? new SolidColorBrush(Color.FromRgb(45, 45, 45)) : new SolidColorBrush(Color.FromRgb(30, 30, 30));
        }
    }

    private void DrawConnection(EntryNode source, EntryNode target, Point start, Point end, string label)
    {
        bool isDownward = Math.Abs(end.Y - start.Y) > 10 && end.Y > start.Y;
        bool isUpward = Math.Abs(end.Y - start.Y) > 10 && end.Y < start.Y;
        bool isHorizontal = Math.Abs(end.Y - start.Y) <= 10;

        // Horizontal side-connection if nodes are horizontally close and vertically near
        bool useHorizontalSide = !isHorizontal && Math.Abs(end.X - start.X) <= NodeWidth + HorizontalGap + 10 && Math.Abs(end.Y - start.Y) < NodeHeight;

        Point p1, p2;

        if (isHorizontal || useHorizontalSide)
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

        var pathGeometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = p1, IsClosed = false };
        figure.Segments.Add(new LineSegment(p2, true));
        pathGeometry.Figures.Add(figure);

        // Add arrow head
        AddArrowHead(pathGeometry, p1, p2);

        var path = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
            StrokeThickness = 3,
            Data = pathGeometry
        };
        GraphCanvas.Children.Add(path);

        // Register for highlighting
        RegisterConnection(path, source, target, isDownward);

        if (!string.IsNullOrEmpty(label))
        {
            double labelX, labelY;
            if (isHorizontal || useHorizontalSide)
            {
                labelX = (p1.X + p2.X) / 2;
                labelY = (p1.Y + p2.Y) / 2 - 10;
            }
            else
            {
                labelX = (p1.X + p2.X) / 2;
                labelY = (p1.Y + p2.Y) / 2;
            }

            var text = new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = Brushes.LightGray,
                Background = new SolidColorBrush(Color.FromArgb(180, 18, 18, 18)),
                Padding = new Thickness(2)
            };
            Canvas.SetLeft(text, labelX - 30);
            Canvas.SetTop(text, labelY - 10);
            GraphCanvas.Children.Add(text);
        }
    }

    private void AddArrowHead(PathGeometry geometry, Point start, Point end)
    {
        Vector v = end - start;
        if (v.Length < 10) return;
        v.Normalize();

        Point p1 = end - v * 12 + new Vector(-v.Y, v.X) * 6;
        Point p2 = end - v * 12 + new Vector(v.Y, -v.X) * 6;

        var figure = new PathFigure { StartPoint = end, IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(p1, true));
        figure.Segments.Add(new LineSegment(p2, true));
        geometry.Figures.Add(figure);
    }

    private void RegisterConnection(Shape shape, EntryNode source, EntryNode target, bool isDownward)
    {
        _connectionInfo[shape] = (source, target, isDownward);
        if (!_nodeToConnections.ContainsKey(source)) _nodeToConnections[source] = new List<Shape>();
        if (!_nodeToConnections.ContainsKey(target)) _nodeToConnections[target] = new List<Shape>();
        _nodeToConnections[source].Add(shape);
        _nodeToConnections[target].Add(shape);
    }

    private void DrawBundledConnections(EntryNode source, Point start, List<(Point Pos, string Label, EntryNode Node)> targets, bool isDownward)
    {
        Point pStart = isDownward
            ? new Point(start.X + NodeWidth / 2, start.Y + NodeHeight)
            : new Point(start.X + NodeWidth / 2, start.Y);

        // Calculate a junction point halfway to the nearest target vertically
        double nearestYOffset = targets.Min(t => Math.Abs(t.Pos.Y - start.Y));
        double junctionOffset = Math.Min(VerticalGap / 2, nearestYOffset / 2);
        double junctionY = isDownward ? pStart.Y + junctionOffset : pStart.Y - junctionOffset;
        Point junctionPoint = new Point(pStart.X, junctionY);

        // Main line to junction
        var mainPathGeometry = new PathGeometry();
        var mainFigure = new PathFigure { StartPoint = pStart, IsClosed = false };
        mainFigure.Segments.Add(new LineSegment(junctionPoint, true));
        mainPathGeometry.Figures.Add(mainFigure);

        var mainPath = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
            StrokeThickness = 3,
            Data = mainPathGeometry
        };
        GraphCanvas.Children.Add(mainPath);

        // Register main path for source
        if (!_nodeToConnections.ContainsKey(source)) _nodeToConnections[source] = new List<Shape>();
        _nodeToConnections[source].Add(mainPath);

        foreach (var target in targets)
        {
            Point pEnd = isDownward
                ? new Point(target.Pos.X + NodeWidth / 2, target.Pos.Y)
                : new Point(target.Pos.X + NodeWidth / 2, target.Pos.Y + NodeHeight);

            // Angled branch: Junction -> Direct to target
            var pathGeometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = junctionPoint, IsClosed = false };
            figure.Segments.Add(new LineSegment(pEnd, true));
            pathGeometry.Figures.Add(figure);

            // Add arrow head to each branch
            AddArrowHead(pathGeometry, junctionPoint, pEnd);

            var branchPath = new System.Windows.Shapes.Path
            {
                Stroke = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
                StrokeThickness = 3,
                Data = pathGeometry
            };
            GraphCanvas.Children.Add(branchPath);

            // Register branch for highlighting
            RegisterConnection(branchPath, source, target.Node, isDownward);

            // Label on the angled branch
            var midX = (junctionPoint.X + pEnd.X) / 2;
            var midY = (junctionPoint.Y + pEnd.Y) / 2;
            var text = new TextBlock
            {
                Text = target.Label,
                FontSize = 11,
                Foreground = Brushes.LightGray,
                Background = new SolidColorBrush(Color.FromArgb(180, 18, 18, 18)),
                Padding = new Thickness(2)
            };
            Canvas.SetLeft(text, midX - 30);
            Canvas.SetTop(text, midY - 10);
            GraphCanvas.Children.Add(text);
        }
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
