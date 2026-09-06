using UnityEngine;

namespace LocalModels.VoxelBridge.Diagnostics
{
    [AddComponentMenu("Voxel Bridge/DOTS Stress Spawner")]
    [DisallowMultipleComponent]
    public sealed class VoxelStressSpawnerAuthoring : MonoBehaviour
    {
        [Tooltip("Prefab de producción de Voxel Bridge. Colocar este componente en una subescena para hornear la referencia. No utilizar el import directo del .vox.")]
        [SerializeField] private GameObject prefab;
        [Tooltip("Cantidad total de instancias. Comenzar con 100 y aumentar gradualmente después de comprobar materiales y memoria.")]
        [SerializeField, Range(1, VoxelStressSpawnSystem.MaximumInstances)] private int count = 100;
        [SerializeField, Min(1)] private int columns = 10;
        [Tooltip("Separación entre pivotes en metros. Elegir un valor mayor que las dimensiones del modelo para evitar solapamientos.")]
        [SerializeField, Min(.001f)] private float spacing = 10;
        [Tooltip("Máximo de raíces creadas o eliminadas por frame. Cada raíz puede contener muchos chunks y LODs. Reducir si aparecen pausas.")]
        [SerializeField, Range(1, 256)] private int instancesPerFrame = 16;

        public GameObject Prefab => prefab;
        public int Count => count;
        public int Columns => columns;
        public float Spacing => spacing;
        public int InstancesPerFrame => instancesPerFrame;
    }
}
