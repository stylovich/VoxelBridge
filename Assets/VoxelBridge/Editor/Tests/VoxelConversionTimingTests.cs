using System;
using System.Linq;
using NUnit.Framework;

namespace LocalModels.VoxelBridge.Tests
{
    public class VoxelConversionTimingTests
    {
        [Test]
        public void NestedStages_AreExclusiveAndAccumulateRepeatedWork()
        {
            double now = 0;
            var timing = new VoxelConversionTiming(() => now);
            now = 2;
            using (timing.Measure("Preparation"))
            {
                now = 5;
                using (timing.Measure("Voxelization")) now = 12;
                now = 16;
            }
            using (timing.Measure("Voxelization")) now = 19;
            now = 20;
            var report = timing.Snapshot("fixture", "completed");
            Assert.That(report.totalMilliseconds, Is.EqualTo(20));
            Assert.That(report.stages.Sum(s => s.milliseconds), Is.EqualTo(20));
            Assert.That(report.stages.Single(s => s.name == "Preparation").milliseconds, Is.EqualTo(7));
            Assert.That(report.stages.Single(s => s.name == "Voxelization").milliseconds, Is.EqualTo(10));
            Assert.That(report.stages.Single(s => s.name == "Other").milliseconds, Is.EqualTo(3));
        }

        [Test]
        public void Failure_UnwindsStagesAndRepeatedDisposeIsSafe()
        {
            double now = 0;
            var timing = new VoxelConversionTiming(() => now);
            var scope = timing.Measure("Write");
            Assert.Throws<OperationCanceledException>(() =>
            {
                using (scope) { now = 5; throw new OperationCanceledException(); }
            });
            scope.Dispose();
            now = 8;
            var report = timing.Snapshot("fixture", "cancelled");
            Assert.That(report.stages.Single(s => s.name == "Write").milliseconds, Is.EqualTo(5));
            Assert.That(report.stages.Single(s => s.name == "Other").milliseconds, Is.EqualTo(3));
        }
    }
}
