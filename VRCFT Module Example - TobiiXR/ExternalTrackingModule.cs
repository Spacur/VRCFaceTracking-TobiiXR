// #define TOBIIDEBUG

/* Define TOBIIDEBUG symbol if debug logs are needed, Release configuration causes issues with undefined reference. 
 * Either due to pointer/memory handling, race condition/multithreading differences etc.
 * Due to this, stay on Debug configuration.
*/

using System;
using System.Threading;
using VRCFaceTracking;
using VRCFaceTracking.Params;
using Tobii.StreamEngine;
using System.Collections.Generic;
using System.Diagnostics;
using Qromodyn;
using System.Runtime.InteropServices;

namespace VRCFT_Module_TobiiXR
{

    // Action to provide debug logs only if TOBIIDEBUG is set.
    class TobiiXRLogger
    {
        public static Action<string> Log = delegate (string x)
        {
            #if TOBIIDEBUG
                Logger.Msg(x);
            #endif
        };
    }

    // TobiiXR "single-eye" desktop data response.
    public struct TobiiXRExternalTrackingDesktopDataEye
    {
        public tobii_validity_t validity;
        public float eye_x;
        public float eye_y;
    }  
     
    // This class contains the overrides for any VRCFT Tracking Data struct functions
    public static class TrackingData
    {
        // Convert eye X/Y data from 1 to 0 format into 1 to -1 format and then normalise to ensure max values are either 1 or -1 respectively.
        // TODO : Callibration tool to gather max values for each axis/directions for better accuracy here.
        private static float NormalisationX = 0.5f;
        private static float NormalisationY = 0.5f;

        private static float ParseEyeData(float EyeData, bool XorY = true)
        {
            if (XorY)
                return ((EyeData * 2) - 1) / NormalisationX;
            else
                return ((EyeData * -2) + 1) / NormalisationY;
        }

        // This function parses the external module's single-eye data into a VRCFT-Parseable format
        public static void Update(ref EyeTrackingData data, TobiiXRExternalTrackingDesktopDataEye external)
        {
            var look = new Vector2(ParseEyeData(external.eye_x), ParseEyeData(external.eye_y, false));
            data.Left.Look = look;
            data.Right.Look = look;
        }
    }

    public class ExternalExtTrackingModule : ExtTrackingModule
    {
        // Synchronous module initialization. Take as much time as you need to initialize any external modules. This runs in the init-thread
        public override (bool SupportsEye, bool SupportsLip) Supported => (true, true);

        // Initialise the variables needed for Tobii Stream Engine
        private IntPtr apiContext = Marshal.AllocHGlobal(1024);
        private IntPtr deviceContext = Marshal.AllocHGlobal(1024);
        private List<string> urls;
        private tobii_error_t result;
        private bool isUserPresent = false;

        // Initialise struct which tracking data will be inputted into
        public TobiiXRExternalTrackingDesktopDataEye ParsedtrackingData;

        public override (bool eyeSuccess, bool lipSuccess) Initialize(bool eye, bool lip)
        {
            TobiiXRLogger.Log("Initializing inside external module");

            // Extract Embedded Tobii Stream Engine DLL for use in Tobii.StreamEngine.Interop.cs
            EmbeddedDllClass.ExtractEmbeddedDlls("tobii_stream_engine.dll", Properties.Resources.tobii_stream_engine);

            // Create API context
            result = Interop.tobii_api_create(out apiContext, null);
            // Debug.Assert(result == tobii_error_t.TOBII_ERROR_NO_ERROR);
            TobiiXRLogger.Log(result.ToString());

            // Enumerate devices to find connected eye trackers
            result = Interop.tobii_enumerate_local_device_urls(apiContext, out urls);
            TobiiXRLogger.Log(result.ToString());
            // Debug.Assert(result == tobii_error_t.TOBII_ERROR_NO_ERROR);
            if (urls.Count == 0)
            {
                Logger.Error("Error: No device found");
                return (false, false);
            }
            foreach (string url in urls)
            {
                TobiiXRLogger.Log(url);
            }

            // Connect to the first tracker found
            result = Interop.tobii_device_create(apiContext, urls[0], Interop.tobii_field_of_use_t.TOBII_FIELD_OF_USE_STORE_OR_TRANSFER_FALSE, out deviceContext);
            TobiiXRLogger.Log(result.ToString());
            // Debug.Assert(result == tobii_error_t.TOBII_ERROR_NO_ERROR);

            return (true, false);
        }

