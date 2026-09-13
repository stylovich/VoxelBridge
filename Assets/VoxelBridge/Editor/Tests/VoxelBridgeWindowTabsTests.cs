using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public class VoxelBridgeWindowTabsTests
    {
        [Test]
        public void Tabs_KeepSharedAndSpecificSettingsIndependent()
        {
            var window = ScriptableObject.CreateInstance<VoxelBridgeWindow>();
            try
            {
                var serialized = new SerializedObject(window);
                serialized.FindProperty("normalizeScale").boolValue = false;
                serialized.FindProperty("individualIgnoreInactiveObjects").boolValue = true;
                serialized.FindProperty("batchIgnoreInactiveObjects").boolValue = false;
                serialized.FindProperty("individualMemoryBudgetMb").intValue = 512;
                serialized.FindProperty("batchMemoryBudgetMb").intValue = 2048;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                foreach (VoxelBridgeWindow.WorkflowTab tab in Enum.GetValues(typeof(VoxelBridgeWindow.WorkflowTab)))
                {
                    window.SelectTab(tab);
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    var single = (VoxelLodBuildOptions)typeof(VoxelBridgeWindow).GetMethod("CreateLodOptions", flags).Invoke(window, null);
                    var batch = (VoxelLodBuildOptions)typeof(VoxelBridgeWindow).GetMethod("CreateBatchLodOptions", flags).Invoke(window, null);
                    Assert.That(single.NormalizeScale, Is.False);
                    Assert.That(batch.NormalizeScale, Is.False);
                    Assert.That(single.IncludeInactiveObjects, Is.False);
                    Assert.That(batch.IncludeInactiveObjects, Is.True);
                    serialized.Update();
                    Assert.That(serialized.FindProperty("individualMemoryBudgetMb").intValue, Is.EqualTo(512));
                    Assert.That(serialized.FindProperty("batchMemoryBudgetMb").intValue, Is.EqualTo(2048));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(window); }
        }
    }
}
