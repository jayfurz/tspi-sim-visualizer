# Header-only producer + example. Boost.Beast/Asio need no compiled Boost libs.
cmake_minimum_required(VERSION 3.16)
project(tspi_live_stream CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(Boost REQUIRED)
find_package(Threads REQUIRED)

add_library(tspi_stream INTERFACE)
target_include_directories(tspi_stream INTERFACE ${CMAKE_CURRENT_SOURCE_DIR} ${Boost_INCLUDE_DIRS})
target_link_libraries(tspi_stream INTERFACE Threads::Threads)

add_executable(tspi_example_sim example_sim.cpp)
target_link_libraries(tspi_example_sim PRIVATE tspi_stream)
