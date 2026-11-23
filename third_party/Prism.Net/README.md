# Prism.Net (vendored)

.NET bindings for Prism, from https://github.com/qt06/prism.net, commit
71b7eb6c791b26e30b7341256ecd766a49a2f7d8 (2026-11-19), version 0.18.4. MPL-2.0 (`LICENSE`, `NOTICE`).

The files under `src/Prism` are copied here unmodified and compiled into the mod, so it ships as one DLL.
Its `Prism.csproj` is left out. The bindings P/Invoke `prism`; the mod (`KillerFrequencyAccessMod.Speech.cs`) points that name at the
`prism.dll` next to the mod in `Mods\` (`native\win-x64`, Prism 0.18.4 from github.com/ethindp/prism).

To update: copy `src/Prism` over these files again, keeping this README, and match the native DLL to the
version the bindings target (`PrismNativeLib.PRISM_CONFIG_VERSION` against `prism.h`).
