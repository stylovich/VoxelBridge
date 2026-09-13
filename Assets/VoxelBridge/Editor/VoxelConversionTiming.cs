using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace LocalModels.VoxelBridge
{
    // Exclusive wall-clock stages: nested scopes pause their parent, never double-count it.
    // Owned by one synchronous conversion or batch; no global timing state.
    internal sealed class VoxelConversionTiming
    {
        [Serializable] internal sealed class Stage { public string name; public double milliseconds; }
        [Serializable] internal sealed class Report
        {
            public string source, status;
            public double totalMilliseconds;
            public Stage[] stages;
        }

        private readonly Func<double> clock;
        private readonly Dictionary<string, double> elapsed = new();
        private readonly double started;
        private double last;
        private string current = "Other";

        internal VoxelConversionTiming(Func<double> clock = null)
        {
            this.clock = clock ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
            started = last = this.clock();
        }

        private void Accumulate()
        {
            double now = clock();
            elapsed.TryGetValue(current, out double previous);
            elapsed[current] = previous + Math.Max(0, now - last);
            last = now;
        }

        internal IDisposable Measure(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("A stage name is required.", nameof(name));
            Accumulate();
            var scope = new Scope(this, current);
            current = name;
            return scope;
        }

        private sealed class Scope : IDisposable
        {
            private VoxelConversionTiming owner;
            private readonly string previous;
            internal Scope(VoxelConversionTiming owner, string previous) { this.owner = owner; this.previous = previous; }
            public void Dispose()
            {
                if (owner == null) return;
                owner.Accumulate(); owner.current = previous; owner = null;
            }
        }

        internal Report Snapshot(string source, string status)
        {
            Accumulate();
            return new Report { source = source, status = status, totalMilliseconds = last - started,
                stages = elapsed.OrderByDescending(p => p.Value).Select(p => new Stage { name = p.Key, milliseconds = p.Value }).ToArray() };
        }

        internal static T Run<T>(string source, Func<VoxelConversionTiming, T> action)
        {
            var timing = new VoxelConversionTiming();
            string status = "failed";
            try
            {
                T result = action(timing);
                status = result is VoxelLodBatchBuildResult batch
                    ? batch.Cancelled ? "cancelled" : batch.FailedCount > 0 ? "completed with failures" : "completed"
                    : "completed";
                return result;
            }
            catch (OperationCanceledException) { status = "cancelled"; throw; }
            finally
            {
                // Diagnostics must never mask a conversion failure or roll back successful assets.
                try
                {
                    Report report = timing.Snapshot(source, status);
                    string directory = Path.GetFullPath(Path.Combine(Application.dataPath, "../Logs/VoxelBridge"));
                    Directory.CreateDirectory(directory);
                    string path = Path.Combine(directory, $"ConversionTiming-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
                    File.WriteAllText(path, JsonUtility.ToJson(report, true));
                    Debug.Log($"Voxel Bridge timing · {source} · {status} · {report.totalMilliseconds / 1000:0.00}s\n" +
                        string.Join(" · ", report.stages.Select(s => $"{s.name}: {s.milliseconds / 1000:0.00}s")) + "\n" + path);
                }
                catch (Exception e) { Debug.LogWarning("Could not write Voxel Bridge timing report: " + e.Message); }
            }
        }
    }
}
