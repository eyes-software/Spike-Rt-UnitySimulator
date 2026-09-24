cmake_minimum_required(VERSION 3.16)
project(etrobo_sim_controller C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

#
# 実行ファイルは構成によらず controller/bin へ出す。
#
# 理由は 2 つ。
#  - Release / Debug でパスが変わらないので、README もスクリプトも 1 か所を指せる。
#  - セキュリティソフトの除外登録が 1 フォルダで済む。この制御プロセスは
#    「署名なし・毎回ビルドし直す・UDP ソケットを開く」という、ヒューリスティックに
#    引っかかりやすい性質を全部持っている。ブロックされた場合は bin/ を除外に入れる。
#
# -DETROBO_BIN_DIR=<path> で出力先を変えられる（検証や CI 用）。
set(ETROBO_BIN_DIR "${CMAKE_CURRENT_SOURCE_DIR}/bin" CACHE PATH "実行ファイルの出力先")
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY         ${ETROBO_BIN_DIR})
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_RELEASE ${ETROBO_BIN_DIR})
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY_DEBUG   ${ETROBO_BIN_DIR})

#
# 走行プログラムの選択。
#
#   -DETROBO_APP=sample    同梱のサンプル (既定)
#   -DETROBO_APP=eyes2026  EYES ETロボコン 2026 (別リポジトリ)
#                          -DEYES2026_DIR=<eyes-et2026 のパス> で場所を指定する
#
set(ETROBO_APP "sample" CACHE STRING "走行プログラム (sample / eyes2026)")
set(EYES2026_DIR "" CACHE PATH "eyes-et2026 を置いた場所")

if(ETROBO_APP STREQUAL "eyes2026")
    if(EYES2026_DIR STREQUAL "" OR NOT EXISTS "${EYES2026_DIR}/app.cpp")
        message(FATAL_ERROR
            "-DEYES2026_DIR に eyes-et2026 のパスを指定してください (app.cpp がある場所)")
    endif()

    #
    # ソースの一覧は Makefile.inc の APPL_CXXOBJS から読む。
    #
    # 直書きしていると、向こうでファイルが増えるたびにここが古くなる
    # （実際 ETSumo ブランチで 3 本増えて未定義参照になった）。
    # 走行プログラムには手を入れない方針なので、一覧も向こうを正とする。
    #
    set(_mk "${EYES2026_DIR}/Makefile.inc")
    if(NOT EXISTS "${_mk}")
        message(FATAL_ERROR "Makefile.inc が見つかりません: ${_mk}")
    endif()

    file(READ "${_mk}" _mk_text)
    string(REPLACE "\r" "" _mk_text "${_mk_text}")
    string(REPLACE "\\\n" " " _mk_text "${_mk_text}")   # 行末 '\' の継続を畳む

    set(EYES2026_UNITS "")
    string(REGEX MATCHALL "APPL_CXXOBJS[^\n]*" _mk_lines "${_mk_text}")
    foreach(_line ${_mk_lines})
        string(REGEX MATCHALL "[A-Za-z0-9_]+\\.o" _objs "${_line}")
        foreach(_o ${_objs})
            string(REGEX REPLACE "\\.o$" "" _o "${_o}")
            list(APPEND EYES2026_UNITS "${_o}")
        endforeach()
    endforeach()

    list(REMOVE_DUPLICATES EYES2026_UNITS)
    list(LENGTH EYES2026_UNITS _unit_count)
    if(_unit_count EQUAL 0)
        message(FATAL_ERROR "Makefile.inc から APPL_CXXOBJS を読めませんでした")
    endif()
    message(STATUS "走行プログラムのユニット: ${_unit_count} 本 (Makefile.inc より)")

    set(ETROBO_APP_SOURCES
        "${EYES2026_DIR}/app.cpp"
        "${EYES2026_DIR}/util.cpp"
        apps/eyes2026/etsim_app_config.c
        # C++ API のうち、ヘッダにインラインで書かれていない分の実装。
        # 上流 (etrobocon/spike-rt-RasPike-ART) の drivers/libcpp から取ってある。
        src/libcpp/spikeapi_Button.cpp
        src/libcpp/spikeapi_Clock.cpp
        src/libcpp/spikeapi_IMU.cpp
        src/libcpp/spikeapi_Light.cpp)
    foreach(unit ${EYES2026_UNITS})
        list(APPEND ETROBO_APP_SOURCES "${EYES2026_DIR}/app/${unit}.cpp")
    endforeach()

    set(ETROBO_APP_INCLUDES
        apps/eyes2026
        "${EYES2026_DIR}"
        "${EYES2026_DIR}/app"
        include/libcpp/spike)

    # ReadScene.cpp が Scenario.csv を #include している。作業ディレクトリに要る
    # TmpScenario.csv は実行時に自分で作る。

    #
    # 走行プログラムには手を入れない方針なので、gcc と MSVC の差はここで吸収する。
    #   _USE_MATH_DEFINES : MSVC は <cmath> だけでは M_PI を出さない
    #   /FIstdint.h       : gcc では他のヘッダ経由で入る int32_t が MSVC では入らない
    #
    set(ETROBO_APP_DEFINES _USE_MATH_DEFINES)
    if(MSVC)
        set(ETROBO_APP_OPTIONS /FIstdint.h)
    endif()
