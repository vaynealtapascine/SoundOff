# Standalone rebuild: extract the APK assets into a directory, copy this file to
# CMakeLists.txt, and configure with the Android NDK toolchain, ANDROID_ABI=arm64-v8a
# (or x86_64), ANDROID_PLATFORM=android-26, and CMAKE_BUILD_TYPE=Release.
cmake_minimum_required(VERSION 3.22)
if(CMAKE_CURRENT_LIST_FILE STREQUAL "${CMAKE_SOURCE_DIR}/CMakeLists.txt")
    project(soundoff_lame LANGUAGES C)
endif()
include(FetchContent)
FetchContent_Declare(lame URL "${CMAKE_CURRENT_LIST_DIR}/lame-source.tar.gz"
    URL_HASH SHA256=0d97ba0776988f5730e0ce1fa936e7c5d6836c757993ba55073513fe89cf410a)
FetchContent_MakeAvailable(lame)
file(GLOB LAME_SOURCES "${lame_SOURCE_DIR}/libmp3lame/*.c")
add_library(mp3lame SHARED ${LAME_SOURCES})
target_include_directories(mp3lame PUBLIC "${lame_SOURCE_DIR}/include" PRIVATE "${lame_SOURCE_DIR}/libmp3lame")
target_compile_definitions(mp3lame PRIVATE STDC_HEADERS=1 HAVE_ERRNO_H=1 HAVE_FCNTL_H=1 HAVE_STDINT_H=1
    HAVE_STRCHR=1 HAVE_MEMCPY=1 HAVE_STRERROR=1 HAVE_LONG_DOUBLE=1 SIZEOF_INT=4 SIZEOF_LONG=8 ieee754_float32_t=float)
target_compile_options(mp3lame PRIVATE -O3)
target_link_libraries(mp3lame m)
target_link_options(mp3lame PRIVATE "-Wl,-z,max-page-size=16384")
