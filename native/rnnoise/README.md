# RNNoise for Wisp

RNNoise **0.2**, Xiph.Org upstream tag commit
`904a876dce1f9ab8860c0a5000ed151f9f6eef58`.
Source: <https://github.com/xiph/rnnoise/releases/download/v0.2/rnnoise-0.2.tar.gz>.
SHA-256: `90fce4b00b9ff24c08dbfe31b82ffd43bae383d85c5535676d28b0a2b11c0d37`.
The official release includes the trained default model and generated tables;
no unpinned model is downloaded during the build. The source archive is not a signed binary.
Our DLL is built by GitHub Actions, not downloaded from a third-party NuGet.

NuGet candidates RNNoiseSharp 1.0.0 (last published 2022) and
YellowDogMan.RRNoise.NET 0.1.9 were considered. Neither establishes the complete
upstream version / build provenance of a signed Windows binary from the package
listing. Wisp therefore uses a source build instead of depending on these binaries.

API verified against this release's `include/rnnoise.h` and `examples/rnnoise_demo.c`:
`int rnnoise_get_frame_size(void)` (480), `DenoiseState *rnnoise_create(RNNModel*)`,
`int rnnoise_init(DenoiseState*, RNNModel*)`, `void rnnoise_destroy(DenoiseState*)`,
`float rnnoise_process_frame(DenoiseState*, float*, const float*)`, C calling
convention. A null model selects the compiled default. Samples use PCM16 scale.
`rnnoise_init(state, NULL)` clears history in place without allocating a new state.

On Windows x64, with CMake 3.24+ and Visual Studio C++ build tools:

```powershell
cmake -S native/rnnoise -B artifacts/rnnoise-build -A x64 -DCMAKE_INSTALL_PREFIX="$PWD/artifacts/native"
cmake --build artifacts/rnnoise-build --config Release
cmake --install artifacts/rnnoise-build --config Release
dotnet test tests/Wisp.Desktop.Tests -c Release
dotnet publish src/Wisp.Desktop -c Release -r win-x64 --self-contained true -p:RequireRnNoise=true -o artifacts/Wisp-win-x64
```

Baseline SSE2, no mandatory AVX/AVX2; static MSVC runtime. The CMake source list
comes from upstream `Makefile.am`. The DLL and BSD-3-Clause license are copied
into build/test/publish output. CI requires native tests to run and refuses a ZIP
without the DLL/license. Local source builds without the DLL still compile and
run with one notification and raw microphone fallback; native-only tests skip.
Loading uses the absolute application path with DLL-directory/system32 dependency
search, never the current directory or PATH. The successfully loaded module lives
until process exit so SafeHandle finalizers remain valid.
