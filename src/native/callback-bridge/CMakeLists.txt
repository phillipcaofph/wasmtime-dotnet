cmake_minimum_required(VERSION 3.16)
project(wasmtime_callback_bridge C)
find_package(Threads REQUIRED)
add_library(wasmtime_callback_bridge SHARED bridge.c)
function(configure_bridge_target target)
  target_compile_features(${target} PRIVATE c_std_11)
  if(MSVC)
    # C11 atomics need Visual Studio 2022 17.5 or later.
    target_compile_options(${target} PRIVATE /std:c11 /experimental:c11atomics /W4 /WX)
  else()
    target_compile_options(${target} PRIVATE -Wall -Wextra -Werror -fvisibility=hidden)
  endif()
  target_link_libraries(${target} PRIVATE Threads::Threads)
endfunction()
configure_bridge_target(wasmtime_callback_bridge)
# A fixed output directory (no per-configuration subdirectory) for MSBuild and CI.
set_target_properties(wasmtime_callback_bridge PROPERTIES
  LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out$<0:>"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out$<0:>")

option(CALLBACK_BRIDGE_BUILD_TESTS "Build native worker fault-injection tests" OFF)
if(CALLBACK_BRIDGE_BUILD_TESTS)
  enable_testing()
  add_executable(callback_bridge_worker_tests tests/worker-start.c)
  configure_bridge_target(callback_bridge_worker_tests)
  foreach(scenario invalid transient retry-initialization permanent finalizer
                   pool-limit growth-retry growth-refused reuse-during-backoff)
    add_test(NAME worker-${scenario} COMMAND callback_bridge_worker_tests ${scenario})
    set_tests_properties(worker-${scenario} PROPERTIES TIMEOUT 15)
  endforeach()
endif()