else()
    set(ETROBO_APP_SOURCES app/etrobo_app.c)
    set(ETROBO_APP_INCLUDES "")
    set(ETROBO_APP_DEFINES "")
    set(ETROBO_APP_OPTIONS "")
endif()

message(STATUS "走行プログラム: ${ETROBO_APP}")

add_executable(etrobo_app
    src/main.c
    src/etsim_platform.c
    src/etsim_runtime.c
    src/etsim_device.c
    src/asp3_shim.c
    src/etsim_serial.c
    ${ETROBO_APP_SOURCES}
)

target_include_directories(etrobo_app PRIVATE
    include
    ${CMAKE_CURRENT_SOURCE_DIR}/../protocol
    ${ETROBO_APP_INCLUDES}
)

if(MSVC)
    # MSVC に無い POSIX ヘッダ (unistd.h) の穴埋め
    target_include_directories(etrobo_app PRIVATE include/msvc)
endif()

target_compile_definitions(etrobo_app PRIVATE ETROBO_ON_SIMULATOR=1 ${ETROBO_APP_DEFINES})
if(ETROBO_APP_OPTIONS)
    target_compile_options(etrobo_app PRIVATE ${ETROBO_APP_OPTIONS})
endif()

if(MSVC)
    # ソースが UTF-8 なので明示しないと日本語のコメント・文字列が壊れる
    target_compile_options(etrobo_app PRIVATE /utf-8 /W3)
    target_compile_definitions(etrobo_app PRIVATE _CRT_SECURE_NO_WARNINGS _WINSOCK_DEPRECATED_NO_WARNINGS)
    target_link_libraries(etrobo_app PRIVATE ws2_32 winmm)
elseif(WIN32)
    target_compile_options(etrobo_app PRIVATE -Wall -Wextra)
    target_link_libraries(etrobo_app PRIVATE ws2_32 winmm)
else()
    target_compile_options(etrobo_app PRIVATE -Wall -Wextra)
    find_package(Threads REQUIRED)
    target_link_libraries(etrobo_app PRIVATE Threads::Threads m)
endif()

# プロトコル構造体のサイズが C# 側と一致しているかを確認する小さなテスト
add_executable(protocol_check tests/protocol_check.c)
target_include_directories(protocol_check PRIVATE
    include
    ${CMAKE_CURRENT_SOURCE_DIR}/../protocol
)
if(MSVC)
    target_compile_options(protocol_check PRIVATE /utf-8)
endif()

enable_testing()
add_test(NAME protocol_layout COMMAND protocol_check)
