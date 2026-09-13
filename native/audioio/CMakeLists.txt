cmake_minimum_required(VERSION 3.16)
project(audioio C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

add_library(audioio SHARED audioio.c)

target_include_directories(audioio PUBLIC ${CMAKE_CURRENT_SOURCE_DIR})

# Hidden by default so the shared object exports only the entry points marked
# in the header, the same rule audiocpp's C ABI follows.
set_target_properties(audioio PROPERTIES
    C_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
    POSITION_INDEPENDENT_CODE ON)

if (UNIX AND NOT APPLE)
    # miniaudio dlopen()s its backends, so there is nothing to link against for
    # ALSA, PulseAudio or JACK -- which is why this needs no -dev packages and
    # runs on a machine that has any one of them.
    target_link_libraries(audioio PRIVATE dl pthread m)
elseif (APPLE)
    target_link_libraries(audioio PRIVATE
        "-framework CoreFoundation" "-framework CoreAudio" "-framework AudioToolbox")
endif()

install(TARGETS audioio RUNTIME DESTINATION bin LIBRARY DESTINATION lib)
