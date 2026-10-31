cmake_minimum_required(VERSION 3.20)
project(arib_t98_analysis_native LANGUAGES C)
add_library(arib_t98_analysis_native SHARED arib_t98_analysis.c)
target_include_directories(arib_t98_analysis_native PRIVATE vendor/mbelib-neo/src/internal)
set_target_properties(arib_t98_analysis_native PROPERTIES C_STANDARD 99 C_STANDARD_REQUIRED ON)
add_library(arib_t98_fec_native SHARED arib_t98_fec.c
    vendor/mbelib-neo/src/ambe/ambe_common.c
    vendor/mbelib-neo/src/ecc/ecc.c
    vendor/mbelib-neo/src/ecc/ecc_const.c)
target_include_directories(arib_t98_fec_native PRIVATE
    vendor/mbelib-neo/include vendor/mbelib-neo/src/internal)
target_compile_definitions(arib_t98_fec_native PRIVATE MBE_STATIC _USE_MATH_DEFINES)
set_target_properties(arib_t98_fec_native PROPERTIES C_STANDARD 99 C_STANDARD_REQUIRED ON)

# Only FEC and parameter analysis are built. No software speech synthesizer.
include(CheckIPOSupported)
check_ipo_supported(RESULT ARIB_T98_IPO_SUPPORTED OUTPUT ARIB_T98_IPO_ERROR)
if(ARIB_T98_IPO_SUPPORTED)
    set_property(TARGET arib_t98_fec_native PROPERTY INTERPROCEDURAL_OPTIMIZATION_RELEASE TRUE)
    set_property(TARGET arib_t98_analysis_native PROPERTY INTERPROCEDURAL_OPTIMIZATION_RELEASE TRUE)
else()
    message(STATUS "ARIB T98 IPO/LTO not supported: ${ARIB_T98_IPO_ERROR}")
endif()
