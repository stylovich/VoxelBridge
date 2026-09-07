using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal enum VoxelSelectionTool { Brush, Rectangle }
    internal enum VoxelSelectionMode { Replace, Add, Subtract }

    // Screen-space selection transaction. Only the nearest cell at each GUI-pixel sample
    // is considered. The caller commits Result after the complete gesture succeeds.
    internal sealed class VoxelSurfaceSelection : IDisposable
    {
        internal const int MaximumSamples = 1048576;
        private const int MaximumSegments = 4096;
        private readonly VoxelGrid grid;
        private readonly Camera camera;
        private readonly Vector2 size;
        private readonly VoxelSelectionMode mode;
        private readonly Queue<IEnumerator<Vector2>> segments = new();
        private readonly HashSet<Vector2Int> visited = new();
        private int segmentCount;
        private int work;
        internal HashSet<int> Result { get; }
        internal int Samples => visited.Count;
        internal bool IsIdle => segments.Count == 0;

        internal VoxelSurfaceSelection(VoxelGrid grid, Camera camera, Vector2 size,
            VoxelSelectionMode mode, IEnumerable<int> original)
        {
            if (grid == null || camera == null) throw new ArgumentNullException();
            if (!float.IsFinite(size.x) || !float.IsFinite(size.y) || size.x <= 0 || size.y <= 0 || size.x > 8192 || size.y > 8192)
                throw new ArgumentException("The selection viewport must be between 1 and 8192 GUI pixels per axis.");
            if (!Enum.IsDefined(typeof(VoxelSelectionMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            this.grid = grid; this.camera = camera; this.size = size; this.mode = mode;
            Result = mode == VoxelSelectionMode.Replace ? new HashSet<int>() : new HashSet<int>(original);
            if (Result.Count > VoxelSurfaceEdit.MaximumSelection) throw new InvalidOperationException("Selection limit exceeded.");
        }

        internal void Brush(Vector2 from, Vector2 to, float diameter)
        {
            ValidatePoint(from); ValidatePoint(to);
            if (!float.IsFinite(diameter) || diameter < 1 || diameter > 128) throw new ArgumentOutOfRangeException(nameof(diameter));
            Enqueue(BrushSamples(from, to, diameter * .5f));
        }

        internal void Rectangle(Vector2 from, Vector2 to)
        {
            ValidatePoint(from); ValidatePoint(to);
            Enqueue(RectangleSamples(Clamp(from), Clamp(to)));
        }

        private void Enqueue(IEnumerable<Vector2> samples)
        {
            if (++segmentCount > MaximumSegments) throw new InvalidOperationException("The selection stroke is too long. Use shorter strokes.");
            segments.Enqueue(samples.GetEnumerator());
        }

        internal void Step(int maximumWork)
        {
            if (maximumWork < 1) throw new ArgumentOutOfRangeException(nameof(maximumWork));
            while (maximumWork-- > 0 && segments.Count > 0)
            {
                var segment = segments.Peek();
                if (!segment.MoveNext()) { segment.Dispose(); segments.Dequeue(); continue; }
                if (++work > MaximumSamples * 4) throw new InvalidOperationException("The selection stroke is too dense. Use shorter strokes.");
                Vector2 point = segment.Current;
                var pixel = new Vector2Int(Mathf.FloorToInt(point.x), Mathf.FloorToInt(point.y));
                if (pixel.x < 0 || pixel.y < 0 || point.x >= size.x || point.y >= size.y || !visited.Add(pixel)) continue;
                if (visited.Count > MaximumSamples) throw new InvalidOperationException("Selection area exceeds 1,048,576 GUI pixels. Select a smaller area.");
                Ray ray = camera.ViewportPointToRay(new Vector3(point.x / size.x, 1 - point.y / size.y, 0));
                if (!VoxelSurfaceEdit.Pick(grid, ray, out int cell)) continue;
                if (mode == VoxelSelectionMode.Subtract) Result.Remove(cell);
                else if (!Result.Contains(cell))
                {
                    if (Result.Count == VoxelSurfaceEdit.MaximumSelection)
                        throw new InvalidOperationException("Selection limit reached. Use a smaller area or clear the selection.");
                    Result.Add(cell);
                }
            }
        }

        private IEnumerable<Vector2> RectangleSamples(Vector2 from, Vector2 to)
        {
            Rect area = Area(from, to);
            if (area.width < 1 && area.height < 1) { yield return from; yield break; }
            for (int y = Mathf.FloorToInt(area.yMin); y < Mathf.CeilToInt(area.yMax); y++)
                for (int x = Mathf.FloorToInt(area.xMin); x < Mathf.CeilToInt(area.xMax); x++)
                {
                    var point = new Vector2(x + .5f, y + .5f);
                    if (area.Contains(point)) yield return point;
                }
        }

        private IEnumerable<Vector2> BrushSamples(Vector2 from, Vector2 to, float radius)
        {
            // Include the precise cursor position for a one-pixel click.
            yield return to;
            Vector2 delta = to - from;
            float lengthSquared = delta.sqrMagnitude;
            Rect bounds = Area(Vector2.Min(from, to) - Vector2.one * radius, Vector2.Max(from, to) + Vector2.one * radius);
            for (int y = Mathf.Max(0, Mathf.FloorToInt(bounds.yMin)); y < Mathf.Min(size.y, Mathf.CeilToInt(bounds.yMax)); y++)
                for (int x = Mathf.Max(0, Mathf.FloorToInt(bounds.xMin)); x < Mathf.Min(size.x, Mathf.CeilToInt(bounds.xMax)); x++)
                {
                    var point = new Vector2(x + .5f, y + .5f);
                    float t = lengthSquared > 0 ? Mathf.Clamp01(Vector2.Dot(point - from, delta) / lengthSquared) : 0;
                    if ((point - (from + delta * t)).sqrMagnitude <= radius * radius) yield return point;
                }
        }

        internal static Rect Area(Vector2 from, Vector2 to) => Rect.MinMaxRect(
            Mathf.Min(from.x, to.x), Mathf.Min(from.y, to.y), Mathf.Max(from.x, to.x), Mathf.Max(from.y, to.y));

        private Vector2 Clamp(Vector2 point) => new(Mathf.Clamp(point.x, 0, size.x), Mathf.Clamp(point.y, 0, size.y));
        private static void ValidatePoint(Vector2 point)
        {
            if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || Mathf.Abs(point.x) > 32768 || Mathf.Abs(point.y) > 32768)
                throw new ArgumentException("Invalid selection coordinates.");
        }

        public void Dispose()
        {
            while (segments.Count > 0) segments.Dequeue().Dispose();
        }
    }
}
