using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MALSpider.Models;

namespace MALSpider.Graph
{
    public class GraphCarousel
    {
        private class CarouselItem
        {
            public EntryNode Node { get; set; } = null!;
            public double TargetSlot { get; set; }
            public double CurrentSlot { get; set; }
            public double Alpha { get; set; }
            public double Scale { get; set; }
        }

        private readonly Canvas _canvas;
        private readonly GraphRenderer _renderer;
        private readonly List<CarouselItem> _items = new();

        private double _animationAngle;

        public GraphCarousel(Canvas canvas, GraphRenderer renderer)
        {
            _canvas = canvas;
            _renderer = renderer;
        }

        public void AddNode(EntryNode node)
        {
            lock (_items)
            {
                if (_items.Any(i => i.Node == node)) return;

                foreach (var item in _items)
                {
                    item.TargetSlot++;
                }

                _items.Add(new CarouselItem
                {
                    Node = node,
                    TargetSlot = 0,
                    CurrentSlot = -1,
                    Alpha = 0,
                    Scale = MALSpiderConstants.CarouselScaleMin
                });
            }
        }

        public void Update(double availableWidth, double availableHeight)
        {
            _canvas.Children.Clear();

            double centerX = availableWidth / 2;
            double centerY = availableHeight / 2;

            if (double.IsNaN(centerX) || centerX <= 0) centerX = 400;
            if (double.IsNaN(centerY) || centerY <= 0) centerY = 200;

            _animationAngle += 5;
            if (_animationAngle >= 360) _animationAngle = 0;

            lock (_items)
            {
                foreach (var item in _items.ToList())
                {
                    double lerp = MALSpiderConstants.CarouselLerpFactor;
                    item.CurrentSlot += (item.TargetSlot - item.CurrentSlot) * lerp;

                    bool isVisibleSlot = item.TargetSlot >= 0 && item.TargetSlot < MALSpiderConstants.CarouselMaxItems;
                    double targetAlpha = isVisibleSlot ? 1.0 : 0.0;
                    double targetScale = isVisibleSlot ? 1.0 : MALSpiderConstants.CarouselScaleMin;

                    item.Alpha += (targetAlpha - item.Alpha) * lerp;
                    item.Scale += (targetScale - item.Scale) * lerp;

                    if (item.Alpha < 0.01 && item.TargetSlot >= MALSpiderConstants.CarouselMaxItems)
                    {
                        _items.Remove(item);
                        continue;
                    }

                    double x = centerX + ((MALSpiderConstants.CarouselMaxItems - 1) / 2.0 - item.CurrentSlot) * MALSpiderConstants.CarouselSpacing;
                    double y = centerY - _renderer.NodeHeight / 2 - 50;

                    var nodeVisual = _renderer.CreateNodeBorder(item.Node);
                    nodeVisual.Opacity = item.Alpha;
                    nodeVisual.RenderTransformOrigin = new Point(0.5, 0.5);
                    nodeVisual.RenderTransform = new ScaleTransform(item.Scale, item.Scale);

                    Canvas.SetLeft(nodeVisual, x - _renderer.NodeWidth / 2);
                    Canvas.SetTop(nodeVisual, y);
                    _canvas.Children.Add(nodeVisual);
                }
            }

            // Draw loading dots below carousel
            double dotsCenterY = centerY + _renderer.NodeHeight / 2 + 20;
            for (int i = 0; i < 8; i++)
            {
                double angle = _animationAngle + (i * 45);
                double rad = angle * Math.PI / 180;
                double x = centerX + Math.Cos(rad) * 40;
                double y = dotsCenterY + Math.Sin(rad) * 40;

                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = new SolidColorBrush(MALSpiderConstants.PrimaryAccentColor),
                    Opacity = (i + 1) / 8.0
                };

                Canvas.SetLeft(dot, x - 6);
                Canvas.SetTop(dot, y - 6);
                _canvas.Children.Add(dot);
            }

            var text = new TextBlock
            {
                Text = "Crawling MyAnimeList...",
                Foreground = Brushes.White,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Width = 300,
                TextAlignment = TextAlignment.Center
            };
            Canvas.SetLeft(text, centerX - 150);
            Canvas.SetTop(text, dotsCenterY + 70);
            _canvas.Children.Add(text);
        }

        public void Clear()
        {
            lock (_items)
            {
                _items.Clear();
            }
            _canvas.Children.Clear();
        }

        public bool HasItems
        {
            get
            {
                lock (_items)
                {
                    return _items.Count > 0;
                }
            }
        }
    }
}
