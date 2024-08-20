using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Experimental.Rendering;
using System;

[Serializable, VolumeComponentMenu("Post-processing/Custom/SSRT")]
public sealed class SSRT_HDRP : CustomPostProcessVolumeComponent, IPostProcessComponent
{
    public enum DebugMode { AO = RenderPass.DebugModeAO, GI = RenderPass.DebugModeGI, None = RenderPass.DebugModeCombined }
    public enum RenderPass { SSRT = 0, Upsample = 1, SampleReuse = 2, TemporalReproj = 3, DebugModeAO = 4, DebugModeBentNormal = 5, DebugModeGI = 6,
        DebugModeCombined = 7, GetDepth = 8, GetLightmask = 9, GetNormals = 10 }
    public enum Fallback { None, AdaptiveProbeVolume, ReflectionProbe}
    public enum FallbackSampleCount { _2 = 2, _4 = 4, _8 = 8, _16 = 16, _32 = 32}

    [Serializable]
    public sealed class DebugModeParameter : VolumeParameter<DebugMode> { public DebugModeParameter(DebugMode value, bool overrideState = false) : base(value, overrideState) { } }
    [Serializable]
    public sealed class RenderPassParameter : VolumeParameter<RenderPass> { public RenderPassParameter(RenderPass value, bool overrideState = false) : base(value, overrideState) { } }
    [Serializable]
    public sealed class FallbackParameter : VolumeParameter<Fallback> { public FallbackParameter(Fallback value, bool overrideState = false) : base(value, overrideState) { } }
    [Serializable]
    public sealed class FallbackSampleCountParameter : VolumeParameter<FallbackSampleCount> { public FallbackSampleCountParameter(FallbackSampleCount value, bool overrideState = false) : base(value, overrideState) { } }
    
    public BoolParameter enabled = new BoolParameter(false);

    [Header("Sampling")]
    [Tooltip("Number of per-pixel hemisphere slices. This has a big performance cost and should be kept as low as possible.")]
    public ClampedIntParameter rotationCount = new ClampedIntParameter(1, 1, 4);

    [Tooltip("Number of samples taken along one side of a given hemisphere slice. The total number of samples taken per pixel is rotationCount * stepCount * 2. This has a big performance cost and should be kept as low as possible.")]
    public ClampedIntParameter stepCount = new ClampedIntParameter(12, 1, 32);

    [Tooltip("Effective sampling radius in world space. AO and GI can only have influence within that radius.")]
    public ClampedFloatParameter radius = new ClampedFloatParameter(5.0f, 1.0f, 25.0f);

    [Tooltip("Controls samples distribution. Exp Factor is an exponent applied at each step get increasing step size over the distance.")]
    public ClampedFloatParameter expFactor = new ClampedFloatParameter(1.0f, 1.0f, 3.0f);

    [Tooltip("Applies some noise on sample positions to hide the banding artifacts that can occur when there is undersampling.")]
    public BoolParameter jitterSamples = new BoolParameter(true);
    [Tooltip("Makes the sample distance in view space instead of world-space (helps having more detail up close).")]
    public BoolParameter screenSpaceSampling = new BoolParameter(false);
    [Tooltip("Use lower mip maps over the distance to use less GPU bandwidth.")]
    public BoolParameter mipOptimization = new BoolParameter(true);

    [Header("GI")]
    [Tooltip("Intensity of the indirect diffuse light.")]
    public ClampedFloatParameter GIIntensity = new ClampedFloatParameter(10.0f, 0.0f, 100.0f);
    [Tooltip("Intensity of the light for second and subsequent bounces.")]
    public ClampedFloatParameter multiBounceGI = new ClampedFloatParameter(0.0f, 0.0f, 1.0f);
    [Tooltip("Guess what normal should be based on sample position. This avoids reading normals from the G-Buffer for every sample and saves some GPU bandwidth.")]
    public BoolParameter normalApproximation = new BoolParameter(false);
    [Tooltip("How much light backface surfaces emit.")]
    public ClampedFloatParameter backfaceLighting = new ClampedFloatParameter(0.0f, 0.0f, 1.0f);

