using System;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    public enum VoxelImpostorType
    {
        Octahedron = 1,
        HemiOctahedron = 2,
        Spherical = 0
    }

    public enum VoxelImpostorQuality
    {
        Unspecified = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Architecture = 4
    }

    [Serializable]
    public sealed class VoxelImpostorSettings
    {
        [Header("Captura y atlas")]
        [Tooltip(
            "Define cómo se distribuyen las vistas capturadas alrededor del modelo.\n\n" +
            "Octahedron: recomendado para la mayoría de objetos y cámaras libres; cubre todo el objeto de manera uniforme.\n" +
            "HemiOctahedron: recomendado para edificios, vehículos y props que nunca se observan desde abajo; concentra las vistas en el hemisferio superior.\n" +
            "Spherical: opción especializada para objetos que pueden verse desde cualquier ángulo y muestran artefactos con la proyección octaédrica. No equivale automáticamente a mayor calidad.")]
        [InspectorName("Tipo de proyección")]
        [SerializeField] private VoxelImpostorType impostorType = VoxelImpostorType.Octahedron;
        [Tooltip(
            "Resolución cuadrada de cada mapa del atlas generado por Amplify Impostors. Amplify suele producir varios mapas, por lo que duplicar la resolución multiplica aproximadamente por cuatro la cantidad de píxeles, la memoria y el tiempo de horneado.\n\n" +
            "512: props pequeños o impostores que aparecen muy lejos.\n" +
            "1024: calidad equilibrada y valor recomendado para la mayoría de vehículos y props.\n" +
            "2048: edificios, objetos grandes o siluetas importantes.\n" +
            "4096: solo casos excepcionales; valida memoria y streaming antes de usarlo en producción.")]
        [InspectorName("Resolución del atlas")]
        [SerializeField] private int textureResolution = 1024;
        [Tooltip(
            "Número de vistas capturadas por eje. Con los ejes acoplados, 8 genera 64 vistas, 12 genera 144 y 16 genera 256. Más vistas reducen el cambio visible al rotar la cámara, pero aumentan el tiempo de horneado y dejan menos píxeles para cada vista si no aumentas también el atlas.\n\n" +
            "8: rendimiento y objetos muy lejanos.\n" +
            "12: equilibrado; recomendado junto con atlas 1024.\n" +
            "16: alta calidad; recomendado junto con atlas 2048.\n" +
            "24-32: uso excepcional; normalmente tiene poco beneficio frente al coste y la pérdida de resolución por vista.")]
        [InspectorName("Vistas por eje")]
        [SerializeField, Range(1, 32)] private int frames = 12;
        [Tooltip(
            "Expande los píxeles del borde de cada captura para evitar líneas, halos y filtrado de vistas vecinas cuando se usan mipmaps. Aumentarlo hace el horneado más lento; un valor demasiado bajo puede producir costuras a distancia.\n\n" +
            "8-16 px: atlas 512 o perfil de rendimiento.\n" +
            "16-32 px: atlas 1024, perfil equilibrado.\n" +
            "32-48 px: atlas 2048, perfil alto.")]
        [InspectorName("Padding entre vistas (px)")]
        [SerializeField, Range(0, 64)] private int pixelPadding = 32;

        [Header("Silueta del billboard")]
        [Tooltip(
            "Límite de vértices del mesh plano que rodea el atlas. Más vértices permiten seguir mejor una silueta irregular y reducir píxeles transparentes/overdraw; menos vértices simplifican el mesh.\n\n" +
            "4-6: cajas, edificios rectangulares o perfil de rendimiento.\n" +
            "8: equilibrado y valor recomendado general.\n" +
            "12: alta calidad para vehículos, árboles o contornos irregulares.\n" +
            "16: solo siluetas especialmente complejas.")]
        [InspectorName("Vértices máximos")]
        [SerializeField, Range(4, 16)] private int maxVertices = 8;
        [Tooltip(
            "Controla cuánto detalle intenta conservar Amplify al detectar el contorno antes de limitarlo con Vértices máximos. Dentro del rango de Amplify, valores mayores siguen la silueta con mayor precisión; el límite final sigue siendo Vértices máximos.\n\n" +
            "0.05-0.10: contorno simple, apropiado para rendimiento o formas regulares.\n" +
            "0.15: equilibrado y valor predeterminado de Amplify.\n" +
            "0.20: máxima fidelidad del detector para perfiles altos; combínalo con 12-16 vértices.")]
        [InspectorName("Detalle del contorno")]
        [SerializeField, Range(0f, 0.2f)] private float silhouetteTolerance = 0.15f;
        [Tooltip(
            "Desplaza hacia fuera los vértices de la silueta siguiendo sus normales. Sirve como margen de seguridad para que el borde capturado no quede recortado por el mesh. No modifica el normal map del material.\n\n" +
            "0.005: borde muy ajustado para alta calidad; comprueba que no recorte partes finas.\n" +
            "0.01: equilibrado y recomendado para empezar.\n" +
            "0.02-0.05: margen más robusto para contornos problemáticos, a cambio de más área transparente y posible halo.")]
        [InspectorName("Expansión de la silueta")]
        [SerializeField, Range(0f, 1f)] private float normalScale = 0.01f;

        [Header("Transición y descarte")]
        [Tooltip(
            "Altura relativa que ocupa el objeto en pantalla cuando Unity deja de dibujar incluso el impostor. Debe ser menor que la transición del último LOD voxel. Un valor mayor descarta antes y mejora rendimiento; uno menor mantiene el objeto visible a más distancia.\n\n" +
            "0.01: descarte agresivo para props pequeños o perfil de rendimiento.\n" +
            "0.005: equilibrado y valor recomendado general.\n" +
            "0.001-0.002: edificios, hitos y perfil de alta distancia.")]
        [InspectorName("Altura de descarte")]
        [SerializeField, Range(0.0001f, 0.1f)] private float cullScreenHeight = 0.005f;

        public VoxelImpostorType ImpostorType => impostorType;
        public int TextureResolution => textureResolution;
        public int Frames => frames;
        public int PixelPadding => pixelPadding;
        public int MaxVertices => maxVertices;
        public float SilhouetteTolerance => silhouetteTolerance;
        public float NormalScale => normalScale;
        public float CullScreenHeight => cullScreenHeight;

        internal static VoxelImpostorSettings CreateLow() => new()
        {
            impostorType = VoxelImpostorType.HemiOctahedron,
            textureResolution = 512,
            frames = 8,
            pixelPadding = 12,
            maxVertices = 6,
            silhouetteTolerance = 0.075f,
            normalScale = 0.02f,
            cullScreenHeight = 0.01f
        };

        internal static VoxelImpostorSettings CreateMedium() => new()
        {
            impostorType = VoxelImpostorType.Octahedron,
            textureResolution = 1024,
            frames = 12,
            pixelPadding = 32,
            maxVertices = 8,
            silhouetteTolerance = 0.15f,
            normalScale = 0.01f,
            cullScreenHeight = 0.005f
        };

        internal static VoxelImpostorSettings CreateHigh() => new()
        {
            impostorType = VoxelImpostorType.Octahedron,
            textureResolution = 2048,
            frames = 16,
            pixelPadding = 48,
            maxVertices = 12,
            silhouetteTolerance = 0.2f,
            normalScale = 0.005f,
            cullScreenHeight = 0.0015f
        };

        internal static VoxelImpostorSettings CreateArchitecture() => new()
        {
            impostorType = VoxelImpostorType.HemiOctahedron,
            textureResolution = 2048,
            frames = 16,
            pixelPadding = 48,
            maxVertices = 10,
            silhouetteTolerance = 0.15f,
            normalScale = 0.01f,
            cullScreenHeight = 0.0005f
        };

        public bool TryValidate(float lastVoxelTransitionHeight, out string error)
        {
            if (textureResolution < 256 || textureResolution > 4096 ||
                (textureResolution & (textureResolution - 1)) != 0)
            {
                error = "La resolución del atlas debe ser una potencia de dos entre 256 y 4096.";
                return false;
            }
            if (frames < 1 || frames > 32)
            {
                error = "Las vistas por eje deben estar entre 1 y 32.";
                return false;
            }
            if (pixelPadding < 0 || pixelPadding > 64 || maxVertices < 4 || maxVertices > 16)
            {
                error = "El padding o el número máximo de vértices está fuera del rango admitido por Amplify.";
                return false;
            }
            if (float.IsNaN(cullScreenHeight) || float.IsInfinity(cullScreenHeight) ||
                cullScreenHeight <= 0f || cullScreenHeight >= lastVoxelTransitionHeight)
            {
                error = $"El descarte del impostor debe ser mayor que cero y menor que la transición del último LOD voxel ({lastVoxelTransitionHeight:0.####}).";
                return false;
            }
            if (silhouetteTolerance < 0f || silhouetteTolerance > 0.2f ||
                normalScale < 0f || normalScale > 1f)
            {
                error = "La tolerancia o la escala de normales está fuera de rango.";
                return false;
            }

            error = null;
            return true;
        }
    }

    [CreateAssetMenu(fileName = "VoxelImpostorProfiles",
        menuName = "Voxel Bridge/Configuración de perfiles de impostor")]
    public sealed class VoxelImpostorProfile : ScriptableObject
    {
        private const int CurrentDataVersion = 4;
        private const float DefaultMinimumImpostorSize = 0.5f;
        private const float DefaultMediumImpostorSize = 4f;
        private const float DefaultArchitectureImpostorSize = 24f;

        [Header("Selección automática por tamaño")]
        [Tooltip(
            "Tamaño máximo del modelo, en metros, por debajo del cual no se genera un impostor. " +
            "Los objetos pequeños suelen descartarse antes de que un impostor aporte una mejora visible. " +
            "El valor recomendado para props pequeños es 0,5 m.")]
        [InspectorName("Tamaño mínimo para impostor (m)")]
        [SerializeField, Min(0f)] private float minimumImpostorSize =
            DefaultMinimumImpostorSize;
        [Tooltip(
            "A partir de este tamaño se selecciona el perfil Medio. Los modelos entre el tamaño mínimo " +
            "y este umbral utilizan el perfil Bajo. Un valor de 4 m cubre props grandes y vehículos compactos.")]
        [InspectorName("Inicio del perfil Medio (m)")]
        [SerializeField, Min(0.01f)] private float mediumImpostorSize =
            DefaultMediumImpostorSize;
        [Tooltip(
            "A partir de este tamaño se selecciona el perfil Arquitectura. Los modelos entre el umbral " +
            "Medio y este valor utilizan el perfil Medio. El perfil Alto permanece como selección manual " +
            "para objetos que pueden observarse desde cualquier dirección.")]
        [InspectorName("Inicio de Arquitectura (m)")]
        [SerializeField, Min(0.01f)] private float architectureImpostorSize =
            DefaultArchitectureImpostorSize;

        [SerializeField, HideInInspector] private int dataVersion;
        [Tooltip(
            "Preset para props pequeños o muy lejanos. Prioriza memoria y tiempo de horneado. " +
            "HemiOctahedron presupone que el modelo no se observará desde abajo.")]
        [InspectorName("Bajo · Rendimiento")]
        [SerializeField] private VoxelImpostorSettings low = VoxelImpostorSettings.CreateLow();
        [Tooltip(
            "Preset recomendado para la mayoría de vehículos, props y objetos de tamaño medio. " +
            "Equilibra estabilidad angular, memoria de atlas y distancia de descarte.")]
        [InspectorName("Medio · Equilibrado")]
        [SerializeField] private VoxelImpostorSettings medium = VoxelImpostorSettings.CreateMedium();
        [Tooltip(
            "Preset para vehículos voladores, objetos móviles importantes y modelos que pueden observarse " +
            "desde cualquier dirección. Aumenta la memoria y el tiempo de horneado.")]
        [InspectorName("Alto · Gran distancia")]
        [SerializeField] private VoxelImpostorSettings high = VoxelImpostorSettings.CreateHigh();
        [Tooltip(
            "Preset especializado para edificios, estructuras y elementos grandes de fondo que se observan " +
            "principalmente desde el suelo o desde arriba. HemiOctahedron concentra las capturas útiles y " +
            "el descarte tardío mantiene hitos y siluetas del skyline a mucha distancia.")]
        [InspectorName("Arquitectura · Fondo")]
        [SerializeField] private VoxelImpostorSettings architecture =
            VoxelImpostorSettings.CreateArchitecture();

        public VoxelImpostorSettings GetSettings(VoxelImpostorQuality quality)
        {
            EnsureInitialized();
            return NormalizeQuality(quality) switch
            {
                VoxelImpostorQuality.Low => low,
                VoxelImpostorQuality.High => high,
                VoxelImpostorQuality.Architecture => architecture,
                _ => medium
            };
        }

        public bool TryValidate(
            VoxelImpostorQuality quality, float lastVoxelTransitionHeight, out string error) =>
            GetSettings(quality).TryValidate(lastVoxelTransitionHeight, out error);

        public float MinimumImpostorSize
        {
            get
            {
                EnsureInitialized();
                return minimumImpostorSize;
            }
        }

        public float MediumImpostorSize
        {
            get
            {
                EnsureInitialized();
                return mediumImpostorSize;
            }
        }

        public float ArchitectureImpostorSize
        {
            get
            {
                EnsureInitialized();
                return architectureImpostorSize;
            }
        }

        public bool TrySelectAutomaticQuality(
            float modelSize, out VoxelImpostorQuality quality)
        {
            EnsureInitialized();
            quality = VoxelImpostorQuality.Unspecified;
            if (!float.IsFinite(modelSize) || modelSize <= 0f ||
                !TryValidateAutomaticPolicy(out _))
                return false;
            if (modelSize < minimumImpostorSize) return false;

            quality = modelSize >= architectureImpostorSize
                ? VoxelImpostorQuality.Architecture
                : modelSize >= mediumImpostorSize
                    ? VoxelImpostorQuality.Medium
                    : VoxelImpostorQuality.Low;
            return true;
        }

        public bool TryValidateAutomaticPolicy(out string error)
        {
            EnsureInitialized();
            if (!float.IsFinite(minimumImpostorSize) ||
                !float.IsFinite(mediumImpostorSize) ||
                !float.IsFinite(architectureImpostorSize) ||
                minimumImpostorSize < 0f ||
                mediumImpostorSize <= minimumImpostorSize ||
                architectureImpostorSize <= mediumImpostorSize)
            {
                error = "Los umbrales automáticos deben ser finitos y crecer en el orden mínimo, Medio y Arquitectura.";
                return false;
            }

            error = null;
            return true;
        }

        internal bool EnsureInitialized()
        {
            bool changed = false;
            if (dataVersion < 4)
            {
                minimumImpostorSize = DefaultMinimumImpostorSize;
                mediumImpostorSize = DefaultMediumImpostorSize;
                architectureImpostorSize = DefaultArchitectureImpostorSize;
                changed = true;
            }
            if (dataVersion != CurrentDataVersion)
            {
                dataVersion = CurrentDataVersion;
                changed = true;
            }
            if (low == null)
            {
                low = VoxelImpostorSettings.CreateLow();
                changed = true;
            }
            if (medium == null)
            {
                medium = VoxelImpostorSettings.CreateMedium();
                changed = true;
            }
            if (high == null)
            {
                high = VoxelImpostorSettings.CreateHigh();
                changed = true;
            }
            if (architecture == null)
            {
                architecture = VoxelImpostorSettings.CreateArchitecture();
                changed = true;
            }
            return changed;
        }

        internal void ResetRecommendedProfiles()
        {
            dataVersion = CurrentDataVersion;
            minimumImpostorSize = DefaultMinimumImpostorSize;
            mediumImpostorSize = DefaultMediumImpostorSize;
            architectureImpostorSize = DefaultArchitectureImpostorSize;
            low = VoxelImpostorSettings.CreateLow();
            medium = VoxelImpostorSettings.CreateMedium();
            high = VoxelImpostorSettings.CreateHigh();
            architecture = VoxelImpostorSettings.CreateArchitecture();
        }

        internal static VoxelImpostorQuality NormalizeQuality(VoxelImpostorQuality quality) =>
            quality is VoxelImpostorQuality.Low or VoxelImpostorQuality.High or
                VoxelImpostorQuality.Architecture
                ? quality
                : VoxelImpostorQuality.Medium;

        internal static string GetQualityName(VoxelImpostorQuality quality) =>
            NormalizeQuality(quality) switch
            {
                VoxelImpostorQuality.Low => "Bajo · Rendimiento",
                VoxelImpostorQuality.High => "Alto · Gran distancia",
                VoxelImpostorQuality.Architecture => "Arquitectura · Fondo",
                _ => "Medio · Equilibrado"
            };

        internal static string GetQualityDescription(VoxelImpostorQuality quality) =>
            NormalizeQuality(quality) switch
            {
                VoxelImpostorQuality.Low =>
                    "Para props pequeños o muy lejanos. Reduce atlas, vistas y coste de transición. " +
                    "Usa HemiOctahedron: elige Medio si la cámara puede mirar el objeto desde abajo.",
                VoxelImpostorQuality.High =>
                    "Para vehículos voladores, objetos móviles importantes y modelos vistos desde cualquier " +
                    "dirección. Consume más memoria de atlas y tarda más en hornearse.",
                VoxelImpostorQuality.Architecture =>
                    "Para edificios y estructuras grandes de fondo que no se observan desde abajo. " +
                    "Concentra las capturas en el hemisferio superior y conserva la silueta hasta el skyline.",
                _ =>
                    "Recomendado para la mayoría de vehículos y props. Mantiene una buena estabilidad " +
                    "al rotar la cámara sin el coste del perfil Alto."
            };
    }
}
