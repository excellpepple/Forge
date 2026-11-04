// Lane-layout algorithm ported from SourceGit (https://github.com/sourcegit-scm/sourcegit),
// Copyright (c) sourcegit-scm contributors, MIT License:
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//
// Ported from src/Models/CommitGraph.cs (Avalonia -> WPF types) and trimmed:
// Forge doesn't need SourceGit's commit-selection highlighting modes, so
// CommitGraphHighlighting and everything built on it were dropped. The lane-
// assignment geometry itself (PathHelper, ColorPicker, Generate's main loop)
// is the part actually being reused and is structurally unchanged.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Forge.Models;

public sealed class CommitGraph
{
    public sealed class Path
    {
        public List<Point> Points { get; } = new();
        public int Color { get; }
        public Path(int color) => Color = color;
    }

    public sealed class Link
    {
        public Point Start;
        public Point Control;
        public Point End;
        public int Color;
    }

    public enum DotType { Default, Head, Merge }

    public sealed class Dot
    {
        public DotType Type;
        public Point Center;
        public int Color;
    }

    public List<Path> Paths { get; } = new();
    public List<Link> Links { get; } = new();
    public List<Dot> Dots { get; } = new();

    private static readonly List<Color> PenColors = new()
    {
        Colors.Orange, Colors.ForestGreen, Colors.Turquoise, Colors.Olive, Colors.Magenta,
        Colors.Red, Colors.Khaki, Colors.Lime, Colors.RoyalBlue, Colors.Teal
    };

    public static Color ColorForLane(int laneColorIndex) => PenColors[laneColorIndex % PenColors.Count];

    // unitWidth/unitHeight are a relative grid, not pixels -- the view
    // multiplies by its own row height and lane spacing when drawing.
    public static CommitGraph Generate(List<GraphCommit> commits, bool firstParentOnly)
    {
        const double unitWidth = 12;
        const double halfWidth = 6;
        const double unitHeight = 1;
        const double halfHeight = 0.5;

        var graph = new CommitGraph();
        var unsolved = new List<PathHelper>();
        var ended = new List<PathHelper>();
        var offsetY = -halfHeight;
        var colorPicker = new ColorPicker(PenColors.Count);

        foreach (var commit in commits)
        {
            PathHelper? major = null;
            offsetY += unitHeight;

            var offsetX = 4 - halfWidth;
            var maxOffsetOld = unsolved.Count > 0 ? unsolved[^1].LastX : offsetX + unitWidth;

            foreach (var l in unsolved)
            {
                if (string.Equals(l.Next, commit.Sha, StringComparison.Ordinal))
                {
                    if (major == null)
                    {
                        offsetX += unitWidth;
                        major = l;

                        if (commit.Parents.Count > 0)
                        {
                            major.Next = commit.Parents[0];
                            major.Goto(offsetX, offsetY, halfHeight);
                        }
                        else
                        {
                            major.End(offsetX, offsetY, halfHeight);
                            ended.Add(l);
                        }
                    }
                    else
                    {
                        l.End(major.LastX, offsetY, halfHeight);
                        ended.Add(l);
                    }
                }
                else
                {
                    offsetX += unitWidth;
                    l.Pass(offsetX, offsetY, halfHeight);
                }
            }

            foreach (var l in ended)
            {
                colorPicker.Recycle(l.Path.Color);
                unsolved.Remove(l);
            }
            ended.Clear();

            // No path found: new curve for a branch head. Otherwise: this
            // commit merges into an already-tracked curve.
            if (major == null)
            {
                offsetX += unitWidth;
                if (commit.Parents.Count > 0)
                {
                    major = new PathHelper(commit.Parents[0], colorPicker.Next(), new Point(offsetX, offsetY));
                    unsolved.Add(major);
                    graph.Paths.Add(major.Path);
                }
            }

            var position = new Point(major?.LastX ?? offsetX, offsetY);
            var dotColor = major?.Path.Color ?? 0;
            var dot = new Dot
            {
                Center = position,
                Color = dotColor,
                Type = commit.IsCurrentHead ? DotType.Head : commit.Parents.Count > 1 ? DotType.Merge : DotType.Default
            };
            graph.Dots.Add(dot);

            if (!firstParentOnly)
            {
                for (var j = 1; j < commit.Parents.Count; j++)
                {
                    var parentSha = commit.Parents[j];
                    var parent = unsolved.Find(x => string.Equals(x.Next, parentSha, StringComparison.Ordinal));
                    if (parent != null)
                    {
                        graph.Links.Add(new Link
                        {
                            Start = position,
                            End = new Point(parent.LastX, offsetY + halfHeight),
                            Control = new Point(parent.LastX, position.Y),
                            Color = parent.Path.Color
                        });
                    }
                    else
                    {
                        offsetX += unitWidth;
                        var l = new PathHelper(parentSha, colorPicker.Next(), position, new Point(offsetX, position.Y + halfHeight));
                        unsolved.Add(l);
                        graph.Paths.Add(l.Path);
                    }
                }
            }

            commit.Color = dotColor;
            commit.LeftMargin = Math.Max(offsetX, maxOffsetOld) + halfWidth + 2;
        }

        // Dangling curves (e.g. a merged-in side branch under --first-parent,
        // or any branch tip in full mode) get extended to the bottom of the graph.
        for (var i = 0; i < unsolved.Count; i++)
        {
            var path = unsolved[i];
            var endY = (commits.Count - 0.5) * unitHeight;
            if (path.Path.Points.Count == 1 && Math.Abs(path.Path.Points[0].Y - endY) < 0.0001) continue;
            path.End((i + 0.5) * unitWidth + 4, endY + halfHeight, halfHeight);
        }

        return graph;
    }

