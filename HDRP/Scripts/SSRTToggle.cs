using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Experimental.Rendering;

public class SSRTToggle : MonoBehaviour
{
    [SerializeField] 
    private VolumeProfile ssrtVolume;
    private SSRT_HDRP ssrtComponent;

    void Awake()
    {
        ssrtVolume.TryGet<SSRT_HDRP>(out ssrtComponent);
    }

    // Update is called once per frame
    void Update()
    {
        if(ssrtComponent == null)
            return;

        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            ssrtComponent.enabled.value = true;
            ssrtComponent.debugMode.value = SSRT_HDRP.DebugMode.None;
            ssrtComponent.lightOnly.value = false;
        }

        if (Input.GetKeyDown(KeyCode.Alpha2))
        {
            ssrtComponent.enabled.value = false;
        }

        if (Input.GetKeyDown(KeyCode.Alpha3))
        {
            ssrtComponent.enabled.value = true;
            ssrtComponent.debugMode.value = SSRT_HDRP.DebugMode.GI;
            ssrtComponent.lightOnly.value = false;
        }

        if (Input.GetKeyDown(KeyCode.Alpha4))
        {
            ssrtComponent.enabled.value = true;
            ssrtComponent.debugMode.value = SSRT_HDRP.DebugMode.GI;
            ssrtComponent.lightOnly.value = true;
        }

        if (Input.GetKeyDown(KeyCode.Alpha5))
        {
            ssrtComponent.enabled.value = true;
            ssrtComponent.debugMode.value = SSRT_HDRP.DebugMode.AO;
        }

        if (Input.GetKeyDown(KeyCode.T))
        {
            ssrtComponent.temporalAccumulation.value = !ssrtComponent.temporalAccumulation.value;
        }

        if (Input.GetKeyDown(KeyCode.B))
        {
            ssrtComponent.denoising.value = !ssrtComponent.denoising.value;
        }
    }
}
