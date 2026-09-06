using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace LocalModels.VoxelBridge.Diagnostics
{
    public enum VoxelStressMode { Idle, Spawning, Clearing, Failed }

    public struct VoxelStressSpawner : IComponentData
    {
        public Entity Prefab;
        public int Count, Columns, InstancesPerFrame;
        public float Spacing;
        public float3 Origin;
        public quaternion Rotation;
        public VoxelStressMode Mode;
    }

    // Survives deletion of the spawner so its remaining instances can be removed on subscene unload.
    [InternalBufferCapacity(0)]
    public struct VoxelStressSpawnedRoot : ICleanupBufferElementData { public Entity Value; }

    [WorldSystemFilter(WorldSystemFilterFlags.Default)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateBefore(typeof(TransformSystemGroup))]
    public partial class VoxelStressSpawnSystem : SystemBase
    {
        public const int MaximumInstances = 10_000;
        public const int MaximumEntities = 250_000;
        private EntityQuery spawners, abandoned;

        protected override void OnCreate()
        {
            spawners = GetEntityQuery(ComponentType.ReadWrite<VoxelStressSpawner>(), ComponentType.ReadWrite<VoxelStressSpawnedRoot>());
            abandoned = GetEntityQuery(new EntityQueryDesc {
                All = new[] { ComponentType.ReadWrite<VoxelStressSpawnedRoot>() },
                None = new[] { ComponentType.ReadOnly<VoxelStressSpawner>() },
                Options = EntityQueryOptions.IncludeDisabledEntities });
        }

        public static void Validate(VoxelStressSpawner settings)
        {
            if (settings.Count < 1 || settings.Count > MaximumInstances || settings.Columns < 1 || settings.Columns > MaximumInstances ||
                settings.InstancesPerFrame < 1 || settings.InstancesPerFrame > 256 || !math.isfinite(settings.Spacing) || settings.Spacing < .001f ||
                !math.all(math.isfinite(settings.Origin)) || !math.all(math.isfinite(settings.Rotation.value)) ||
                math.abs(math.lengthsq(settings.Rotation.value) - 1) > .001f)
                throw new ArgumentException("Invalid stress spawner settings.");
            float span = settings.Spacing * settings.Count;
            if (!math.isfinite(span) || !math.all(math.isfinite(math.abs(settings.Origin) + span)))
                throw new ArgumentException("The requested distribution exceeds finite world coordinates.");
        }

        public static float3 Position(VoxelStressSpawner settings, int index)
        {
            int columns = math.min(settings.Columns, settings.Count);
            int rows = (settings.Count + columns - 1) / columns;
            float3 offset = new float3(index % columns - (columns - 1) * .5f, 0, index / columns - (rows - 1) * .5f) * settings.Spacing;
            return settings.Origin + math.rotate(settings.Rotation, offset);
        }

        protected override void OnUpdate()
        {
            using (var owners = abandoned.ToEntityArray(Allocator.Temp))
                foreach (var owner in owners)
                {
                    Clear(owner, 64);
                    if (EntityManager.GetBuffer<VoxelStressSpawnedRoot>(owner).Length == 0)
                        EntityManager.RemoveComponent<VoxelStressSpawnedRoot>(owner);
                }
            using var active = spawners.ToEntityArray(Allocator.Temp);
            foreach (var owner in active)
            {
                var settings = EntityManager.GetComponentData<VoxelStressSpawner>(owner);
                if (settings.Mode != VoxelStressMode.Spawning && settings.Mode != VoxelStressMode.Clearing) continue;
                try
                {
                    if (settings.Mode == VoxelStressMode.Clearing)
                    {
                        Clear(owner, math.clamp(settings.InstancesPerFrame, 1, 256));
                        if (EntityManager.GetBuffer<VoxelStressSpawnedRoot>(owner).Length == 0) settings.Mode = VoxelStressMode.Idle;
                    }
                    else
                    {
                        Validate(settings);
                        ValidatePrefab(EntityManager, settings);
                        int existing = EntityManager.GetBuffer<VoxelStressSpawnedRoot>(owner).Length;
                        int count = math.min(settings.InstancesPerFrame, settings.Count - existing);
                        if (count > 0)
                        {
                            using var instances = new NativeArray<Entity>(count, Allocator.Temp);
                            EntityManager.Instantiate(settings.Prefab, instances);
                            // Reacquire the buffer after structural changes. Register every clone before configuring it.
                            var owned = EntityManager.GetBuffer<VoxelStressSpawnedRoot>(owner);
                            foreach (var instance in instances) owned.Add(new VoxelStressSpawnedRoot { Value = instance });
                            for (int i = 0; i < count; i++)
                            {
                                var transform = EntityManager.GetComponentData<LocalTransform>(instances[i]);
                                transform.Position = Position(settings, existing + i);
                                transform.Rotation = math.mul(settings.Rotation, transform.Rotation);
                                EntityManager.SetComponentData(instances[i], transform);
                            }
                        }
                        if (existing + count >= settings.Count) settings.Mode = VoxelStressMode.Idle;
                    }
                }
                catch (Exception ex)
                {
                    settings.Mode = VoxelStressMode.Failed;
                    Debug.LogError($"Voxel stress spawn failed for entity {owner.Index}: {ex.Message}");
                }
                EntityManager.SetComponentData(owner, settings);
            }
        }

        public static void ValidatePrefab(EntityManager manager, VoxelStressSpawner settings)
        {
            if (!manager.Exists(settings.Prefab) || !manager.HasComponent<Prefab>(settings.Prefab) ||
                !manager.HasComponent<LocalTransform>(settings.Prefab) || manager.HasComponent<Parent>(settings.Prefab))
                throw new InvalidOperationException("The baked prefab root is unavailable or has no independent dynamic transform.");
            int linked = manager.HasBuffer<LinkedEntityGroup>(settings.Prefab) ? manager.GetBuffer<LinkedEntityGroup>(settings.Prefab).Length : 1;
            if ((long)math.max(1, linked) * settings.Count > MaximumEntities)
                throw new InvalidOperationException($"The test exceeds {MaximumEntities:N0} linked entities. Reduce the instance count.");
        }

        private void Clear(Entity owner, int budget)
        {
            for (int i = 0; i < budget; i++)
            {
                var roots = EntityManager.GetBuffer<VoxelStressSpawnedRoot>(owner);
                if (roots.Length == 0) return;
                Entity root = roots[roots.Length - 1].Value;
                roots.RemoveAt(roots.Length - 1);
                if (EntityManager.Exists(root)) EntityManager.DestroyEntity(root);
            }
        }
    }
}
