using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using MelonLoader;
using Prism;
using Prism.Primitives;
using NativeLibrary = System.Runtime.InteropServices.NativeLibrary;

namespace Killer_Frequency_Access;

/// <summary>
/// Speech and braille through Prism (github.com/ethindp/prism, MPL-2.0, v0.18.4 in native\) by way of the
/// Prism.Net bindings (third_party\Prism.Net, compiled into the mod). Prism drives NVDA, JAWS, ZDSR,
/// PC-Talker, ZoomText, System Access and Narrator, and falls back to the OS voice (SAPI/OneCore) when no
/// screen reader runs.
///
/// - Loading: the bindings P/Invoke "prism"; a DllImport resolver on this assembly loads the prism.dll
///   next to the mod in Mods\, which the default search would not find.
/// - Backend: the best one Prism can create. Prism's availability polling (a background thread, every
///   second, a change confirmed on two samples) reports a screen reader starting or stopping; the next
///   line spoken re-selects the best backend, so NVDA started after the game takes over.
/// - Output: Prism's output (speech and braille). Lines queue in the order the mod speaks them, unless
///   a caller asks to interrupt, which cuts off whatever is speaking.
/// </summary>
internal static class SpeechRouter
{
    private const string NativeName = "prism";

    private static MelonLogger.Instance? _logger;
    private static string _nativePath = string.Empty;
    private static Context? _context;
    private static Backend? _backend;
    private static int _availabilityChanged;
    private static bool _loggedOutputError;
    private static readonly ConcurrentQueue<string> AvailabilityLog = new();

    public static void Initialize(MelonLogger.Instance logger)
    {
        _logger = logger;
        try
        {
            var directory = Path.GetDirectoryName(typeof(SpeechRouter).Assembly.Location) ?? string.Empty;
            _nativePath = Path.Combine(directory, "prism.dll");
            if (!File.Exists(_nativePath))
            {
                logger.Error($"Prism: {_nativePath} is missing; speech is disabled.");
                return;
            }

            NativeLibrary.SetDllImportResolver(typeof(SpeechRouter).Assembly, ResolveNative);

            var config = PrismConfig.Create();
            config.AvailabilityCallback = OnAvailabilityChanged;
            _context = new Context(config);
            logger.Msg($"Prism {PrismInfo.VersionString} loaded from {_nativePath}");
            SelectBest();
        }
        catch (Exception ex)
        {
            logger.Error($"Prism: initialization failed: {ex}");
            _context = null;
        }
    }

    public static void Speak(string text, bool interrupt = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // Every spoken line goes to the log, so a test run shows exactly what the player heard.
        _logger?.Msg(interrupt ? $"[speech] (interrupt) {text}" : $"[speech] {text}");
        if (_context == null)
        {
            return;
        }

        try
        {
            ApplyAvailabilityChanges();
            if (_backend == null && !SelectBest())
            {
                return;
            }

            try
            {
                _backend!.Output(text, interrupt);
                _loggedOutputError = false;
            }
            catch (Exception ex)
            {
                if (!_loggedOutputError)
                {
                    _loggedOutputError = true;
                    _logger?.Warning($"Prism: {_backend?.Name} output failed ({ex.Message}); re-selecting backend.");
                }

                if (SelectBest())
                {
                    _backend!.Output(text, interrupt);
                }
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
            _backend?.Dispose();
            _backend = null;
            _context?.Dispose();
            _context = null;
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Prism: shutdown failed: {ex.Message}");
        }
    }

    private static IntPtr ResolveNative(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        return libraryName == NativeName ? NativeLibrary.Load(_nativePath) : IntPtr.Zero;
    }

    /// <summary>Runs on Prism's poll thread: only records the change for the main thread.</summary>
    private static void OnAvailabilityChanged(BackendId id, string name, bool available)
    {
        AvailabilityLog.Enqueue($"Prism: {name} {(available ? "became available" : "is no longer available")}");
        Interlocked.Exchange(ref _availabilityChanged, 1);
    }

    /// <summary>After a screen reader starts or stops, switches to the best backend if that changed it.</summary>
    private static void ApplyAvailabilityChanges()
    {
        while (AvailabilityLog.TryDequeue(out var line))
        {
            _logger?.Msg(line);
        }

        if (Interlocked.Exchange(ref _availabilityChanged, 0) == 0)
        {
            return;
        }

        Backend candidate;
        try
        {
            candidate = _context!.CreateBest();
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Prism: no backend after an availability change ({ex.Message}); keeping {_backend?.Name}.");
            return;
        }

        if (_backend != null && candidate.Name == _backend.Name)
        {
            candidate.Dispose();
            return;
        }

        var previous = _backend?.Name ?? "none";
        _backend?.Dispose();
        _backend = candidate;
        _logger?.Msg($"Speech backend switched: {previous} -> {_backend.Name}");
    }

    /// <summary>Replaces the active backend with the best one Prism can create.</summary>
    private static bool SelectBest()
    {
        _backend?.Dispose();
        _backend = null;
        try
        {
            _backend = _context!.CreateBest();
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Prism: no speech backend available, no screen reader and no OS voice ({ex.Message}).");
            return false;
        }

        _logger?.Msg($"Speech backend: {_backend.Name}");
        return true;
    }
}
