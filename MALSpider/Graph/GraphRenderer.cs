using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MALSpider.Models;

namespace MALSpider.Graph
{
    public class GraphRenderer
    {
        public Action<EntryNode>? OnNodeRetryRequested { get; set; }

        private readonly Canvas _graphCanvas;
        private readonly Canvas _timeCanvas;

        public double NodeWidth { get; set; } = MALSpiderConstants.NodeWidth;
        public double NodeHeight { get; set; } = MALSpiderConstants.NodeHeight;

        private readonly Dictionary<EntryNode, Border> _nodeToBorder = new();
        private readonly Dictionary<EntryNode, List<Shape>> _nodeToConnections = new();
        private readonly Dictionary<Shape, (EntryNode Source, EntryNode Target, bool IsDownward)> _connectionInfo = new();

        public GraphRenderer(Canvas graphCanvas, Canvas timeCanvas)
        {
            _graphCanvas = graphCanvas;
            _timeCanvas = timeCanvas;
        }

        public void DrawGraph(NodeLayout layout)
        {
            _graphCanvas.Children.Clear();
            _timeCanvas.Children.Clear();
            _nodeToBorder.Clear();
            _nodeToConnections.Clear();
            _connectionInfo.Clear();

            // 1. Draw Axis
            DrawTimeAxis(layout.MinDate, layout.MaxDate, layout.VisibleDates, date => layout.Compressor.GetY(date, 100, MALSpiderConstants.VerticalSpacing));

            // 3. Draw Nodes
            foreach (var entry in layout.NodePositions)
            {
                DrawNode(entry.Key, entry.Value);
            }

            // 4. Draw Connections
            foreach (var group in layout.VisualRelations.GroupBy(r => r.Source))
            {
                var sourceNode = group.Key;
                var sourcePos = layout.NodePositions[sourceNode];
                var downwardTargets = new List<(Point Pos, string Label, EntryNode Node)>();
                var horizontalTargets = new List<(Point Pos, string Label, EntryNode Node)>();

                foreach (var rel in group)
                {
                    var targetNode = rel.Target;
                    var targetPos = layout.NodePositions[targetNode];
                    bool useSideAnchors = (sourcePos.Y + NodeHeight) > targetPos.Y || Math.Abs(sourcePos.X - targetPos.X) > 10;

                    if (useSideAnchors)
                    {
                        horizontalTargets.Add((targetPos, rel.Label, targetNode));
                    }
                    else
                    {
                        downwardTargets.Add((targetPos, rel.Label, targetNode));
                    }
                }

                if (downwardTargets.Count > 1) DrawBundledConnections(sourceNode, sourcePos, downwardTargets, true);
                else if (downwardTargets.Count == 1) DrawConnection(sourceNode, downwardTargets[0].Node, sourcePos, downwardTargets[0].Pos, downwardTargets[0].Label, false);

                foreach (var horiz in horizontalTargets) DrawConnection(sourceNode, horiz.Node, sourcePos, horiz.Pos, horiz.Label, true);
            }

            // 5. Update canvas size
            UpdateCanvasSize(layout.MaxRight + MALSpiderConstants.CanvasWidthMargin, layout.MaxBottom + MALSpiderConstants.CanvasHeightMargin);

            // 6. Draw Lane Background Text
            foreach (var header in layout.LaneHeaders)
            {
                // Draw background repeating text
                DrawLaneBackgroundText(header.Title, header.LaneLeft, header.LaneRight);
            }

            // 7. Draw Separators
            foreach (var sepX in layout.SeparatorXPositions)
            {
                DrawVerticalSeparator(sepX);
            }
        }

        private void DrawLaneBackgroundText(string title, double left, double right)
        {
            double laneWidth = right - left;
            double centerX = left + laneWidth / 2;

            string displayText = title;
            if (displayText.ToUpper() == "LIGHT NOVEL")
            {
                displayText = "LIGHT\nNOVEL";
            }

            // Repeating vertical text
            double canvasHeight = _graphCanvas.Height;
            if (double.IsNaN(canvasHeight)) canvasHeight = 3000;
            else canvasHeight = Math.Max(3000, canvasHeight);

            for (double y = 100; y < canvasHeight; y += 200)
            {
                var tb = new TextBlock
                {
                    Text = displayText,
                    FontSize = 40,
                    FontWeight = FontWeights.Black,
                    Foreground = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)), // Brighter (alpha 40)
                    IsHitTestVisible = false,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Width = Math.Max(10, laneWidth - 20)
                };

