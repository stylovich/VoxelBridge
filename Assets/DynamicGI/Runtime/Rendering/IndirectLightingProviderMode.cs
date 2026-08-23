namespace DynamicGI.Rendering
{
    /// <summary>
    /// Selects how a material combines the indirect lighting it already owns with
    /// the runtime Dynamic GI field. The HDRP screen-space bridge can only add the
    /// dynamic term; replacing HDRP/APV requires the material HLSL API.
    /// </summary>
    public enum IndirectLightingProviderMode
    {
        ExistingPlusDynamic = 0,
        DynamicOnly = 1,
        ExistingOnly = 2,
        Disabled = 3,
    }
}