    [Header("Occlusion")]
    [Tooltip("Power function applied to AO to make it appear darker/lighter.")]
    public ClampedFloatParameter AOIntensity = new ClampedFloatParameter(1.0f, 0.0f, 4.0f);
    [Tooltip("Constant thickness value of objects on the screen in world space. Allows light to pass behind surfaces past that thickness value.")]
    public ClampedFloatParameter thickness = new ClampedFloatParameter(1.0f, 0.01f, 10.0f);
    [Tooltip("Increase thickness linearly over distance (avoid losing detail over the distance)")]
    public BoolParameter linearThickness = new BoolParameter(false);
    [Tooltip("Multi-Bounce analytic approximation from GTAO.")]
    public BoolParameter multiBounceAO = new BoolParameter(false);

    [Header("Off-screen Fallback")]
    [Tooltip("Source for off-screen lighting.")]
    public FallbackParameter fallback = new FallbackParameter(Fallback.None);
    [Tooltip("Power function applied to ambient source to make it appear darker/lighter.")]
    public ClampedFloatParameter fallbackPower = new ClampedFloatParameter(1.0f, 1.0f, 4.0f);
    [Tooltip("Intensity of the ambient light coming from a fallback source.")]
    public ClampedFloatParameter fallbackIntensity = new ClampedFloatParameter(1.0f, 0.0f, 10.0f);
    [Tooltip("Number of samples per rotation taken in the ambient source. Higer number can give more correct ambient estimation, but is more taxing on performance.")]
    public FallbackSampleCountParameter fallbackSampleCount = new FallbackSampleCountParameter(FallbackSampleCount._4);
    [Tooltip("If enabled, ambient sampling done outside of the influence shape of the reflection probes will use the sky light instead (Used only when using reflection probe fallback).")]
    public BoolParameter reflectSky = new BoolParameter(false);
    
    [Header("Filters")]
    [Tooltip("Enable/Disable temporal reprojection")]
    public BoolParameter temporalAccumulation = new BoolParameter(true);
    [Tooltip("Controls the speed of the accumulation, slower accumulation is more effective at removing noise but can introduce ghosting.")]
    public ClampedFloatParameter temporalResponse = new ClampedFloatParameter(0.35f, 0.0f, 1.0f);
    [Tooltip("Enable/Disable diffuse denoiser.")]
    public BoolParameter denoising = new BoolParameter(true);
    [Tooltip("Controls the radius of the GI denoiser.")]
    public ClampedFloatParameter denoisingRadius = new ClampedFloatParameter(0.5f, 0.0f, 1.0f);

    [Header("Debug")]
    [Tooltip("View of the different SSRT buffers for debug purposes.")]
    public DebugModeParameter debugMode = new DebugModeParameter(DebugMode.None);
    [Tooltip("If enabled only the radiance that affects the surface will be displayed, if unchecked radiance will be multiplied by surface albedo.")]
    public BoolParameter lightOnly = new BoolParameter(false);
    
    Material material;

    Texture2D owenScrambledNoise;

    public ComputeShader ssrtCS;
    public ComputeShader diffuseDenoiserCS = null;
    
    public bool IsActive() => material != null && enabled.value;
    public override CustomPostProcessInjectionPoint injectionPoint => CustomPostProcessInjectionPoint.AfterOpaqueAndSky;
    
    public override void Setup()
    {
        Shader ssrtShader = Shader.Find("Hidden/Shader/SSRT");
        if (ssrtShader != null)
            material = new Material(ssrtShader);

        if(ssrtCS == null)
            ssrtCS = (ComputeShader)Resources.Load("SSRTCS");
        if(diffuseDenoiserCS == null)
            diffuseDenoiserCS = (ComputeShader)Resources.Load("DiffuseDenoiser");

        if(owenScrambledNoise == null)
            owenScrambledNoise = (Texture2D)Resources.Load("OwenScrambledNoise256");

        filterTexture1 = RTHandles.Alloc(Vector2.one, TextureXR.slices, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R16G16B16A16_SFloat, 
            dimension: TextureXR.dimension, useDynamicScale: true, enableRandomWrite: true, name: "FilterTexture1");
        filterTexture2 = RTHandles.Alloc(Vector2.one, TextureXR.slices, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R16G16B16A16_SFloat, 
            dimension: TextureXR.dimension, useDynamicScale: true, enableRandomWrite: true, name: "FilterTexture2");
        previousFrameTexture = RTHandles.Alloc(Vector2.one, TextureXR.slices, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R16G16B16A16_SFloat, 
            dimension: TextureXR.dimension, useDynamicScale: true, enableRandomWrite: true, name: "PreviousFrameTexture");
        previousDepthTexture = RTHandles.Alloc(Vector2.one, TextureXR.slices, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R32_SFloat, 
            dimension: TextureXR.dimension, useDynamicScale: true, enableRandomWrite: true, name: "PreviousDepthTexture");
        lightPyramidTexture = RTHandles.Alloc(Vector2.one, TextureXR.slices, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.B10G11R11_UFloatPack32, 
            dimension: TextureXR.dimension, useDynamicScale: true, enableRandomWrite: true, name: "LighPyramidTexture", useMipMap: true, autoGenerateMips: false);
        depthPyramidTexture = RTHandles.Alloc(Vector2.one, TextureXR.slices, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R32_SFloat, 
            dimension: TextureXR.dimension, useDynamicScale: true, enableRandomWrite: true, name: "DepthPyramidTexture", useMipMap: true, autoGenerateMips: false);
        normalPyramidTexture = RTHandles.Alloc(Vector2.one, TextureXR.slices, filterMode: FilterMode.Point, colorFormat: GraphicsFormat.R16G16_UNorm, 
            dimension: TextureXR.dimension, useDynamicScale: true, enableRandomWrite: true, name: "DepthPyramidTexture", useMipMap: true, autoGenerateMips: false);
    }
    
