using UnityEngine;

namespace LocalModels.VoxelBridge
{
    public enum VoxelConversionAction { Voxelize, Ignore, KeepOriginal }

    [DisallowMultipleComponent, AddComponentMenu("Voxel Bridge/Conversion Rule")]
    public sealed class VoxelConversionRule : MonoBehaviour
    {
        [SerializeField] private VoxelConversionAction action;
        [Tooltip("Aplica esta regla a los renderers descendientes. Una regla más cercana tiene prioridad.")]
        [SerializeField] private bool applyToChildren = true;
        [Tooltip("SurfaceID explícito. -1 utiliza las reglas del material y la detección de emisión del perfil.")]
        [SerializeField, Range(-1, 255)] private int surfaceId = -1;

        public VoxelConversionAction Action => action;
        public bool ApplyToChildren => applyToChildren;
        public int SurfaceId => surfaceId;
    }
}
