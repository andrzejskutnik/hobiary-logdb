cmake_minimum_required(VERSION 3.20)
project(hobiarylogdb VERSION 0.1.0 LANGUAGES C)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

if(MSVC)
    add_compile_options(/W4 /WX)
else()
    add_compile_options(-Wall -Wextra -Werror -pedantic)
endif()

# ── Shared library (P/Invoke z C#) ──────────────────────────────────────────
add_library(hobiarylogdb SHARED
    src/segment/hlog_segment.c
    src/index/hlog_index.c
    src/log/hlog.c
)

target_include_directories(hobiarylogdb PUBLIC include)

# ── Usuń prefix "lib" na Windows (MinGW domyślnie dodaje "lib") ──────────────
if(WIN32)
    set_target_properties(hobiarylogdb PROPERTIES PREFIX "")
endif()

# ── Eksport symboli na Windows ───────────────────────────────────────────────
if(WIN32)
    target_compile_definitions(hobiarylogdb PRIVATE
        HOBIARYLOGDB_EXPORTS
        _CRT_SECURE_NO_WARNINGS
    )
endif()

# ── Kopiowanie DLL do katalogu testów po buildzie ────────────────────────────
set(TEST_OUTPUT_DIR "${CMAKE_SOURCE_DIR}/tests/HobiaryLogDB.Tests/bin/Debug/net10.0")

add_custom_command(TARGET hobiarylogdb POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${TEST_OUTPUT_DIR}"
    COMMAND ${CMAKE_COMMAND} -E copy "$<TARGET_FILE:hobiarylogdb>" "${TEST_OUTPUT_DIR}"
    COMMENT "Kopiowanie $<TARGET_FILE_NAME:hobiarylogdb> → tests/HobiaryLogDB.Tests/bin/Debug/net10.0/"
)