        // This will be run in the tracking thread. This is exposed so you can control when and if the tracking data is updated down to the lowest level.
        public override Action GetUpdateThreadFunc()
        {
            return () =>
            {
                // Subscribe to consumer data which will be sent to the classes local update method
                result = Interop.tobii_gaze_point_subscribe(deviceContext, Update);
                result = Interop.tobii_user_presence_subscribe(deviceContext, Presense);
                TobiiXRLogger.Log(result.ToString());
                // Debug.Assert(result == tobii_error_t.TOBII_ERROR_NO_ERROR);

                while (true)
                {

                    // Optionally block this thread until data is available. Especially useful if running in a separate thread.
                    Interop.tobii_wait_for_callbacks(new[] { deviceContext });
                    Debug.Assert(result == tobii_error_t.TOBII_ERROR_NO_ERROR || result == tobii_error_t.TOBII_ERROR_TIMED_OUT);

                    // TobiiXRLogger.Log(deviceContext.ToString());

                    // Process callbacks on this thread if data is available
                    Interop.tobii_device_process_callbacks(deviceContext);
                    Debug.Assert(result == tobii_error_t.TOBII_ERROR_NO_ERROR);

                    //Thread.Sleep(10);
                }
            };
        }

        private void Presense(tobii_user_presence_status_t status, long timestamp_us, IntPtr user_data)
        {
            switch (status)
            {
                case tobii_user_presence_status_t.TOBII_USER_PRESENCE_STATUS_PRESENT:
                    isUserPresent = true;
                    break;
                default:
                    isUserPresent = false;
                    break;
            }
        }

        // TobiiXRLogger.Log("Updating inside external module.");
        private void Update(ref tobii_gaze_point_t gaze_point, IntPtr user_data)
        {
            if (Status.EyeState == ModuleState.Active && isUserPresent)
            {
                TobiiXRLogger.Log("Eye data is being utilized.");
                
                if (gaze_point.validity == tobii_validity_t.TOBII_VALIDITY_VALID)
                {
                    ParsedtrackingData.eye_x = gaze_point.position.x;
                    ParsedtrackingData.eye_y = gaze_point.position.y;

                    ParsedtrackingData.eye_x = gaze_point.position.x;
                    ParsedtrackingData.eye_y = gaze_point.position.y;

                    TrackingData.Update(ref UnifiedTrackingData.LatestEyeData, ParsedtrackingData);
                }
                
                TobiiXRLogger.Log(UnifiedTrackingData.LatestEyeData.Left.Openness.ToString() + " " + UnifiedTrackingData.LatestEyeData.Right.Openness.ToString());
                TobiiXRLogger.Log(UnifiedTrackingData.LatestEyeData.Left.Look.x.ToString() + " " + UnifiedTrackingData.LatestEyeData.Left.Look.y.ToString());
                TobiiXRLogger.Log(UnifiedTrackingData.LatestEyeData.Right.Look.x.ToString() + " " + UnifiedTrackingData.LatestEyeData.Right.Look.y.ToString());
            }
        }

        // A chance to de-initialize everything. This runs synchronously inside main game thread. Do not touch any Unity objects here.
        public override void Teardown()
        {
            Interop.tobii_user_presence_unsubscribe(deviceContext);
            Interop.tobii_gaze_point_unsubscribe(deviceContext);
            Interop.tobii_device_destroy(deviceContext);
            Interop.tobii_api_destroy(apiContext);
            Marshal.FreeHGlobal(deviceContext);
            Marshal.FreeHGlobal(apiContext);
            TobiiXRLogger.Log("Teardown");
        }
    }
}