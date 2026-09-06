using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalModels.VoxelBridge.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal sealed class VoxelStressSpawnerBaker : Baker<VoxelStressSpawnerAuthoring>
    {
        public override void Bake(VoxelStressSpawnerAuthoring authoring)
        {
            string path = AssetDatabase.GetAssetPath(authoring.Prefab);
            try
            {
                if (!VoxelProductionLink.HasLink(path)) throw new InvalidDataException("Assign a Voxel Bridge production prefab, not a VOX preview or a scene instance.");
                VoxelProductionLink.Load(path);
                for (Transform transform = authoring.transform; transform != null; transform = transform.parent) DependsOn(transform);
                var settings = new VoxelStressSpawner { Count = authoring.Count, Columns = authoring.Columns,
                    Spacing = authoring.Spacing, InstancesPerFrame = authoring.InstancesPerFrame,
                    Origin = authoring.transform.position, Rotation = authoring.transform.rotation };
                VoxelStressSpawnSystem.Validate(settings);
                settings.Prefab = GetEntity(authoring.Prefab, TransformUsageFlags.Dynamic);
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, settings);
            }
            catch (Exception ex) { Debug.LogError("Voxel stress spawner baking failed: " + ex.Message, authoring); }
        }
    }

    [CustomEditor(typeof(VoxelStressSpawnerAuthoring))]
    internal sealed class VoxelStressSpawnerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.HelpBox("Colocar este componente en una subescena y asignar un prefab de producción. No genera objetos automáticamente. Entrar en Play Mode y abrir DOTS Stress Test para iniciar, pausar o limpiar la prueba. La posición y rotación del componente definen el centro y orientación de la distribución; su escala no se aplica.", MessageType.Info);
            if (GUILayout.Button("Open DOTS Stress Test")) VoxelStressTestWindow.Open();
        }
    }

    internal sealed class VoxelStressSnapshot
    {
        internal bool HasCamera;
        internal int Roots, RenderEntities, Disabled, MissingBounds, Inside, Outside, MissingResources;
        internal readonly Dictionary<Material, int> Materials = new Dictionary<Material, int>();
        internal readonly HashSet<Mesh> Meshes = new HashSet<Mesh>();

        internal static VoxelStressSnapshot Capture(World world, Entity spawner, Camera camera)
        {
            var result = new VoxelStressSnapshot { HasCamera = camera != null };
            var manager = world.EntityManager;
            manager.CompleteAllTrackedJobs();
            var graphics = world.GetExistingSystemManaged<EntitiesGraphicsSystem>();
            var planes = camera != null ? GeometryUtility.CalculateFrustumPlanes(camera) : null;
            var seen = new HashSet<Entity>();
            var roots = manager.GetBuffer<VoxelStressSpawnedRoot>(spawner, true);
            for (int i = 0; i < roots.Length; i++)
            {
                var root = roots[i].Value;
                if (!manager.Exists(root)) continue;
                result.Roots++;
                if (manager.HasBuffer<LinkedEntityGroup>(root))
                {
                    var linked = manager.GetBuffer<LinkedEntityGroup>(root, true);
                    foreach (var item in linked) Inspect(item.Value);
                }
                else Inspect(root);
            }
            return result;

            void Inspect(Entity entity)
            {
                if (!seen.Add(entity) || !manager.Exists(entity) || !manager.HasComponent<MaterialMeshInfo>(entity)) return;
                result.RenderEntities++;
                var info = manager.GetComponentData<MaterialMeshInfo>(entity);
                if (manager.HasComponent<RenderMeshArray>(entity))
                {
                    var array = manager.GetSharedComponentManaged<RenderMeshArray>(entity);
                    if (info.HasMaterialMeshIndexRange)
                    {
                        var range = info.MaterialMeshIndexRange;
                        for (int i = range.start; i < range.end; i++)
                        {
                            var entry = array.MaterialMeshIndices[i];
                            Material material = array.MaterialReferences[entry.MaterialIndex];
                            Mesh mesh = array.MeshReferences[entry.MeshIndex];
                            Add(material, mesh);
                        }
                    }
                    else Add(info.Material >= 0 ? graphics?.GetMaterial(info.MaterialID) : array.GetMaterial(info),
                        info.Mesh >= 0 ? graphics?.GetMesh(info.MeshID) : array.GetMesh(info));
                }
                else Add(info.Material >= 0 ? graphics?.GetMaterial(info.MaterialID) : null,
                    info.Mesh >= 0 ? graphics?.GetMesh(info.MeshID) : null);

                if (manager.HasComponent<Disabled>(entity) || manager.HasComponent<DisableRendering>(entity) || !manager.IsComponentEnabled<MaterialMeshInfo>(entity))
                { result.Disabled++; return; }
                if (!manager.HasComponent<WorldRenderBounds>(entity)) { result.MissingBounds++; return; }
                if (planes == null) return;
                var aabb = manager.GetComponentData<WorldRenderBounds>(entity).Value;
                if (GeometryUtility.TestPlanesAABB(planes, new Bounds(aabb.Center, aabb.Extents * 2))) result.Inside++;
                else result.Outside++;
            }

            void Add(Material material, Mesh mesh)
            {
                if (material == null || mesh == null) result.MissingResources++;
                if (material != null) { result.Materials.TryGetValue(material, out int count); result.Materials[material] = count + 1; }
                if (mesh != null) result.Meshes.Add(mesh);
            }
        }
    }

    internal sealed class VoxelStressTestWindow : EditorWindow
    {
        [SerializeField] private Camera targetCamera;
        private Entity selected;
        private World lastWorld;
        private Vector2 scroll;
        private VoxelStressSnapshot snapshot;
        private string snapshotHeader, error;
        private double nextRepaint;

        [MenuItem("Tools/Voxel Bridge/DOTS Stress Test", false, 104)]
        internal static void Open() => GetWindow<VoxelStressTestWindow>("DOTS Stress Test").Show();

        private void OnInspectorUpdate()
        {
            if (EditorApplication.timeSinceStartup < nextRepaint) return;
            nextRepaint = EditorApplication.timeSinceStartup + .5;
            Repaint();
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.HelpBox("Prueba manual de entidades. Añadir DOTS Stress Spawner a un GameObject de una subescena, asignar un prefab de producción y entrar en Play Mode. Empezar con 100 instancias y aumentar gradualmente; los límites no garantizan un consumo concreto de RAM o VRAM.", MessageType.Info);
            var world = World.DefaultGameObjectInjectionWorld;
            if (!EditorApplication.isPlaying || world == null || !world.IsCreated)
            {
                EditorGUILayout.LabelField("Enter Play Mode to access the runtime world.");
                EditorGUILayout.EndScrollView(); return;
            }
            if (world != lastWorld) { lastWorld = world; selected = Entity.Null; snapshot = null; }
            try
            {
                var manager = world.EntityManager;
                using var query = manager.CreateEntityQuery(typeof(VoxelStressSpawner));
                using var entities = query.ToEntityArray(Allocator.Temp);
                if (entities.Length == 0) EditorGUILayout.HelpBox("No hay spawners horneados en este mundo. Comprobar que la subescena está cargada y revisar posibles errores de baking.", MessageType.Info);
                else
                {
                    int index = 0;
                    for (int i = 0; i < entities.Length; i++) if (entities[i] == selected) index = i;
                    var labels = entities.Select(e => $"{manager.GetName(e)} [{e.Index}:{e.Version}]").ToArray();
                    int chosen = EditorGUILayout.Popup("Spawner", index, labels);
                    if (selected != entities[chosen]) { selected = entities[chosen]; snapshot = null; error = null; }
                    if (!manager.HasBuffer<VoxelStressSpawnedRoot>(selected))
                        EditorGUILayout.HelpBox("El spawner está horneado y espera su inicialización en runtime. Reanudar Play Mode si está pausado.", MessageType.Info);
                    else
                    {
                        var settings = manager.GetComponentData<VoxelStressSpawner>(selected);
                        int count = manager.GetBuffer<VoxelStressSpawnedRoot>(selected).Length;
                        EditorGUILayout.LabelField("World", world.Name);
                        EditorGUILayout.LabelField("State", settings.Mode.ToString());
                        EditorGUILayout.LabelField("Instances", $"{count:N0} / {settings.Count:N0}");
                        using (new EditorGUI.DisabledScope(count != 0 || settings.Mode == VoxelStressMode.Spawning || settings.Mode == VoxelStressMode.Clearing))
                        {
                            EditorGUI.BeginChangeCheck();
                            settings.Count = Mathf.Clamp(EditorGUILayout.IntField("Runtime Target Count", settings.Count), 1, VoxelStressSpawnSystem.MaximumInstances);
                            settings.Columns = Mathf.Clamp(EditorGUILayout.IntField("Runtime Columns", settings.Columns), 1, VoxelStressSpawnSystem.MaximumInstances);
                            settings.Spacing = Mathf.Max(.001f, EditorGUILayout.FloatField("Runtime Spacing", settings.Spacing));
                            settings.InstancesPerFrame = Mathf.Clamp(EditorGUILayout.IntField("Instances per Frame", settings.InstancesPerFrame), 1, 256);
                            if (EditorGUI.EndChangeCheck()) { VoxelStressSpawnSystem.Validate(settings); manager.SetComponentData(selected, settings); }
                        }
                        EditorGUILayout.HelpBox("Los ajustes de esta ventana afectan sólo a la sesión de Play Mode. Limpiar las instancias antes de cambiar la distribución.", MessageType.None);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            using (new EditorGUI.DisabledScope(settings.Mode == VoxelStressMode.Spawning || settings.Mode == VoxelStressMode.Clearing || count >= settings.Count))
                                if (GUILayout.Button("Spawn / Resume"))
                                {
                                    VoxelStressSpawnSystem.Validate(settings); VoxelStressSpawnSystem.ValidatePrefab(manager, settings);
                                    if (settings.Count <= 1000 || EditorUtility.DisplayDialog("Large Voxel Stress Test", $"La prueba creará hasta {settings.Count:N0} instancias completas, con todos sus chunks y LODs. Comprobar el margen de memoria antes de continuar.", "Spawn", "Cancel"))
                                    { settings.Mode = VoxelStressMode.Spawning; manager.SetComponentData(selected, settings); error = null; }
                                }
                            if (GUILayout.Button("Pause")) { settings.Mode = VoxelStressMode.Idle; manager.SetComponentData(selected, settings); }
                            if (GUILayout.Button("Clear Spawned")) { settings.Mode = VoxelStressMode.Clearing; manager.SetComponentData(selected, settings); snapshot = null; error = null; }
                        }
                        targetCamera = (Camera)EditorGUILayout.ObjectField("Frustum Camera", targetCamera, typeof(Camera), true);
                        if (GUILayout.Button("Use Main Camera")) targetCamera = Camera.main;
                        if (GUILayout.Button("Capture Resource and Frustum Snapshot"))
                        {
                            snapshot = VoxelStressSnapshot.Capture(world, selected, targetCamera);
                            snapshotHeader = $"Frame {Time.frameCount} | Camera: {(targetCamera == null ? "None" : targetCamera.name)}";
                            error = null;
                        }
                        DrawSnapshot();
                    }
                }
                DrawGraphicsStats(world);
            }
            catch (Exception ex) { error = ex.Message; }
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            EditorGUILayout.EndScrollView();
        }

        private void DrawSnapshot()
        {
            if (snapshot == null) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(snapshotHeader, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Live Roots / Render Entities", $"{snapshot.Roots:N0} / {snapshot.RenderEntities:N0}");
            EditorGUILayout.LabelField("Unique Materials / Meshes", $"{snapshot.Materials.Count:N0} / {snapshot.Meshes.Count:N0}");
            EditorGUILayout.LabelField("Inside / Outside Frustum", snapshot.HasCamera ? $"{snapshot.Inside:N0} / {snapshot.Outside:N0}" : "No camera assigned");
            EditorGUILayout.LabelField("Disabled / Missing Bounds", $"{snapshot.Disabled:N0} / {snapshot.MissingBounds:N0}");
            EditorGUILayout.LabelField("Unresolved Resource Bindings", snapshot.MissingResources.ToString());
            foreach (var pair in snapshot.Materials.OrderBy(p => p.Key.name))
            {
                using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField($"{pair.Value:N0} references", pair.Key, typeof(Material), false);
                EditorGUILayout.LabelField(pair.Key.shader.name, EditorStyles.miniLabel);
            }
            EditorGUILayout.HelpBox("Los recursos se agrupan por identidad del asset, no por nombre. Su cantidad única debería permanecer estable al aumentar instancias del mismo prefab. El frustum es una estimación por bounds de TODOS los LODs, sin aplicar selección de LOD ni oclusión; no mide draws efectivos. Capturar después de pausar el spawn y dejar completar un frame de transforms.", MessageType.Info);
        }

        private static void DrawGraphicsStats(World world)
        {
            var graphics = world.GetExistingSystemManaged<EntitiesGraphicsSystem>();
            if (graphics == null) { EditorGUILayout.LabelField("Entities Graphics counters unavailable."); return; }
            var stats = graphics.Stats;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Entities Graphics — All Views", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Rendered Instances / Draw Commands", $"{stats.RenderedInstanceCount:N0} / {stats.DrawCommandCount:N0}");
            EditorGUILayout.LabelField("Batches / Instance Tests", $"{stats.BatchCount:N0} / {stats.InstanceTests:N0}");
            EditorGUILayout.LabelField("ECS Chunks / Chunks with LOD", $"{stats.ChunkTotal:N0} / {stats.ChunkCountAnyLod:N0}");
            EditorGUILayout.LabelField("Instance Data GPU Memory", EditorUtility.FormatBytes(stats.BytesGPUMemoryUsed));
            EditorGUILayout.HelpBox("Contadores de Entities Graphics acumulados para todas las vistas y callbacks, no sólo este spawner o la cámara elegida. Scene View y sombras pueden mantener draws aunque un objeto esté fuera de Game View. La memoria indicada no incluye toda la VRAM de texturas y meshes. Confirmar el descarte con Profiler o Frame Debugger y condiciones de cámara equivalentes.", MessageType.Info);
        }
    }
}
