using System;
using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;
using UnityEngine;

namespace Killer_Frequency_Access;

internal static class SpeechRouter
{
    private enum SpeechBackend
    {
        None,
        Nvda,
        Jaws,
        Sapi
    }

    private const float DetectionRefreshSeconds = 2f;

    private static readonly NvdaClient Nvda = new();
    private static readonly JawsClient Jaws = new();
    private static readonly SapiClient Sapi = new();

    private static MelonLogger.Instance? _logger;
    private static SpeechBackend _activeBackend = SpeechBackend.None;
    private static float _lastDetectAt = -1000f;

    public static void Initialize(MelonLogger.Instance logger)
    {
        _logger = logger;
        RefreshBackend(true);
        logger.Msg($"Speech backend: {_activeBackend}");
    }

    public static void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        RefreshBackend(false);
        if (TrySpeakWithActiveBackend(text))
        {
            return;
        }

        if (Sapi.IsAvailable && Sapi.Speak(text))
        {
            _activeBackend = SpeechBackend.Sapi;
            return;
        }
    }

    public static void Shutdown()
    {
        Jaws.Dispose();
        Sapi.Dispose();
    }

    private static bool TrySpeakWithActiveBackend(string text)
    {
        switch (_activeBackend)
        {
            case SpeechBackend.Nvda:
                return Nvda.Speak(text);
            case SpeechBackend.Jaws:
                return Jaws.Speak(text);
            case SpeechBackend.Sapi:
                return Sapi.Speak(text);
            default:
                return false;
        }
    }

    private static void RefreshBackend(bool force)
    {
        var now = Time.unscaledTime;
        if (!force && now - _lastDetectAt < DetectionRefreshSeconds)
        {
            return;
        }

        _lastDetectAt = now;

        var previous = _activeBackend;

        if (Nvda.IsRunning())
        {
            _activeBackend = SpeechBackend.Nvda;
        }
        else if (Jaws.IsRunning())
        {
            _activeBackend = SpeechBackend.Jaws;
        }
        else if (Sapi.IsAvailable)
        {
            _activeBackend = SpeechBackend.Sapi;
        }
        else
        {
            _activeBackend = SpeechBackend.None;
        }

        if (previous != _activeBackend)
        {
            _logger?.Msg($"Speech backend switched: {_activeBackend}");
        }
    }
}

internal sealed class NvdaClient
{
    public bool IsRunning()
    {
        try
        {
            return nvdaController_testIfRunning() == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    public bool Speak(string text)
    {
        try
        {
            return nvdaController_speakText(text) == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("nvdaControllerClient64.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern int nvdaController_speakText([MarshalAs(UnmanagedType.LPWStr)] string text);

    [DllImport("nvdaControllerClient64.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int nvdaController_testIfRunning();
}

internal sealed class JawsClient
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private delegate bool JawsSayStringDelegate([MarshalAs(UnmanagedType.LPWStr)] string text, [MarshalAs(UnmanagedType.Bool)] bool interrupt);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool JawsIsRunningDelegate();

    private IntPtr _libraryHandle = IntPtr.Zero;
    private JawsSayStringDelegate? _say;
    private JawsIsRunningDelegate? _isRunning;
    private bool _loadAttempted;

    public bool IsRunning()
    {
        if (!EnsureLoaded())
        {
            return false;
        }

        if (_isRunning != null)
        {
            try
            {
                return _isRunning();
            }
            catch
            {
                return false;
            }
        }

        return FindWindow("JFWUI2", null) != IntPtr.Zero;
    }

    public bool Speak(string text)
    {
        if (!EnsureLoaded() || _say == null)
        {
            return false;
        }

        try
        {
            return _say(text, true);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_libraryHandle == IntPtr.Zero)
        {
            return;
        }

        FreeLibrary(_libraryHandle);
        _libraryHandle = IntPtr.Zero;
        _say = null;
        _isRunning = null;
    }

    private bool EnsureLoaded()
    {
        if (_loadAttempted)
        {
            return _libraryHandle != IntPtr.Zero && _say != null;
        }

        _loadAttempted = true;
        _libraryHandle = LoadLibrary("jfwapi.dll");
        if (_libraryHandle == IntPtr.Zero)
        {
            return false;
        }

        _say = TryGetFunction<JawsSayStringDelegate>("JFWSayString");
        _isRunning = TryGetFunction<JawsIsRunningDelegate>("JFWIsRunning");
        return _say != null;
    }

    private T? TryGetFunction<T>(string functionName) where T : class
    {
        if (_libraryHandle == IntPtr.Zero)
        {
            return null;
        }

        var address = GetProcAddress(_libraryHandle, functionName);
        if (address == IntPtr.Zero)
        {
            return null;
        }

        return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
}

internal sealed class SapiClient
{
    private object? _voice;
    private Type? _voiceType;

    public bool IsAvailable => EnsureCreated();

    public bool Speak(string text)
    {
        if (!EnsureCreated())
        {
            return false;
        }

        try
        {
            _voiceType!.InvokeMember("Speak", BindingFlags.InvokeMethod, null, _voice, new object[] { text, 3 });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_voice == null || !Marshal.IsComObject(_voice))
        {
            return;
        }

        Marshal.ReleaseComObject(_voice);
        _voice = null;
        _voiceType = null;
    }

    private bool EnsureCreated()
    {
        if (_voice != null)
        {
            return true;
        }

        try
        {
            _voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (_voiceType == null)
            {
                return false;
            }

            _voice = Activator.CreateInstance(_voiceType);
            return _voice != null;
        }
        catch
        {
            return false;
        }
    }
}