                tb.Measure(new Size(tb.Width, double.PositiveInfinity));
                Canvas.SetLeft(tb, centerX - tb.DesiredSize.Width / 2);
                Canvas.SetTop(tb, y);
                Panel.SetZIndex(tb, -100);
                _graphCanvas.Children.Add(tb);
            }
        }

        public void DrawVerticalSeparator(double x)
        {
            double height = _graphCanvas.Height;
            if (double.IsNaN(height)) height = 1000;
            else height = Math.Max(1000, height);

            var line = new Line
            {
                X1 = x,
                Y1 = 0,
                X2 = x,
                Y2 = height,
                Stroke = new SolidColorBrush(MALSpiderConstants.PrimaryAccentColor),
                StrokeThickness = 4,
                Opacity = 0.3
            };
            Panel.SetZIndex(line, -50);
            _graphCanvas.Children.Add(line);
        }

        public void Clear()
        {
            _graphCanvas.Children.Clear();
            _timeCanvas.Children.Clear();
            _nodeToBorder.Clear();
            _nodeToConnections.Clear();
            _connectionInfo.Clear();
        }

        public void UpdateCanvasSize(double width, double height)
        {
            _graphCanvas.Width = width;
            _graphCanvas.Height = height;
        }

        public Border GetBorderForNode(EntryNode node)
        {
            return _nodeToBorder.TryGetValue(node, out var border) ? border : null;
        }

        public void DrawTimeAxis(DateTime minDate, DateTime maxDate, List<DateTime> visibleDates, Func<DateTime?, double> getYForDate)
        {
            int startYear = minDate.Year;
            int endYear = maxDate.Year;

            var yearsWithNodes = new HashSet<int>(visibleDates.Select(d => d.Year));

            List<(int Year, double Y, bool HasNodes)> yearPlacements = new();
            for (int year = startYear; year <= endYear; year++)
            {
                var yearDate = new DateTime(year, 1, 1);
                if (yearDate < minDate) yearDate = minDate;
                if (yearDate > maxDate) yearDate = maxDate;
                yearPlacements.Add((year, getYForDate(yearDate), yearsWithNodes.Contains(year)));
            }

            for (int i = 0; i < yearPlacements.Count; i++)
            {
                var current = yearPlacements[i];

                // Ellipsis logic:
                // Skip if this year has no nodes AND it's not the first/last year
                // AND it's part of a gap of at least 2 consecutive years without nodes
                if (i > 0 && i < yearPlacements.Count - 1 && !current.HasNodes && yearPlacements[i - 1].HasNodes)
                {
                    // Find how long this gap is
                    int gapEnd = i;
                    while (gapEnd < yearPlacements.Count - 1 && !yearPlacements[gapEnd].HasNodes)
                    {
                        gapEnd++;
                    }

                    int gapLength = gapEnd - i;
                    if (gapLength >= 2)
                    {
                        double midY = (yearPlacements[i].Y + yearPlacements[gapEnd - 1].Y) / 2;
                        var ellipsis = new TextBlock
                        {
                            Text = "...",
                            Foreground = Brushes.Gray,
                            FontSize = MALSpiderConstants.TimeAxisYearFontSize,
                            FontWeight = FontWeights.Bold
                        };
                        Canvas.SetTop(ellipsis, midY - 15);
                        Canvas.SetRight(ellipsis, 10);
                        _timeCanvas.Children.Add(ellipsis);

                        i = gapEnd - 1; // Skip the gap
                        continue;
                    }
                }

                var yearLabel = new TextBlock
                {
                    Text = current.Year.ToString(),
                    Foreground = Brushes.Gray,
                    FontSize = MALSpiderConstants.TimeAxisYearFontSize,
                    FontWeight = FontWeights.Bold
                };
                Canvas.SetTop(yearLabel, current.Y - 10);
                Canvas.SetRight(yearLabel, 5);
                _timeCanvas.Children.Add(yearLabel);

                // Only draw months if we have space and it's a short range
                if ((maxDate - minDate).TotalDays < 365 * 10)
                {
                    for (int month = 4; month <= 10; month += 3)
                    {
                        var monthDate = new DateTime(current.Year, month, 1);
                        if (monthDate > maxDate || monthDate < minDate) continue;
                        double my = getYForDate(monthDate);

                        // Don't draw month if too close to year labels
                        if (Math.Abs(my - current.Y) < 15) continue;
                        if (i < yearPlacements.Count - 1 && Math.Abs(yearPlacements[i+1].Y - my) < 15) continue;

                        var monthLabel = new TextBlock
                        {
                            Text = monthDate.ToString("MMM"),
                            Foreground = Brushes.DarkGray,
                            FontSize = MALSpiderConstants.TimeAxisMonthFontSize
                        };
                        Canvas.SetTop(monthLabel, my - 6);
                        Canvas.SetRight(monthLabel, 5);
                        _timeCanvas.Children.Add(monthLabel);
                    }
                }
            }
        }

        public void DrawNode(EntryNode node, Point pos)
        {
            var border = CreateNodeBorder(node);
            _nodeToBorder[node] = border;

            Canvas.SetLeft(border, pos.X);
            Canvas.SetTop(border, pos.Y);
            _graphCanvas.Children.Add(border);
        }

        public Border CreateNodeBorder(EntryNode node)
        {
            string tooltipContent = $"{node.Title}\n{node.TitleEnglish}\n{node.TitleJapanese}\nReleased: {node.ReleaseDate?.ToShortDateString() ?? "Unknown"}";
            if (!string.IsNullOrEmpty(node.ErrorMessage))
            {
                tooltipContent += $"\n\nERROR: {node.ErrorMessage}";
            }
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
                Background = new SolidColorBrush(MALSpiderConstants.NodeBackgroundColor),
                BorderBrush = !string.IsNullOrEmpty(node.ErrorMessage) ? new SolidColorBrush(MALSpiderConstants.NodeErrorColor) : (node.IsInputRoot ? new SolidColorBrush(MALSpiderConstants.PrimaryAccentColor) : new SolidColorBrush(MALSpiderConstants.NodeBorderColor)),
                BorderThickness = node.IsInputRoot || !string.IsNullOrEmpty(node.ErrorMessage) ? new Thickness(4) : new Thickness(2),
                CornerRadius = new CornerRadius(MALSpiderConstants.NodeCornerRadius),
                Padding = new Thickness(MALSpiderConstants.NodePadding),
                Tag = node,
                Cursor = Cursors.Hand,
                ToolTip = new ToolTip
                {
                    Content = new TextBlock
                    {
                        Text = tooltipContent,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = MALSpiderConstants.ToolTipMaxWidth
                    },
                    FontSize = MALSpiderConstants.ToolTipFontSize
                }
            };

            border.MouseEnter += Node_MouseEnter;
            border.MouseLeave += Node_MouseLeave;

            var stack = new StackPanel();

            if (node.LoadedImage != null)
            {
                var img = new Image
                {
                    Source = node.LoadedImage,
                    Height = MALSpiderConstants.NodeImageHeight,
                    Margin = new Thickness(0, 0, 0, 8),
                    Stretch = Stretch.Uniform
                };
                stack.Children.Add(img);
            }
            else if (!string.IsNullOrEmpty(node.ImageUrl))
            {
                // Placeholder while loading
                stack.Children.Add(new Border
                {
                    Height = MALSpiderConstants.NodeImageHeight,
                    Margin = new Thickness(0, 0, 0, 8),
                    Background = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                    Child = new TextBlock
                    {
                        Text = "Loading...",
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = Brushes.Gray,
                        FontSize = 10
                    }
                });
            }

            if (!string.IsNullOrEmpty(node.SourceType))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = node.SourceType,
                    FontSize = MALSpiderConstants.NodeSourceTypeFontSize,
                    TextAlignment = TextAlignment.Center,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }

            var titleBlock = new TextBlock
            {
                Text = !string.IsNullOrEmpty(node.TitleEnglish) ? node.TitleEnglish : (string.IsNullOrEmpty(node.Title) ? "???" : node.Title),
                FontWeight = FontWeights.Bold,
                FontSize = MALSpiderConstants.NodeTitleFontSize,
                Foreground = !string.IsNullOrEmpty(node.ErrorMessage) ? new SolidColorBrush(MALSpiderConstants.NodeErrorColor) : Brushes.White,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 60
            };
            stack.Children.Add(titleBlock);

            if (!string.IsNullOrEmpty(node.ErrorMessage))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = node.ErrorMessage,
                    FontSize = MALSpiderConstants.NodeErrorFontSize,
                    Foreground = new SolidColorBrush(MALSpiderConstants.NodeErrorTextColor),
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 5, 0, 0)
                });

                if (!node.IsRetrying)
                {
                    var retryBtn = new Button
                    {
                        Content = "Retry",
                        Padding = new Thickness(10, 2, 10, 2),
                        Margin = new Thickness(0, 5, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Style = (Style)Application.Current.MainWindow.FindResource("ModernButton"),
                        FontSize = 10
                    };
                    retryBtn.Click += (s, e) => OnNodeRetryRequested?.Invoke(node);
                    stack.Children.Add(retryBtn);
                }
                else
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = "Retrying...",
                        FontSize = 10,
                        Foreground = Brushes.Gray,
                        TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(0, 5, 0, 0)
                    });
                }
            }

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
            return border;
        }

        private void Node_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Border border && border.Tag is EntryNode node)
            {
                HighlightNode(node, true);

                // Set tooltip content
                var tooltipPanel = new StackPanel { MaxWidth = 400 };

                tooltipPanel.Children.Add(new TextBlock
                {
                    Text = node.Title,
                    FontWeight = FontWeights.Bold,
                    FontSize = 16,
                    Foreground = new SolidColorBrush(MALSpiderConstants.PrimaryAccentColor),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0,0,0, (string.IsNullOrWhiteSpace(node.TitleRomaji) || node.TitleRomaji == node.Title) ? 5 : 0)
                });

                if (!string.IsNullOrWhiteSpace(node.TitleRomaji) && node.TitleRomaji != node.Title)
                {
                    tooltipPanel.Children.Add(new TextBlock
                    {
                        Text = node.TitleRomaji,
                        FontSize = 12,
                        Foreground = Brushes.LightSlateGray,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 5)
                    });
                }

                if (!string.IsNullOrEmpty(node.SourceType))
                {
                    tooltipPanel.Children.Add(new TextBlock
                    {
                        Text = $"Type: {node.SourceType}",
                        FontSize = 12,
                        Foreground = Brushes.Gray,
                        Margin = new Thickness(0,0,0,3)
                    });
                }

                if (node.ReleaseDate.HasValue)
                {
                    tooltipPanel.Children.Add(new TextBlock
                    {
                        Text = $"Released: {node.ReleaseDate.Value:MMM yyyy}",
                        FontSize = 12,
                        Foreground = Brushes.LightGray,
                        Margin = new Thickness(0,0,0,8)
                    });
                }

                if (!string.IsNullOrEmpty(node.Synopsis))
                {
                    tooltipPanel.Children.Add(new TextBlock
                    {
                        Text = node.Synopsis,
                        FontSize = 13,
                        Foreground = Brushes.White,
                        TextWrapping = TextWrapping.Wrap,
                        LineHeight = 18
                    });
                }

                border.ToolTip = new ToolTip { Content = tooltipPanel };
            }
        }

        private void Node_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Border border && border.Tag is EntryNode node)
            {
                HighlightNode(node, false);
            }
        }

        public void HighlightNode(EntryNode node, bool highlight)
        {
            if (highlight)
            {
                foreach (var b in _nodeToBorder.Values) b.Opacity = 0.2;
                foreach (var s in _connectionInfo.Keys) s.Opacity = 0.1;

                if (_nodeToBorder.TryGetValue(node, out var border))
                {
                    border.Opacity = 1.0;
                    border.BorderBrush = Brushes.Yellow;
                }

                if (_nodeToConnections.TryGetValue(node, out var connections))
                {
                    foreach (var shape in connections)
                    {
                        shape.Opacity = 1.0;
                        shape.Stroke = Brushes.Yellow;
                        shape.StrokeThickness = 3;
                        Panel.SetZIndex(shape, 10);

                        if (_connectionInfo.TryGetValue(shape, out var info))
                        {
                            var other = info.Source == node ? info.Target : info.Source;
                            if (_nodeToBorder.TryGetValue(other, out var otherBorder))
                            {
                                otherBorder.Opacity = 1.0;
                            }
                        }
                    }
                }
            }
            else
            {
                foreach (var b in _nodeToBorder.Values)
                {
                    b.Opacity = 1.0;
                    var n = (EntryNode)b.Tag;
                    b.BorderBrush = !string.IsNullOrEmpty(n.ErrorMessage) ? Brushes.Red : (n.IsInputRoot ? new SolidColorBrush(Color.FromRgb(187, 134, 252)) : new SolidColorBrush(Color.FromRgb(64, 64, 64)));
                }
                foreach (var shape in _connectionInfo.Keys)
                {
                    var info = _connectionInfo[shape];
                    shape.Opacity = 1.0;
                    shape.Stroke = info.IsDownward ? new SolidColorBrush(MALSpiderConstants.DownwardConnectionColor) : new SolidColorBrush(MALSpiderConstants.HorizontalConnectionColor);
                    shape.StrokeThickness = info.IsDownward ? 2 : 1;
                    Panel.SetZIndex(shape, 0);
                }
            }
        }

        public void DrawConnection(EntryNode source, EntryNode target, Point start, Point end, string label, bool? forcedUseSideAnchors = null)
        {
            bool useSideAnchors = forcedUseSideAnchors ?? ((start.Y + NodeHeight) > end.Y);
            Point pStart, pEnd;
            bool isDownward;

            if (useSideAnchors)
            {
                if (end.X > start.X + NodeWidth / 2) // To the right
                {
                    pStart = new Point(start.X + NodeWidth, start.Y + NodeHeight / 2);
                    pEnd = new Point(end.X, end.Y + NodeHeight / 2);
                }
                else if (end.X < start.X - NodeWidth / 2) // To the left
                {
                    pStart = new Point(start.X, start.Y + NodeHeight / 2);
                    pEnd = new Point(end.X + NodeWidth, end.Y + NodeHeight / 2);
                }
                else // Same column
                {
                    pStart = new Point(start.X, start.Y + NodeHeight / 2);
                    pEnd = new Point(end.X, end.Y + NodeHeight / 2);
                }
                isDownward = false;
            }
            else
            {
                pStart = new Point(start.X + NodeWidth / 2, start.Y + NodeHeight);
                pEnd = new Point(end.X + NodeWidth / 2, end.Y);
                isDownward = true;
            }

            var path = new Path
            {
                Stroke = isDownward ? new SolidColorBrush(MALSpiderConstants.DownwardConnectionColor) : new SolidColorBrush(MALSpiderConstants.HorizontalConnectionColor),
                StrokeThickness = isDownward ? 2 : 1
            };

            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = pStart };

            if (useSideAnchors)
            {
                double midX = pStart.X + (pEnd.X - pStart.X) / 2;
                if (Math.Abs(pStart.X - pEnd.X) < 1) midX -= 60; // Curve out to the left for same-column

                figure.Segments.Add(new BezierSegment(
                    new Point(midX, pStart.Y),
                    new Point(midX, pEnd.Y),
                    pEnd,
                    true));
            }
            else
            {
                double midY = pStart.Y + (pEnd.Y - pStart.Y) / 2;
                figure.Segments.Add(new BezierSegment(
                    new Point(pStart.X, midY),
                    new Point(pEnd.X, midY),
                    pEnd,
                    true));
            }

            geometry.Figures.Add(figure);

            if (isDownward)
            {
                AddArrowHead(geometry, new Point(pEnd.X, pEnd.Y - 10), pEnd);
            }
            else
            {
                // Side connection arrow
                double arrowX;
                if (Math.Abs(pStart.X - pEnd.X) < 1) arrowX = pEnd.X - 10; // Same column, curve from left
                else arrowX = end.X > start.X ? pEnd.X - 10 : pEnd.X + 10;

                AddArrowHead(geometry, new Point(arrowX, pEnd.Y), pEnd);
            }

            path.Data = geometry;

            _graphCanvas.Children.Add(path);
            RegisterConnection(path, source, target, isDownward);

            var labelBorder = new Border
            {
                Background = new SolidColorBrush(MALSpiderConstants.ConnectionLabelBackgroundColor),
                Padding = new Thickness(4, 2, 4, 2),
                CornerRadius = new CornerRadius(4),
                Child = new TextBlock { Text = label, FontSize = MALSpiderConstants.ConnectionLabelFontSize, Foreground = Brushes.LightGray }
            };

            if (useSideAnchors)
            {
                double midX = pStart.X + (pEnd.X - pStart.X) / 2;
                if (Math.Abs(pStart.X - pEnd.X) < 1) midX -= 60;

                Canvas.SetLeft(labelBorder, midX - 30);
                Canvas.SetTop(labelBorder, pStart.Y + (pEnd.Y - pStart.Y) / 2 - 10);
            }
            else
            {
                double midY = pStart.Y + (pEnd.Y - pStart.Y) / 2;
                Canvas.SetLeft(labelBorder, pStart.X + (pEnd.X - pStart.X) / 2 - 30);
                Canvas.SetTop(labelBorder, midY - 10);
            }
            _graphCanvas.Children.Add(labelBorder);
        }

        public void DrawBundledConnections(EntryNode source, Point start, List<(Point Pos, string Label, EntryNode Node)> targets, bool isDownward)
        {
            Point pStart = isDownward
                ? new Point(start.X + NodeWidth / 2, start.Y + NodeHeight)
                : new Point(start.X + NodeWidth / 2, start.Y);

            double bundleY = isDownward ? pStart.Y + 20 : pStart.Y - 20;

            var trunk = new Line
            {
                X1 = pStart.X, Y1 = pStart.Y,
                X2 = pStart.X, Y2 = bundleY,
                Stroke = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                StrokeThickness = 2
            };
            _graphCanvas.Children.Add(trunk);

            foreach (var target in targets)
            {
                Point pEnd = isDownward
                    ? new Point(target.Pos.X + NodeWidth / 2, target.Pos.Y)
                    : new Point(target.Pos.X + NodeWidth / 2, target.Pos.Y + NodeHeight);

                var path = new Path
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                    StrokeThickness = 2
                };

                var geometry = new PathGeometry();
                var figure = new PathFigure { StartPoint = new Point(pStart.X, bundleY) };
                figure.Segments.Add(new BezierSegment(
                    new Point(pStart.X, bundleY + (pEnd.Y - bundleY) / 2),
                    new Point(pEnd.X, bundleY + (pEnd.Y - bundleY) / 2),
                    pEnd,
                    true));
                geometry.Figures.Add(figure);

                if (isDownward) AddArrowHead(geometry, new Point(pEnd.X, pEnd.Y - 10), pEnd);
                path.Data = geometry;

                _graphCanvas.Children.Add(path);
                RegisterConnection(path, source, target.Node, isDownward);

                var labelBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)),
                    Padding = new Thickness(4, 2, 4, 2),
                    CornerRadius = new CornerRadius(4),
                    Child = new TextBlock { Text = target.Label, FontSize = 10, Foreground = Brushes.LightGray }
                };
                Canvas.SetLeft(labelBorder, pEnd.X - 30);
                Canvas.SetTop(labelBorder, pEnd.Y - (isDownward ? 30 : -10));
                _graphCanvas.Children.Add(labelBorder);
            }
        }

        private void RegisterConnection(Shape shape, EntryNode source, EntryNode target, bool isDownward)
        {
            _connectionInfo[shape] = (source, target, isDownward);
            if (!_nodeToConnections.ContainsKey(source)) _nodeToConnections[source] = new List<Shape>();
            if (!_nodeToConnections.ContainsKey(target)) _nodeToConnections[target] = new List<Shape>();
            _nodeToConnections[source].Add(shape);
            _nodeToConnections[target].Add(shape);
        }

        private void AddArrowHead(PathGeometry geometry, Point start, Point end)
        {
            double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
            Point p1 = new Point(end.X - 10 * Math.Cos(angle - Math.PI / 6), end.Y - 10 * Math.Sin(angle - Math.PI / 6));
            Point p2 = new Point(end.X - 10 * Math.Cos(angle + Math.PI / 6), end.Y - 10 * Math.Sin(angle + Math.PI / 6));

            var figure = new PathFigure { StartPoint = end, IsClosed = true };
            figure.Segments.Add(new LineSegment(p1, true));
            figure.Segments.Add(new LineSegment(p2, true));
            geometry.Figures.Add(figure);
        }
    }
}