    private sealed class ColorPicker
    {
        private readonly Queue<int> _queue = new();
        private readonly int _penCount;
        public ColorPicker(int penCount) => _penCount = penCount;

        public int Next()
        {
            if (_queue.Count == 0)
                for (var i = 0; i < _penCount; i++) _queue.Enqueue(i);
            return _queue.Dequeue();
        }

        public void Recycle(int idx)
        {
            if (!_queue.Contains(idx)) _queue.Enqueue(idx);
        }
    }

    private sealed class PathHelper
    {
        public Path Path { get; private set; }
        public string Next { get; set; }
        public double LastX { get; private set; }
        private double _lastY;
        private double _endY;

        public PathHelper(string next, int color, Point start)
        {
            Next = next;
            LastX = start.X;
            _lastY = start.Y;
            Path = new Path(color);
            Path.Points.Add(start);
        }

        public PathHelper(string next, int color, Point start, Point to)
        {
            Next = next;
            LastX = to.X;
            _lastY = to.Y;
            Path = new Path(color);
            Path.Points.Add(start);
            Path.Points.Add(to);
        }

        // A path that just passed this row without a commit on it.
        public void Pass(double x, double y, double halfHeight)
        {
            if (x > LastX) { Add(LastX, _lastY); Add(x, y - halfHeight); }
            else if (x < LastX) { Add(LastX, y - halfHeight); y += halfHeight; Add(x, y); }
            LastX = x; _lastY = y;
        }

        // A path that has a commit in this row but continues (has a parent).
        public void Goto(double x, double y, double halfHeight)
        {
            if (x > LastX) { Add(LastX, _lastY); Add(x, y - halfHeight); }
            else if (x < LastX)
            {
                var minY = y - halfHeight;
                if (minY > _lastY) minY -= halfHeight;
                Add(LastX, minY); Add(x, y);
            }
            LastX = x; _lastY = y;
        }

        // A path that has a commit in this row and ends (root commit, or merged away).
        public void End(double x, double y, double halfHeight)
        {
            if (x > LastX) { Add(LastX, _lastY); Add(x, y - halfHeight); }
            else if (x < LastX) { Add(LastX, y - halfHeight); }
            Add(x, y);
            LastX = x; _lastY = y;
        }

        private void Add(double x, double y)
        {
            if (_endY < y) { Path.Points.Add(new Point(x, y)); _endY = y; }
        }
    }
}
