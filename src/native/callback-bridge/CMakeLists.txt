cmake_minimum_required(VERSION 3.16)
project(wasmtime_callback_bridge C)
find_package(Threads REQUIRED)
add_library(wasmtime_callback_bridge SHARED bridge.c)
target_compile_features(wasmtime_callback_bridge PRIVATE c_std_11)
if(MSVC)
  # C11 atomics need Visual Studio 2022 17.5 or later.
  target_compile_options(wasmtime_callback_bridge PRIVATE /std:c11 /experimental:c11atomics /W4 /WX)
else()
  target_compile_options(wasmtime_callback_bridge PRIVATE -Wall -Wextra -Werror -fvisibility=hidden)
endif()
target_link_libraries(wasmtime_callback_bridge PRIVATE Threads::Threads)
# A fixed output directory (no per-configuration subdirectory) for MSBuild and CI.
set_target_properties(wasmtime_callback_bridge PROPERTIES
  LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out$<0:>"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out$<0:>")
