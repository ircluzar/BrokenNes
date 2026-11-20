# [cer64plus] Builds cor64_prdp.dll: parallel-rdp (vendored in ../parallel-rdp-standalone, MIT) + a small C API.
cmake_minimum_required(VERSION 3.16)
project(cor64_prdp CXX C)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

set(P ${CMAKE_CURRENT_SOURCE_DIR}/../parallel-rdp-standalone)
file(GLOB RDP_SOURCES ${P}/parallel-rdp/*.cpp)
set(VK_SOURCES
    buffer.cpp buffer_pool.cpp command_buffer.cpp command_pool.cpp context.cpp cookie.cpp descriptor_set.cpp device.cpp
    event_manager.cpp fence.cpp fence_manager.cpp image.cpp indirect_layout.cpp memory_allocator.cpp pipeline_event.cpp
    query_pool.cpp render_pass.cpp sampler.cpp semaphore.cpp semaphore_manager.cpp shader.cpp texture/texture_format.cpp)
list(TRANSFORM VK_SOURCES PREPEND ${P}/vulkan/)
set(UTIL_SOURCES arena_allocator.cpp logging.cpp thread_id.cpp aligned_alloc.cpp timer.cpp timeline_trace_file.cpp
    environment.cpp thread_name.cpp)
list(TRANSFORM UTIL_SOURCES PREPEND ${P}/util/)

add_library(cor64_prdp SHARED cor64_prdp.cpp ${RDP_SOURCES} ${VK_SOURCES} ${UTIL_SOURCES} ${P}/volk/volk.c)
target_include_directories(cor64_prdp PRIVATE ${P}/parallel-rdp ${P}/volk ${P}/vulkan ${P}/vulkan-headers/include ${P}/util)
target_compile_definitions(cor64_prdp PRIVATE VK_USE_PLATFORM_WIN32_KHR NOMINMAX WIN32_LEAN_AND_MEAN _CRT_SECURE_NO_WARNINGS)
if (MSVC)
  target_compile_options(cor64_prdp PRIVATE /O2 /W0 /bigobj)
endif()
target_link_libraries(cor64_prdp PRIVATE winmm)
