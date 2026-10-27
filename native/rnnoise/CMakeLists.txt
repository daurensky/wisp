cmake_minimum_required(VERSION 3.24)
project(wisp_rnnoise VERSION 0.2 LANGUAGES C)

# Official v0.2 release (tag commit 904a876dce1f9ab8860c0a5000ed151f9f6eef58).
# Unlike a Git source archive this includes the default trained model and tables.
include(FetchContent)
FetchContent_Declare(rnnoise_source
    URL https://github.com/xiph/rnnoise/releases/download/v0.2/rnnoise-0.2.tar.gz
    URL_HASH SHA256=90fce4b00b9ff24c08dbfe31b82ffd43bae383d85c5535676d28b0a2b11c0d37
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_MakeAvailable(rnnoise_source)
set(RNN_SRC "${rnnoise_source_SOURCE_DIR}/src")

# Same baseline sources as the upstream Makefile.am; no training tools, external
# model downloads or AVX requirement. SSE2 is part of the Windows x64 baseline.
add_library(rnnoise SHARED
    ${RNN_SRC}/denoise.c ${RNN_SRC}/rnn.c ${RNN_SRC}/pitch.c
    ${RNN_SRC}/kiss_fft.c ${RNN_SRC}/celt_lpc.c ${RNN_SRC}/nnet.c
    ${RNN_SRC}/nnet_default.c ${RNN_SRC}/parse_lpcnet_weights.c
    ${RNN_SRC}/rnnoise_data.c ${RNN_SRC}/rnnoise_tables.c)
target_include_directories(rnnoise PRIVATE "${rnnoise_source_SOURCE_DIR}/include" "${RNN_SRC}")
target_compile_definitions(rnnoise PRIVATE RNNOISE_BUILD DLL_EXPORT OPUS_X86_MAY_HAVE_SSE2)
set_target_properties(rnnoise PROPERTIES C_STANDARD 99 C_STANDARD_REQUIRED TRUE)
if(MSVC)
    # Static CRT keeps the portable DLL independent of a VC++ runtime installer.
    set_property(TARGET rnnoise PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    target_compile_definitions(rnnoise PRIVATE _CRT_SECURE_NO_WARNINGS)
    target_compile_options(rnnoise PRIVATE /O2)
else()
    target_compile_options(rnnoise PRIVATE -O3)
    target_link_libraries(rnnoise PRIVATE m)
endif()
install(TARGETS rnnoise RUNTIME DESTINATION . LIBRARY DESTINATION .)
