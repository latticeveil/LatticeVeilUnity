using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LatticeVeil.Game
{
    /// <summary>
    /// Live microphone engine for the Options AUDIO tab (Unity equivalent of
    /// the MonoGame MicTester):
    ///  - enumerates capture (microphone) devices via Microphone.devices
    ///  - enumerates OUTPUT devices via WASAPI through the Windows MMDevice
    ///    registry (no NAudio in the Unity player): renders
    ///    Render\DeviceInterfaces under
    ///    HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio
    ///    and resolves friendly names from the Properties subkey
    ///  - runs an always-on capture loop on the selected mic, exposes a
    ///    smoothed 0..1 Level for the meter, and (when monitor is on) plays
    ///    the capture back through an AudioSource for sidetone.
    /// </summary>
    public static class MicMonitorHost
    {
        private const int SampleRate = 24000;
        // Shorter loop = less sidetone delay. 0.35 s keeps the AudioSource
        // fed while staying near real time (1 s loops feel clearly delayed).
        private const float ClipSeconds = 0.35f;

        private static AudioClip _clip;
        private static string _clipDevice;
        private static float[] _buffer = new float[(int)(SampleRate * ClipSeconds)];
        private static float _smoothedLevel;
        private static AudioSource _sidetone;
        private static bool _monitorEnabled;
        private static float _micVolume = 1f;
        private static string _selectedDevice = ""; // "" = system default

        /// <summary>Smoothed 0..1 input level for the meter bar.</summary>
        public static float Level => _smoothedLevel;

        public static bool IsCapturing => _clip != null;

        /// <summary>
        /// Output (render) devices: id + friendly name. Unity's netstandard
        /// profile doesn't include Microsoft.Win32.Registry, so this uses
        /// advapi32 P/Invoke to walk the WASAPI MMDevice registry:
        /// HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render
        /// Windows-only; elsewhere or on failure it returns an empty list
        /// (UI keeps "SYSTEM DEFAULT").
        /// </summary>
        public static List<(string id, string name)> EnumerateOutputDevices()
        {
            var result = new List<(string id, string name)>();
            if (Application.platform != RuntimePlatform.WindowsPlayer &&
                Application.platform != RuntimePlatform.WindowsEditor)
                return result;

            try
            {
                const string devicesKey =
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";

                if (RegOpenKeyEx(HKEY_LOCAL_MACHINE, devicesKey, 0, KEY_READ, out var rootKey) != 0)
                    return result;

                try
                {
                    // Enumerate subkeys (one per endpoint GUID).
                    uint index = 0;
                    var nameBuffer = new System.Text.StringBuilder(256);
                    while (true)
                    {
                        uint nameLen = 256;
                        int err = RegEnumKeyEx(rootKey, index, nameBuffer, ref nameLen,
                            IntPtr.Zero, null, IntPtr.Zero, IntPtr.Zero);
                        if (err != 0)
                            break; // ERROR_NO_MORE_ITEMS or other failure
                        var deviceId = nameBuffer.ToString();
                        index++;

                        if (RegOpenKeyEx(rootKey, deviceId, 0, KEY_READ, out var devKey) != 0)
                            continue;
                        try
                        {
                            if (RegOpenKeyEx(devKey, "Properties", 0, KEY_READ, out var propsKey) != 0)
                                continue;
                            try
                            {
                                // {b3f8fa53-...},0 = endpoint friendly name (preferred)
                                // {a45c254e-...},2 = device friendly name
                                var name = ReadRegString(propsKey,
                                    "{b3f8fa53-0004-438e-9003-51a46e139bfc},0")
                                    ?? ReadRegString(propsKey,
                                        "{a45c254e-df1c-4efd-8020-67d146a850e0},2");

                                if (!string.IsNullOrWhiteSpace(name))
                                    result.Add((deviceId, name));
                            }
                            finally { RegCloseKey(propsKey); }
                        }
                        finally { RegCloseKey(devKey); }
                    }
                }
                finally { RegCloseKey(rootKey); }
            }
            catch
            {
                // Registry access failed: default-only.
            }

            // Same friendly name can appear on multiple endpoints (e.g. HDMI
            // audio on several outputs of one GPU). Show each name once.
            return result
                .GroupBy(d => d.name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(d => d.name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string ReadRegString(IntPtr key, string valueName)
        {
            const uint RRF_RT_REG_SZ = 0x00000002;
            uint cbData = 0;
            // Size query with null buffer.
            if (RegGetValueW(key, null, valueName, RRF_RT_REG_SZ, IntPtr.Zero, IntPtr.Zero, ref cbData) != 0)
                return null;
            var data = new byte[cbData];
            if (RegGetValueW(key, null, valueName, RRF_RT_REG_SZ, IntPtr.Zero, data, ref cbData) != 0)
                return null;
            return System.Text.Encoding.Unicode.GetString(data).TrimEnd('\0');
        }

#pragma warning disable 414 // keep constants for readability
        private static readonly IntPtr HKEY_LOCAL_MACHINE = new IntPtr(unchecked((int)0x80000002));
        private const uint KEY_READ = 0x20019;
#pragma warning restore 414

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegOpenKeyEx(IntPtr hKey, string subKey, uint options, uint samDesired, out IntPtr hkResult);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegEnumKeyEx(IntPtr hKey, uint index,
            System.Text.StringBuilder nameBuilder, ref uint nameLen,
            IntPtr reservedClass, string reservedClassBuffer, IntPtr reservedClassLen, IntPtr reservedLastWrite);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegGetValueW(IntPtr hKey, string lpSubKey, string lpValue,
            uint dwFlags, IntPtr pdwType, byte[] pvData, ref uint pcbData);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegGetValueW(IntPtr hKey, string lpSubKey, string lpValue,
            uint dwFlags, IntPtr pdwType, IntPtr pvData, ref uint pcbData);

        [DllImport("advapi32.dll")]
        private static extern int RegCloseKey(IntPtr hKey);

        /// <summary>Capture (microphone) devices. First entry is system default.</summary>
        public static List<(string id, string name)> EnumerateInputDevices()
        {
            var result = new List<(string id, string name)> { ("", "SYSTEM DEFAULT") };
            try
            {
                foreach (var name in Microphone.devices)
                    result.Add((name, string.IsNullOrWhiteSpace(name) ? "MICROPHONE" : name));
            }
            catch
            {
                // Microphone subsystem unavailable.
            }
            return result;
        }


        /// <summary>
        /// Starts/updates the always-on capture loop. Safe to call every time
        /// settings change; restarts capture only when the device changed.
        /// </summary>
        public static void Configure(string inputDeviceId, bool micEnabled, float micVolume, bool monitor)
        {
            _selectedDevice = string.IsNullOrWhiteSpace(inputDeviceId) ? "" : inputDeviceId;
            _micVolume = Mathf.Clamp01(micVolume);
            _monitorEnabled = monitor && micEnabled;

            EnsureSidetone();

            if (!micEnabled || Microphone.devices.Length == 0)
            {
                StopCapture();
                UpdateSidetoneRouting();
                return;
            }

            StartCaptureIfNeeded();
            UpdateSidetoneRouting();
        }

        /// <summary>Call once per frame from the Options screen to drive the meter.</summary>
        public static void Poll()
        {
            if (_clip == null)
            {
                _smoothedLevel = Mathf.Lerp(_smoothedLevel, 0f, Time.unscaledDeltaTime * 10f);
                return;
            }

            int pos = Microphone.GetPosition(_clipDevice.Length == 0 ? null : _clipDevice);
            if (pos <= 0)
                return;

            // Read a fixed recent window (~50 ms) ending at the write head —
            // reads the freshest audio regardless of clip length.
            int window = Mathf.Min((int)(SampleRate * 0.05f), _buffer.Length);
            if (_clip.GetData(_buffer, 0))
            {
                float peak = 0f;
                for (int i = 0; i < window; i++)
                {
                    int idx = (pos - window + i + _buffer.Length * 2) % _buffer.Length;
                    float s = Mathf.Abs(_buffer[idx]) * _micVolume;
                    if (s > peak) peak = s;
                }

                // Attack instantly (bar jumps up), release smoothly (falls).
                float level = Mathf.Clamp01(peak * 3.5f);
                _smoothedLevel = level >= _smoothedLevel
                    ? level
                    : Mathf.Lerp(_smoothedLevel, level, Time.unscaledDeltaTime * 14f);
            }
        }

        public static void Shutdown()
        {
            StopCapture();
            _monitorEnabled = false;
            if (_sidetone != null)
            {
                _sidetone.Stop();
                UnityEngine.Object.Destroy(_sidetone.gameObject);
                _sidetone = null;
            }
            _smoothedLevel = 0f;
        }

        // ------------------------------------------------------------------

        private static void StartCaptureIfNeeded()
        {
            // Unity's null/empty device string means "system default mic".
            string unityDevice = _selectedDevice;
            if (!string.IsNullOrEmpty(unityDevice) && Array.IndexOf(Microphone.devices, unityDevice) < 0)
            {
                // Saved device not present anymore; fall back to default.
                unityDevice = "";
            }

            if (_clip != null && _clipDevice == unityDevice && Microphone.IsRecording(_clipDevice.Length == 0 ? null : _clipDevice))
                return;

            StopCapture();

            // Microphone.Start takes whole seconds (0 throws). Use 1 s of ring
            // buffer, but keep sidetone near-real-time by seeking the play
            // head to just behind the write cursor (see UpdateSidetoneRouting).
            _clip = Microphone.Start(unityDevice.Length == 0 ? null : unityDevice, true, Mathf.Max(1, (int)ClipSeconds), SampleRate);
            _clipDevice = unityDevice;
        }

        private static void StopCapture()
        {
            if (_clip != null)
            {
                Microphone.End(_clipDevice.Length == 0 ? null : _clipDevice);
                if (_clip != null) UnityEngine.Object.Destroy(_clip);
                _clip = null;
                _clipDevice = null;
            }
        }

        private static void EnsureSidetone()
        {
            if (_sidetone != null)
                return;
            var go = new GameObject("MicSidetone", typeof(AudioSource));
            UnityEngine.Object.DontDestroyOnLoad(go);
            _sidetone = go.GetComponent<AudioSource>();
            _sidetone.loop = true;
            _sidetone.playOnAwake = false;
            _sidetone.volume = 1f;
        }

        private static void UpdateSidetoneRouting()
        {
            if (_sidetone == null)
                return;

            bool shouldPlay = _monitorEnabled && _clip != null;
            if (shouldPlay)
            {
                if (_sidetone.clip != _clip)
                {
                    _sidetone.clip = _clip;
                    _sidetone.volume = Mathf.Clamp01(_micVolume);
                    // Loop points nearest the write head: play from a position
                    // just behind the mic write cursor so sidetone trails only
                    // the DSP buffer, not the whole clip length.
                    _sidetone.Play();
                    int pos = Microphone.GetPosition(_clipDevice.Length == 0 ? null : _clipDevice);
                    if (pos > 0)
                        _sidetone.timeSamples = Mathf.Max(0, pos - 256);
                }
                else if (!_sidetone.isPlaying)
                {
                    _sidetone.Play();
                }
                // Keep tracking the write head so the loop stays near-real-time.
                int cur = Microphone.GetPosition(_clipDevice.Length == 0 ? null : _clipDevice);
                if (cur > 0 && Mathf.Abs(_sidetone.timeSamples - cur) > _buffer.Length / 2)
                    _sidetone.timeSamples = Mathf.Max(0, cur - 256);
            }
            else if (_sidetone.isPlaying)
            {
                _sidetone.Stop();
                _sidetone.clip = null;
            }
        }
    }
}