    public override void Render(CommandBuffer cmd, HDCamera camera, RTHandle source, RTHandle destination)
    {
        if (material == null)
            return;
        material.SetTexture("_InputTexture", source);
        Camera cam = camera.camera;

        // ---------- Generate pyramids pass ----------
        HDUtils.DrawFullScreen(cmd, material, lightPyramidTexture, null, (int)RenderPass.GetLightmask);
        cmd.GenerateMips(lightPyramidTexture);
        HDUtils.DrawFullScreen(cmd, material, depthPyramidTexture, null, (int)RenderPass.GetDepth);
        cmd.GenerateMips(depthPyramidTexture);
        HDUtils.DrawFullScreen(cmd, material, normalPyramidTexture, null, (int)RenderPass.GetNormals);
        cmd.GenerateMips(normalPyramidTexture);
        
        // ---------- Main pass ----------
        
         // Main pass internal parameters
        var worldToCameraMatrix = cam.worldToCameraMatrix;
        cmd.SetComputeMatrixParam(ssrtCS, "_CameraToWorldMatrix", worldToCameraMatrix.inverse);
        var projectionMatrix = GL.GetGPUProjectionMatrix(cam.projectionMatrix, false);
        cmd.SetComputeMatrixParam(ssrtCS, "_InverseProjectionMatrix", projectionMatrix.inverse);
        float projScale;
        var renderResolution = new Vector2(cam.pixelWidth, cam.pixelHeight);
        projScale = (float)renderResolution.y / (Mathf.Tan(cam.fieldOfView * Mathf.Deg2Rad * 0.5f) * 2) * 0.5f;
        cmd.SetComputeFloatParam(ssrtCS, "_HalfProjScale", projScale);
        int frameCount = Time.frameCount;
        float temporalRotation = temporalRotations[frameCount % 6];
        float temporalOffset = spatialOffsets[(frameCount / 6) % 4];
        cmd.SetComputeFloatParam(ssrtCS, "_TemporalDirections", temporalRotation / 360);
        cmd.SetComputeFloatParam(ssrtCS, "_TemporalOffsets", temporalOffset);
        
        // Sampling parameters
        cmd.SetComputeFloatParam(ssrtCS, "_Radius", radius.value);
        cmd.SetComputeFloatParam(ssrtCS, "_ExpFactor", expFactor.value);
        cmd.SetComputeIntParam(ssrtCS, "_RotationCount", rotationCount.value);
        cmd.SetComputeIntParam(ssrtCS, "_StepCount", stepCount.value);
        cmd.SetComputeIntParam(ssrtCS, "_JitterSamples", jitterSamples.value ? 1 : 0);
        cmd.SetComputeIntParam(ssrtCS, "_ScreenSpaceSampling", screenSpaceSampling.value ? 1 : 0);
        cmd.SetComputeIntParam(ssrtCS, "_MipOptimization", mipOptimization.value ? 1 : 0);

        // GI parameters
        cmd.SetComputeFloatParam(ssrtCS, "_GIIntensity", GIIntensity.value);
        material.SetFloat("_MultiBounceGI", multiBounceGI.value);
        cmd.SetKeyword(ssrtCS, new LocalKeyword(ssrtCS, "NORMAL_APPROXIMATION"), normalApproximation.value);
        cmd.SetComputeFloatParam(ssrtCS, "_BackfaceLighting", backfaceLighting.value);

        // Occlusion parameters
        cmd.SetComputeFloatParam(ssrtCS, "_AOIntensity", AOIntensity.value);
        cmd.SetComputeFloatParam(ssrtCS, "_Thickness", thickness.value);
        cmd.SetComputeIntParam(ssrtCS, "_LinearThickness", linearThickness.value ? 1 : 0);

        // Fallback parameters
        cmd.SetKeyword(ssrtCS, new LocalKeyword(ssrtCS, "FALLBACK_REFLECTION_PROBE"), fallback.value == Fallback.ReflectionProbe); 
        cmd.SetKeyword(ssrtCS, new LocalKeyword(ssrtCS, "FALLBACK_APV"), fallback.value == Fallback.AdaptiveProbeVolume);
        cmd.SetKeyword(ssrtCS, new LocalKeyword(ssrtCS, "FALLBACK_NONE"), fallback.value == Fallback.None);
        cmd.SetComputeFloatParam(ssrtCS, "_FallbackPower", fallbackPower.value);
        cmd.SetComputeFloatParam(ssrtCS, "_FallbackIntensity", fallbackIntensity.value);
        cmd.SetComputeIntParam(ssrtCS, "_FallbackSampleCount", (int)fallbackSampleCount.value);
        cmd.SetKeyword(ssrtCS, new LocalKeyword(ssrtCS, "REFLECT_SKY"), reflectSky.value);

        const int ssgiTileSize = 8;
        int numTilesXHR = (cam.pixelWidth + (ssgiTileSize - 1)) / ssgiTileSize;
        int numTilesYHR = (cam.pixelHeight + (ssgiTileSize - 1)) / ssgiTileSize;
        cmd.SetComputeTextureParam(ssrtCS, (int)RenderPass.SSRT, "_LightPyramidTexture", lightPyramidTexture);
        cmd.SetComputeTextureParam(ssrtCS, (int)RenderPass.SSRT, "_DepthPyramidTexture", depthPyramidTexture);
        cmd.SetComputeTextureParam(ssrtCS, (int)RenderPass.SSRT, "_NormalPyramidTexture", normalPyramidTexture);
        cmd.SetComputeTextureParam(ssrtCS, (int)RenderPass.SSRT, "_GIOcclusionTexture", filterTexture1);

        cmd.DispatchCompute(ssrtCS, (int)RenderPass.SSRT, numTilesXHR, numTilesYHR, 1);

        // ---------- Temporal accumulation pass ----------
        cmd.SetGlobalTexture("_FilterTexture1", filterTexture1);
        cmd.SetGlobalTexture("_FilterTexture2", filterTexture2);
        cmd.SetGlobalTexture("_PreviousColor", previousFrameTexture);
        if(temporalAccumulation.value)
        {
            material.SetFloat("_TemporalResponse", temporalResponse.value);
            viewProjectionMatrix = projectionMatrix * worldToCameraMatrix;
            material.SetMatrix("_InverseViewProjectionMatrix", viewProjectionMatrix.inverse);
            material.SetMatrix("_LastFrameInverseViewProjectionMatrix", lastFrameInverseViewProjectionMatrix);
            cmd.SetGlobalTexture("_PreviousDepth", previousDepthTexture);

            HDUtils.DrawFullScreen(cmd, material, filterTexture2, null, (int)RenderPass.TemporalReproj);
            HDUtils.DrawFullScreen(cmd, material, previousDepthTexture, null, (int)RenderPass.GetDepth);
        }
        else
        {
            cmd.CopyTexture(filterTexture1, filterTexture2);
        }

        // ---------- Diffuse denoiser pass ----------
        int sdTileSize = 8;
        int numTilesX = (cam.pixelWidth + (sdTileSize - 1)) / sdTileSize;
        int numTilesY = (cam.pixelHeight + (sdTileSize - 1)) / sdTileSize;
       
        float pixelSpreadTangent = GetPixelSpreadTangent(cam.fieldOfView, cam.pixelWidth, cam.pixelHeight);
        const int pointDistributionKernel = 0;
        const int bilateralFilterKernel = 2;

        if(denoising.value)
        {
            if(pointDistributionBuffer == null)
            {
                pointDistributionBuffer = new ComputeBuffer(16 * 4, 2 * sizeof(float));
                
                cmd.SetComputeTextureParam(diffuseDenoiserCS, pointDistributionKernel, "_OwenScrambledTexture", owenScrambledNoise);
                cmd.SetComputeBufferParam(diffuseDenoiserCS, pointDistributionKernel, "_PointDistributionRW", pointDistributionBuffer);
                cmd.DispatchCompute(diffuseDenoiserCS, pointDistributionKernel, numTilesX, numTilesY, 1);
            }

            cmd.SetComputeBufferParam(diffuseDenoiserCS, bilateralFilterKernel, "_PointDistribution", pointDistributionBuffer);
            cmd.SetComputeFloatParam(diffuseDenoiserCS, "_DenoiserFilterRadius", denoisingRadius.value * 0.5f);
            cmd.SetComputeTextureParam(diffuseDenoiserCS, bilateralFilterKernel, "_DenoiseInputTexture", filterTexture2);
            cmd.SetComputeTextureParam(diffuseDenoiserCS, bilateralFilterKernel, "_DepthTexture", depthPyramidTexture);
            cmd.SetComputeTextureParam(diffuseDenoiserCS, bilateralFilterKernel, "_DenoiseOutputTextureRW", filterTexture1);
            cmd.SetComputeIntParam(diffuseDenoiserCS, "_HalfResolutionFilter", 0);
            cmd.SetComputeFloatParam(diffuseDenoiserCS, "_PixelSpreadAngleTangent", pixelSpreadTangent);
            cmd.SetComputeIntParam(diffuseDenoiserCS, "_JitterFramePeriod", -1);
            cmd.SetKeyword(diffuseDenoiserCS, new LocalKeyword(diffuseDenoiserCS, "FULL_RESOLUTION_INPUT"), true);
            cmd.DispatchCompute(diffuseDenoiserCS, bilateralFilterKernel, numTilesX, numTilesY, 1);
            cmd.CopyTexture(filterTexture1, filterTexture2);
        }

        cmd.CopyTexture(filterTexture2, previousFrameTexture);

        // ---------- Final composite pass ----------
        material.SetInt("_LightOnly", debugMode.value != DebugMode.None && lightOnly.value ? 1 : 0);
        material.SetInt("_MultiBounceAO", multiBounceAO.value ? 1 : 0);
        HDUtils.DrawFullScreen(cmd, material, destination, null, (int)debugMode.value);

        lastFrameViewProjectionMatrix = viewProjectionMatrix;
        lastFrameInverseViewProjectionMatrix = viewProjectionMatrix.inverse;
    }

