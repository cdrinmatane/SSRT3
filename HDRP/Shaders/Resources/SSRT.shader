Shader "Hidden/Shader/SSRT"
{
    HLSLINCLUDE
    #pragma target 4.5
    #pragma only_renderers d3d11 ps4 xboxone vulkan metal switch
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/PostProcessing/Shaders/FXAA.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/PostProcessing/Shaders/RTUpscale.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/NormalBuffer.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/AtmosphericScattering/AtmosphericScattering.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Sky/SkyUtils.hlsl"

    int _ReuseCount;
    int _LightOnly;
    int _MultiBounceAO;
    int _DirectLightingAO;
    float _MultiBounceGI;
    float _TemporalResponse;
    float4x4 _InverseProjectionMatrix;
    float4x4 _InverseViewProjectionMatrix;
    float4x4 _LastFrameInverseViewProjectionMatrix;

    TEXTURE2D_X(_GBufferTexture0);
    TEXTURE2D_X(_GBufferTexture3);
    TEXTURE2D_X(_InputTexture);
    TEXTURE2D_X(_FilterTexture1);
    TEXTURE2D_X(_FilterTexture2);
    TEXTURE2D_X(_PreviousColor);
    TEXTURE2D_X(_PreviousDepth);
    TEXTURE2D_X(_LightPyramidTexture);

    //SAMPLER(sampler_LinearClamp);

    static const float2 offset[17] =
    {
        float2(0, 0),
        float2(2, -2),
        float2(-2, -2),
        float2(0, 2),

        float2(2, 0),

        float2(0, -2),
        float2(-2, 0),
        float2(-2, 2),
        float2(2, 2),

        float2(4, -4),
        float2(-4, -4),
        float2(0, 4),
        float2(4, 0),

        float2(0, -4),
        float2(-4, 0),
        float2(-4, 4),
        float2(4, 4),
    };

    // From Activision GTAO paper: https://www.activision.com/cdn/research/s2016_pbs_activision_occlusion.pptx
    inline float3 MultiBounceAO(float visibility, float3 albedo)
    {
        float3 a = 2.0404 * albedo - 0.3324;
        float3 b = -4.7951 * albedo + 0.6417;
        float3 c = 2.7552 * albedo + 0.6903;
        
        float x = visibility;
        return max(x, ((x * a + b) * x + c) * x);
    }

    inline float GetLinearDepth(uint2 positionSS)
    {
        float logDepth = LOAD_TEXTURE2D_X(_CameraDepthTexture, positionSS).r; 
        return LinearEyeDepth(logDepth, _ZBufferParams);
    }

    inline float GetPreviousLinearDepth(uint2 positionSS)
    {
        float logDepth = LOAD_TEXTURE2D_X(_PreviousDepth, positionSS).r; 
        return LinearEyeDepth(logDepth, _ZBufferParams);
    }

    inline float3 GetNormalWS(uint2 positionSS)
    {
        NormalData normalData;
        const float4 normalBuffer = LOAD_TEXTURE2D_X(_NormalBufferTexture, positionSS);
        DecodeFromNormalBuffer(normalBuffer, positionSS, normalData);
        return normalData.normalWS;
    }

    inline float3 GetNormalVS(uint2 positionSS)
    {
        float3 normalWS = GetNormalWS(positionSS);
        float3 normalVS = normalize(mul((float3x3)UNITY_MATRIX_V, normalWS));
        return float3(normalVS.xy, -normalVS.z);
    }

    struct Attributes
    {
        uint vertexID : SV_VertexID;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 texcoord   : TEXCOORD0;
        UNITY_VERTEX_OUTPUT_STEREO
    };

    void AtmosphericScatteringCompute(Varyings input, float3 V, float depth, out float3 color, out float3 opacity)
    {
        PositionInputs posInput = GetPositionInput(input.positionCS.xy, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);

        if (depth == UNITY_RAW_FAR_CLIP_VALUE)
        {
            // When a pixel is at far plane, the world space coordinate reconstruction is not reliable.
            // So in order to have a valid position (for example for height fog) we just consider that the sky is a sphere centered on camera with a radius of 5km (arbitrarily chosen value!)
            // And recompute the position on the sphere with the current camera direction.
            posInput.positionWS = GetCurrentViewPosition() - V * _MaxFogDistance;

            // Warning: we do not modify depth values. Use them with care!
        }

        EvaluateAtmosphericScattering(posInput, V, color, opacity); // Premultiplied alpha
    }

    Varyings Vert(Attributes input)
    {
        Varyings output;
        UNITY_SETUP_INSTANCE_ID(input);
        UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
        output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
        output.texcoord = GetFullScreenTriangleTexCoord(input.vertexID);
        return output;
    }
    
    float4 DummyPass(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        
        return 0;
    }
    
    float4 TemporalReprojPass(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        int2 positionSS = input.texcoord * _ScreenSize.xy;
        float2 uv = input.texcoord;
        
        float2 oneOverResolution = (1.0 / _ScreenParams.xy);
                
        float4 gi = LOAD_TEXTURE2D_X(_FilterTexture1, positionSS);
        float depth = GetLinearDepth(positionSS);
        float4 currentPos = float4(uv.x * 2.0 - 1.0, uv.y * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
        
        float4 fragpos = mul(_InverseViewProjectionMatrix, float4(float3(uv * 2 - 1, depth), 1));
        fragpos.xyz /= fragpos.w;
        float4 thisWorldPosition = fragpos;
        
        float2 motionVectors;
        DecodeMotionVector(LOAD_TEXTURE2D_X(_CameraMotionVectorsTexture, positionSS), motionVectors);
        float2 reprojCoord = uv - motionVectors.xy;
        
        float prevDepth = GetPreviousLinearDepth(reprojCoord*_ScreenParams.xy);
        
        float4 previousWorldPosition = mul(_LastFrameInverseViewProjectionMatrix, float4(reprojCoord.xy * 2.0 - 1.0, prevDepth * 2.0 - 1.0, 1.0));
        previousWorldPosition /= previousWorldPosition.w;
        
        float blendWeight = _TemporalResponse;
        
        float posSimilarity = saturate(1.0 - distance(previousWorldPosition.xyz, thisWorldPosition.xyz) * 1.0);
        blendWeight = lerp(1.0, blendWeight, posSimilarity);
        
        float4 minPrev = float4(10000, 10000, 10000, 10000);
        float4 maxPrev = float4(0, 0, 0, 0);

        float4 s0 = LOAD_TEXTURE2D_X(_FilterTexture1, positionSS+int2(1, 1));
        minPrev = s0;
        maxPrev = s0;
        s0 = LOAD_TEXTURE2D_X(_FilterTexture1, positionSS+int2(1, -1));
        minPrev = min(minPrev, s0);
        maxPrev = max(maxPrev, s0);
        s0 = LOAD_TEXTURE2D_X(_FilterTexture1, positionSS+int2(-1, 1));
        minPrev = min(minPrev, s0);
        maxPrev = max(maxPrev, s0);
        s0 = LOAD_TEXTURE2D_X(_FilterTexture1, positionSS+int2(-1, -1));
        minPrev = min(minPrev, s0);
        maxPrev = max(maxPrev, s0);

        float4 prevGI = LOAD_TEXTURE2D_X(_PreviousColor, reprojCoord*_ScreenParams.xy);
        prevGI = lerp(prevGI, clamp(prevGI, minPrev, maxPrev), 0.25);
        
        gi = lerp(prevGI, gi, float4(blendWeight, blendWeight, blendWeight, blendWeight));
        
        return gi;
    }
    
    float4 GetDepthPass(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        uint2 positionSS = input.texcoord * _ScreenSize.xy;
        return LOAD_TEXTURE2D_X(_CameraDepthTexture, positionSS);
    }
    
    float4 DebugAOPass(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        uint2 positionSS = input.texcoord * _ScreenSize.xy;
        float ao = LOAD_TEXTURE2D_X(_PreviousColor, positionSS).a;
        
        if (_MultiBounceAO)
        {
            float3 albedo = LOAD_TEXTURE2D_X(_GBufferTexture0, positionSS);
            ao = MultiBounceAO(ao, albedo);
        }
        
        return float4(ao.xxx, 1);
    }
    
    float4 GIDebugPass(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        uint2 positionSS = input.texcoord * _ScreenSize.xy;
        
        float3 albedo = _LightOnly ? 1 : LOAD_TEXTURE2D_X(_GBufferTexture0, positionSS).rgb;
        float occlusionMap = LOAD_TEXTURE2D_X(_GBufferTexture3, positionSS).g;
        float4 GTAOGI = LOAD_TEXTURE2D_X(_PreviousColor, positionSS).rgba;
        float3 ambient = 0;//_LightOnly ? 0 : LOAD_TEXTURE2D_X(_InputTexture, positionSS).rgb;
        
        if (_MultiBounceAO)
        {
            GTAOGI.a = MultiBounceAO(GTAOGI.a, albedo);
        }

        float3 SceneColor = GTAOGI.rgb * albedo * occlusionMap + ambient * GTAOGI.a;

        return float4(SceneColor, 1);
    }
    
    float4 CombinedDebugPass(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        uint2 positionSS = input.texcoord * _ScreenSize.xy;
        
        float3 albedo = _LightOnly ? 1 : LOAD_TEXTURE2D_X(_GBufferTexture0, positionSS).rgb;
        float occlusionMap = LOAD_TEXTURE2D_X(_GBufferTexture3, positionSS).g;
        float4 GTAOGI = LOAD_TEXTURE2D_X(_PreviousColor, positionSS).rgba;
        float3 ambient = _LightOnly ? 0 : LOAD_TEXTURE2D_X(_InputTexture, positionSS).rgb;
        float3 directLighting = LOAD_TEXTURE2D_X(_InputTexture, positionSS).rgb * (_DirectLightingAO ? GTAOGI.a : 1);
        
        if (_MultiBounceAO)
        {
            GTAOGI.a = MultiBounceAO(GTAOGI.a, albedo);
        }

        float3 V           = GetSkyViewDirWS(positionSS);
        float  depth       = LoadCameraDepth(positionSS);
        float3 volColor, volOpacity;
        AtmosphericScatteringCompute(input, V, depth, volColor, volOpacity);

        float3 sceneColor = GTAOGI.rgb * albedo * occlusionMap * (1.0 - volOpacity); // GI is attenuated by fog opacity
        sceneColor += ambient * GTAOGI.a + (1.0 - GTAOGI.a) * volColor; // Ambient AO is compensated by fog color

        return float4(sceneColor, 1);
    }

    float4 GetLightmaskPass(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        uint2 positionSS = input.texcoord * _ScreenSize.xy;
        float2 uv = input.texcoord;

        float2 motionVectors;
        DecodeMotionVector(LOAD_TEXTURE2D_X(_CameraMotionVectorsTexture, positionSS), motionVectors);
        float2 reprojCoord = uv - motionVectors.xy;

        float3 lightRGB = LOAD_TEXTURE2D_X(_PreviousColor, reprojCoord*_ScreenSize.xy) * _MultiBounceGI;
        float3 albedo = LOAD_TEXTURE2D_X(_GBufferTexture0, positionSS).rgb;
        float3 ambient = lightRGB * albedo; 
        return float4(LOAD_TEXTURE2D_X(_InputTexture, positionSS).rgb + lightRGB /*+ ambient*/, 1) ;
    }

    // Spheremap transform encoding from https://aras-p.info/texts/CompactNormalStorage.html
    float4 GetNormalPass(Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        uint2 positionSS = input.texcoord * _ScreenSize.xy;
        const float4 normalBuffer = LOAD_TEXTURE2D_X(_NormalBufferTexture, positionSS);
        NormalData normalData;
        DecodeFromNormalBuffer(normalBuffer, positionSS, normalData);
        float p = sqrt(normalData.normalWS.z*8+8);
        return float4(normalData.normalWS.xy/p + 0.5,0,0);
        //float3 normalVS = normalize(mul((float3x3)UNITY_MATRIX_V, normalWS));
    }

    ENDHLSL
    SubShader
    {
        Pass // 0
        {
            Name "Unused"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
                #pragma fragment DummyPass
                #pragma vertex Vert
            ENDHLSL
        }
        
        Pass // 1
        {
            Name "Upsample"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
                #pragma fragment DummyPass
                #pragma vertex Vert
            ENDHLSL
        }
        
        Pass // 2
        {
            Name "Unused"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
                #pragma fragment DummyPass
                #pragma vertex Vert
            ENDHLSL
        }
        
        Pass // 3
        {
            Name "TemporalReproj"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
                #pragma fragment TemporalReprojPass
                #pragma vertex Vert
            ENDHLSL
        }
        
        Pass // 4
        {
            Name "DebugMode AO"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
                #pragma fragment DebugAOPass
                #pragma vertex Vert
            ENDHLSL
        }
        
        Pass // 5
        {
            Name "Unused"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
                #pragma fragment DummyPass
                #pragma vertex Vert
            ENDHLSL
        }
        
        Pass // 6
        {
            Name "DebugMode GI"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
                #pragma fragment GIDebugPass
                #pragma vertex Vert
            ENDHLSL
        }
        
        Pass // 7
        {
            Name "DebugMode Combined"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
                #pragma fragment CombinedDebugPass
                #pragma vertex Vert
            ENDHLSL
        }
        
        Pass // 8
        {
            Name "GetDepth"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
                #pragma fragment GetDepthPass
                #pragma vertex Vert
            ENDHLSL
        }

        Pass // 9
        {
            Name "GetLightmask"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
                #pragma fragment GetLightmaskPass
                #pragma vertex Vert
            ENDHLSL
        }

        Pass // 10
        {
            Name "GetNormals"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
                #pragma fragment GetNormalPass
                #pragma vertex Vert
            ENDHLSL
        }
    }

    Fallback Off
}