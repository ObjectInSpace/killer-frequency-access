using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MelonLoader;

namespace Killer_Frequency_Access;

/// <summary>
/// Speech and braille through Prism (github.com/ethindp/prism, MPL-2.0, v0.18.3 in native\), which drives
/// NVDA, JAWS, ZDSR, PC-Talker, ZoomText, System Access and Narrator, and falls back to the OS voice
/// (SAPI/OneCore) when no screen reader runs. Ported from Before The Storm Access (PrismSpeech.cs),
/// Windows path only: Killer Frequency ships on Windows.
///
/// prism.dll sits next to the mod DLL in Mods\ and is loaded by absolute path, with every export bound
/// through a function pointer. Output never interrupts: lines queue in the order the mod speaks them.
/// </summary>
internal static class SpeechRouter
{
    private const int PrismOk = 0;
    private const int PrismErrorNotImplemented = 3;
    private const int PrismErrorAlreadyInitialized = 15;
    private const ulong FeatureSupportedAtRuntime = 1UL << 0;

    // Plain TTS backends: while one is active, keep checking for a screen reader that starts later.
    private const ulong BackendSapi = 0x1D6DF72422CEEE66;
    private const ulong BackendOneCore = 0x6797D32F0D994CB4;
    private const int ScreenReaderProbeIntervalMs = 5000;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr InitFn(IntPtr config);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ContextFn(IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateBestFn(IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate UIntPtr RegistryCountFn(IntPtr ctx);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong RegistryIdAtFn(IntPtr ctx, UIntPtr index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong RegistryIdFn(IntPtr ctx, byte[] utf8Name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RegistryPriorityFn(IntPtr ctx, ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr RegistryCreateFn(IntPtr ctx, ulong id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr BackendNameFn(IntPtr backend);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong BackendFeaturesFn(IntPtr backend);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BackendCallFn(IntPtr backend);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BackendOutputFn(IntPtr backend, byte[] utf8Text, [MarshalAs(UnmanagedType.I1)] bool interrupt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void BackendFreeFn(IntPtr backend);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ErrorStringFn(int error);

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);

    private static MelonLogger.Instance? _logger;
    private static IntPtr _library;
    private static InitFn? _init;
    private static ContextFn? _shutdown;
    private static CreateBestFn? _createBest;
    private static RegistryCountFn? _registryCount;
    private static RegistryIdAtFn? _registryIdAt;
    private static RegistryIdFn? _registryId;
    private static RegistryPriorityFn? _registryPriority;
    private static RegistryCreateFn? _registryCreate;
    private static BackendNameFn? _backendName;
    private static BackendFeaturesFn? _backendFeatures;
    private static BackendCallFn? _backendInitialize;
    private static BackendOutputFn? _backendOutput;
    private static BackendFreeFn? _backendFree;
    private static ErrorStringFn? _errorString;

    private static IntPtr _context;
    private static IntPtr _backend;
    private static ulong _backendId;
    private static int _backendPriority;
    private static int _lastProbeTick;
    private static bool _loggedOutputError;
    private static string _backendNameText = string.Empty;

    public static void Initialize(MelonLogger.Instance logger)
    {
        _logger = logger;
        try
        {
            if (!LoadLibrary())
            {
                return;
            }

            _context = _init!(IntPtr.Zero);
            if (_context == IntPtr.Zero)
            {
                logger.Error("Prism: prism_init failed; speech is disabled.");
                return;
            }

            SelectBest();
        }
        catch (Exception ex)
        {
            logger.Error($"Prism: initialization failed: {ex}");
        }
    }

    public static void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Every spoken line goes to the log, so a test run shows exactly what the player heard.
        _logger?.Msg($"[speech] {text}");
        if (_context == IntPtr.Zero)
        {
            return;
        }

        try
        {
            ProbeForScreenReader();
            if (_backend == IntPtr.Zero && !SelectBest())
            {
                return;
            }

            var utf8 = ToUtf8(text);
            var result = _backendOutput!(_backend, utf8, false);
            if (result == PrismOk)
            {
                _loggedOutputError = false;
                return;
            }

            if (!_loggedOutputError)
            {
                _loggedOutputError = true;
                _logger?.Warning($"Prism: {_backendNameText} output failed ({ErrorText(result)}); re-selecting backend.");
            }

            if (SelectBest())
            {
                _backendOutput(_backend, utf8, false);
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Prism: output threw: {ex.Message}");
        }
    }

    public static void Shutdown()
    {
        try
        {
            FreeBackend();
            if (_context != IntPtr.Zero)
            {
                _shutdown!(_context);
                _context = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Prism: shutdown failed: {ex.Message}");
        }
    }

    private static bool LoadLibrary()
    {
        var directory = Path.GetDirectoryName(typeof(SpeechRouter).Assembly.Location) ?? string.Empty;
        var path = Path.Combine(directory, "prism.dll");
        if (!File.Exists(path))
        {
            _logger?.Error($"Prism: {path} is missing; speech is disabled.");
            return false;
        }

        _library = LoadLibraryW(path);
        if (_library == IntPtr.Zero)
        {
            _logger?.Error($"Prism: could not load {path} (Windows error {Marshal.GetLastWin32Error()}).");
            return false;
        }

        _init = Bind<InitFn>("prism_init");
        _shutdown = Bind<ContextFn>("prism_shutdown");
        _createBest = Bind<CreateBestFn>("prism_registry_create_best");
        _registryCount = Bind<RegistryCountFn>("prism_registry_count");
        _registryIdAt = Bind<RegistryIdAtFn>("prism_registry_id_at");
        _registryId = Bind<RegistryIdFn>("prism_registry_id");
        _registryPriority = Bind<RegistryPriorityFn>("prism_registry_priority");
        _registryCreate = Bind<RegistryCreateFn>("prism_registry_create");
        _backendName = Bind<BackendNameFn>("prism_backend_name");
        _backendFeatures = Bind<BackendFeaturesFn>("prism_backend_get_features");
        _backendInitialize = Bind<BackendCallFn>("prism_backend_initialize");
        _backendOutput = Bind<BackendOutputFn>("prism_backend_output");
        _backendFree = Bind<BackendFreeFn>("prism_backend_free");
        _errorString = Bind<ErrorStringFn>("prism_error_string");

        _logger?.Msg($"Prism: loaded {path}");
        return true;
    }

    private static T Bind<T>(string export) where T : Delegate
    {
        var symbol = GetProcAddress(_library, export);
        if (symbol == IntPtr.Zero)
        {
            throw new EntryPointNotFoundException($"Prism export not found: {export}");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(symbol);
    }

    /// <summary>Replaces the active backend with the highest-priority one that initializes.</summary>
    private static bool SelectBest()
    {
        FreeBackend();
        var backend = _createBest!(_context);
        if (backend == IntPtr.Zero)
        {
            _logger?.Warning("Prism: no speech backend available (no screen reader and no OS voice).");
            return false;
        }

        Adopt(backend);
        _logger?.Msg($"Speech backend: {_backendNameText}");
        return true;
    }

    /// <summary>On a plain TTS backend, retries the screen readers every few seconds so one started later takes over.</summary>
    private static void ProbeForScreenReader()
    {
        if (_backend == IntPtr.Zero || (_backendId != BackendSapi && _backendId != BackendOneCore))
        {
            return;
        }

        var now = Environment.TickCount;
        if (unchecked(now - _lastProbeTick) < ScreenReaderProbeIntervalMs)
        {
            return;
        }

        _lastProbeTick = now;
        var count = (int)_registryCount!(_context).ToUInt32();
        for (var i = 0; i < count; i++)
        {
            var id = _registryIdAt!(_context, new UIntPtr((uint)i));
            if (_registryPriority!(_context, id) <= _backendPriority)
            {
                break; // the registry is sorted by descending priority
            }

            var candidate = _registryCreate!(_context, id);
            if (candidate == IntPtr.Zero)
            {
                continue;
            }

            if ((_backendFeatures!(candidate) & FeatureSupportedAtRuntime) == 0)
            {
                _backendFree!(candidate);
                continue;
            }

            var result = _backendInitialize!(candidate);
            if (result != PrismOk && result != PrismErrorAlreadyInitialized)
            {
                _backendFree!(candidate);
                continue;
            }

            FreeBackend();
            Adopt(candidate);
            _logger?.Msg($"Speech backend switched: {_backendNameText}");
            return;
        }
    }

    private static void Adopt(IntPtr backend)
    {
        _backend = backend;
        _backendNameText = Utf8At(_backendName!(backend));
        _backendId = _registryId!(_context, ToUtf8(_backendNameText));
        _backendPriority = _registryPriority!(_context, _backendId);
        _lastProbeTick = Environment.TickCount;
    }

    private static void FreeBackend()
    {
        if (_backend == IntPtr.Zero)
        {
            return;
        }

        _backendFree!(_backend);
        _backend = IntPtr.Zero;
        _backendId = 0;
        _backendNameText = string.Empty;
    }

    private static string ErrorText(int error)
    {
        var text = Utf8At(_errorString!(error));
        return text.Length > 0 ? text : $"error {error}";
    }

    private static byte[] ToUtf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

    /// <summary>Reads a null-terminated UTF-8 string owned by native code.</summary>
    private static string Utf8At(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return string.Empty;
        }

        var length = 0;
        while (Marshal.ReadByte(pointer, length) != 0)
        {
            length++;
        }

        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }
}
