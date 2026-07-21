cmake_minimum_required(VERSION 3.24)
project(ai_copilot_bridge LANGUAGES CXX)

if(NOT WIN32)
    message(FATAL_ERROR "The Arma client extension is Windows-only.")
endif()

if(NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
    message(FATAL_ERROR "Version 0.1.0 builds only the x64 Arma extension.")
endif()

add_library(ai_copilot_bridge SHARED ArmaBridge.cpp)
target_compile_features(ai_copilot_bridge PRIVATE cxx_std_20)
target_compile_definitions(ai_copilot_bridge PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
set_target_properties(ai_copilot_bridge PROPERTIES
    OUTPUT_NAME "ai_copilot_bridge_x64"
    PREFIX ""
)

if(MSVC)
    target_compile_options(ai_copilot_bridge PRIVATE /W4 /permissive- /EHsc)
endif()