    public override void Cleanup()
    {
        CoreUtils.Destroy(material);
        RTHandles.Release(filterTexture1);
        RTHandles.Release(filterTexture2);
        RTHandles.Release(previousFrameTexture);
        RTHandles.Release(previousDepthTexture);
        RTHandles.Release(lightPyramidTexture);
        RTHandles.Release(depthPyramidTexture);
        RTHandles.Release(normalPyramidTexture);

        if(pointDistributionBuffer != null)
        {
            pointDistributionBuffer.Release();
            pointDistributionBuffer = null;
        }
    } 

    static internal float GetPixelSpreadTangent(float fov, int width, int height)
    {
        return Mathf.Tan(fov * Mathf.Deg2Rad * 0.5f) * 2.0f / Mathf.Min(width, height);
    }

    Matrix4x4 lastFrameViewProjectionMatrix;
    Matrix4x4 viewProjectionMatrix;
    Matrix4x4 lastFrameInverseViewProjectionMatrix;

    private RTHandle filterTexture1;
    private RTHandle filterTexture2;
    private RTHandle previousFrameTexture;
    private RTHandle previousDepthTexture;
    private RTHandle lightPyramidTexture;
    private RTHandle depthPyramidTexture;
    private RTHandle normalPyramidTexture;

    ComputeBuffer pointDistributionBuffer;

    // From Activision GTAO paper: https://www.activision.com/cdn/research/s2016_pbs_activision_occlusion.pptx
    static readonly float[] temporalRotations = { 60, 300, 180, 240, 120, 0 };
    static readonly float[] spatialOffsets = { 0, 0.5f, 0.25f, 0.75f };
}