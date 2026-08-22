using DynamicGI.Contributors;
using DynamicGI.Debugging;
using DynamicGI.Geometry;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DynamicGI.Editor
{
    /// <summary>
    /// Small deterministic smoke test for the Phase 1 GPU path. It validates surface
    /// voxelization, shader-side page lookup/readback, and localized removal.
    /// </summary>
    public static class GeometryFieldPhase1Validation
    {
        [MenuItem("Tools/Dynamic GI/Run Phase 1 GPU Validation")]
        public static void Run()
        {
            GameObject fieldObject = null;
            GameObject cube = null;

            try
            {
                fieldObject = new GameObject("Dynamic GI Phase 1 Validation Field");
                WorldGeometryField field = fieldObject.AddComponent<WorldGeometryField>();
                GeometryFieldDebug fieldDebug = fieldObject.AddComponent<GeometryFieldDebug>();

                cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "Dynamic GI Phase 1 Validation Cube";
                cube.transform.position = new Vector3(0f, 1f, 0f);
                GIGeometryContributor contributor = cube.AddComponent<GIGeometryContributor>();
                contributor.RefreshRendererList();

                field.RebuildAll();
                field.ProcessAllDirtyNow();
                if (field.ActiveBrickCount == 0)
                    throw new System.InvalidOperationException("No geometry bricks were activated by the validation cube.");

                // Exercise debug instance generation and the HDRP indirect draw setup.
                fieldDebug.SendMessage("LateUpdate", SendMessageOptions.DontRequireReceiver);

                // The +X face lies exactly at x=0.5. Query the voxel selected by the
                // field's half-open [min,max) convention on the positive side.
                Vector3 surfacePosition = new(0.51f, 1f, 0f);
                GeometryOccupancyResult occupiedResult = QuerySynchronously(field, surfacePosition);
                if (occupiedResult.HasError || !occupiedResult.IsOccupied)
                    throw new System.InvalidOperationException("The GPU occupancy query did not find the cube surface.");

                Renderer cubeRenderer = cube.GetComponent<Renderer>();
                cubeRenderer.enabled = false;
                contributor.NotifyGeometryChanged();
                field.ProcessAllDirtyNow();

                GeometryOccupancyResult emptyResult = QuerySynchronously(field, surfacePosition);
                if (emptyResult.HasError || emptyResult.IsOccupied)
                    throw new System.InvalidOperationException("Localized invalidation left stale occupancy after disabling the cube.");

                UnityEngine.Debug.Log(
                    $"DYNAMIC_GI_PHASE1_VALIDATION_PASSED | bricks={field.ActiveBrickCount} | " +
                    $"GPU={EditorUtility.FormatBytes(field.Stats.EstimatedGpuBytes)}");
            }
            finally
            {
                if (cube != null)
                    Object.DestroyImmediate(cube);
                if (fieldObject != null)
                    Object.DestroyImmediate(fieldObject);
            }
        }

        private static GeometryOccupancyResult QuerySynchronously(WorldGeometryField field, Vector3 position)
        {
            GeometryOccupancyResult result = default;
            bool completed = false;
            bool accepted = field.RequestOccupancy(position, value =>
            {
                result = value;
                completed = true;
            });

            if (!accepted)
                throw new System.InvalidOperationException("The geometry field rejected the GPU occupancy query.");

            AsyncGPUReadback.WaitAllRequests();
            if (!completed)
                throw new System.InvalidOperationException("GPU occupancy readback did not complete.");

            return result;
        }
    }
}
