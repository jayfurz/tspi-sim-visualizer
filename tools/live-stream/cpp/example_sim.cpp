// example_sim.cpp — a toy Boost simulator streaming live state to the viewer.
//
//   cmake -S tools/live-stream/cpp -B build/live-stream && cmake --build build/live-stream
//   ./build/live-stream/tspi_example_sim --port 8787 [--rate 1] [--duration 60]
//   open web/viewer/index.html and connect to ws://localhost:8787/stream
//
// Stands in for real vehicle dynamics: two aircraft on curved paths and a
// missile that spawns mid-run (announced late, exactly as a real launch would
// be) and intercepts. Everything the viewer draws comes from the pushed
// records — the page never simulates.
#include "tspi_stream.hpp"

#include <chrono>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <string>
#include <thread>

namespace {

constexpr double kPi = 3.14159265358979323846;

// Attitude from a velocity vector: yaw/pitch into a quaternion (w,x,y,z),
// zero roll. Enough to point the viewer's dart down the flight path.
void quat_from_velocity(const double v[3], float q[4]) {
  const double yaw = std::atan2(v[1], v[0]);
  const double pitch = std::atan2(-v[2], std::hypot(v[0], v[1]));
  const double cy = std::cos(yaw * 0.5), sy = std::sin(yaw * 0.5);
  const double cp = std::cos(pitch * 0.5), sp = std::sin(pitch * 0.5);
  q[0] = static_cast<float>(cy * cp);
  q[1] = static_cast<float>(-sy * sp);
  q[2] = static_cast<float>(cy * sp);
  q[3] = static_cast<float>(sy * cp);
}

struct Track {
  double pos[3]{};
  double vel[3]{};
};

// Blue: a wide left-hand orbit. Red: an inbound run with a slow descent.
Track blue_at(double t) {
  const double r = 6000.0, w = 2 * kPi / 90.0;
  Track k;
  k.pos[0] = r * std::cos(w * t);
  k.pos[1] = r * std::sin(w * t);
  k.pos[2] = -6000.0 - 200.0 * std::sin(w * t * 2);
  k.vel[0] = -r * w * std::sin(w * t);
  k.vel[1] = r * w * std::cos(w * t);
  k.vel[2] = -400.0 * w * std::cos(w * t * 2);
  return k;
}

Track red_at(double t) {
  Track k;
  k.pos[0] = -22000.0 + 260.0 * t;
  k.pos[1] = 9000.0 - 40.0 * t;
  k.pos[2] = -9000.0 + 45.0 * t;
  k.vel[0] = 260.0;
  k.vel[1] = -40.0;
  k.vel[2] = 45.0;
  return k;
}

void fill(tspi::State& s, const Track& k) {
  for (int i = 0; i < 3; ++i) {
    s.pos[i] = k.pos[i];
    s.vel[i] = static_cast<float>(k.vel[i]);
  }
  quat_from_velocity(k.vel, s.quat);
}

double arg_num(int argc, char** argv, const char* name, double dflt) {
  for (int i = 1; i + 1 < argc; ++i)
    if (std::strcmp(argv[i], name) == 0) return std::atof(argv[i + 1]);
  return dflt;
}

}  // namespace

int main(int argc, char** argv) {
  const double rate = arg_num(argc, argv, "--rate", 1.0);
  const double duration = arg_num(argc, argv, "--duration", 90.0);
  const double hz = arg_num(argc, argv, "--hz", 50.0);
  const double dt = 1.0 / hz;

  tspi::StreamConfig cfg;
  cfg.port = static_cast<unsigned short>(arg_num(argc, argv, "--port", 8787));
  cfg.dt_ns = static_cast<std::int64_t>(dt * 1e9 + 0.5);
  cfg.epoch_unix_ns = std::chrono::duration_cast<std::chrono::nanoseconds>(
                          std::chrono::system_clock::now().time_since_epoch())
                          .count();
  cfg.origin_lat_deg = 36.2;
  cfg.origin_lon_deg = -115.0;
  cfg.name = "boost example sim";
  cfg.dynamics = "toy kinematics (example_sim.cpp)";
  cfg.extent_m = 25000;

  tspi::StreamServer stream(cfg);
  stream.start();
  std::cout << "streaming on ws://localhost:" << stream.port() << "/stream\n"
            << "open web/viewer/index.html and connect, or serve it and use\n"
            << "  ?ws=ws://localhost:" << stream.port() << "/stream\n";

  constexpr std::uint32_t kBlue = 0, kRed = 1, kSam = 2;
  stream.add_entity({kBlue, "blue-01", "blue", "aircraft", "f-16", -1, 0});
  stream.add_entity({kRed, "red-01", "red", "aircraft", "su-30", -1, 0});

  const double launch_t = 20.0, flight_t = 18.0;
  const std::int64_t launch_ns = static_cast<std::int64_t>(launch_t * 1e9);
  bool launched = false, intercepted = false;
  Track sam_start{};

  const auto t_start = std::chrono::steady_clock::now();
  std::uint32_t i = 0, sam_i = 0;
  for (double t = 0.0; t <= duration; t += dt, ++i) {
    tspi::State s{};
    fill(s, blue_at(t));
    stream.push(kBlue, i, s);

    const Track red = red_at(t);
    if (!intercepted) {
      fill(s, red);
      stream.push(kRed, i, s);
    }

    if (!launched && t >= launch_t) {
      launched = true;
      sam_start = blue_at(t);
      stream.add_entity({kSam, "blue-01-sam-1", "blue", "munition", "aim-120", kBlue, launch_ns});
      stream.event(launch_ns, "launch", kBlue, kRed);
    }
    if (launched && !intercepted) {
      // Straight-line run to the predicted intercept point, then a kill.
      const double u = (t - launch_t) / flight_t;
      const Track aim = red_at(launch_t + flight_t);
      Track k;
      for (int a = 0; a < 3; ++a) {
        k.pos[a] = sam_start.pos[a] + (aim.pos[a] - sam_start.pos[a]) * u;
        k.vel[a] = (aim.pos[a] - sam_start.pos[a]) / flight_t;
      }
      fill(s, k);
      stream.push(kSam, sam_i++, s);
      if (u >= 1.0) {
        intercepted = true;
        stream.event(static_cast<std::int64_t>(t * 1e9), "intercept", kSam, kRed, "{\"miss_m\":2.4}");
      }
    }

    stream.flush();

    // Pace to wall clock so the viewer sees a real-time feed.
    const auto due = t_start + std::chrono::duration<double>((t + dt) / rate);
    std::this_thread::sleep_until(due);
  }

  stream.end();
  std::cout << "run complete (" << duration << " s, " << i << " ticks)\n";
  std::this_thread::sleep_for(std::chrono::milliseconds(200));  // let the last frames drain
  stream.stop();
  return 0;
}
