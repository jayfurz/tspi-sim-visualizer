// tspi_stream.hpp — push live simulation state to the tspi web viewer.
//
// Header-only, Boost.Beast (header-only) + Boost.Asio. Drop it into an existing
// C++/Boost simulator, construct a tspi::StreamServer, announce entities, and
// call push()/flush() once per integration tick:
//
//     tspi::StreamConfig cfg;
//     cfg.port = 8787;
//     cfg.dt_ns = 20'000'000;                 // 50 Hz — must match the tick rate
//     tspi::StreamServer stream(cfg);
//     stream.start();
//     stream.add_entity({.ord = 0, .id = "blue-01", .team = "blue", .type = "aircraft"});
//     for (std::uint32_t i = 0; ; ++i) {
//       ...integrate...
//       stream.push(0, i, tspi::State{...});   // NED metres, quat wxyz, body rates
//       stream.flush();                        // one batch per tick
//     }
//
// The bytes on the wire are the .tspi format's own 64-byte layout-1 records, so
// the viewer's interpolation (Hermite position, slerped attitude) is the exact
// same code path it uses for recorded files — see tools/live-stream/PROTOCOL.md.
//
// Threading: start() runs the io_context on its own thread; add_entity/push/
// flush/event/end are safe to call from the simulation thread.
//
// Late joiners get the entity roster in the hello and then records from the
// current tick onward — no history backfill (the viewer draws the trail from
// where the viewer joined). Record a .tspi alongside if you need the full run.
#pragma once

#include <boost/asio.hpp>
#include <boost/beast/core.hpp>
#include <boost/beast/websocket.hpp>

#include <atomic>
#include <cstdint>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#if defined(__BYTE_ORDER__) && __BYTE_ORDER__ != __ORDER_LITTLE_ENDIAN__
#error "tspi_stream: the .tspi record layout is little-endian; add byte swaps for this target"
#endif

namespace tspi {

namespace net = boost::asio;
namespace beast = boost::beast;
namespace websocket = boost::beast::websocket;
using tcp = boost::asio::ip::tcp;

/// One 6-DoF sample: NED metres/metres-per-second, attitude quaternion (w,x,y,z)
/// body->NED, body rates rad/s. Mirrors record layout 1 of docs/FORMAT.md.
struct State {
  double pos[3]{};                  // north, east, down (m)
  float vel[3]{};                   // north, east, down (m/s) — Hermite tangents
  float quat[4]{1.f, 0.f, 0.f, 0.f};// w, x, y, z
  float omega[3]{};                 // p, q, r (rad/s)
};

struct EntityDesc {
  std::uint32_t ord{};              // stable wire id, also the event src/dst key
  std::string id;                   // display name, e.g. "blue-01"
  std::string team{"white"};        // blue | red | white
  std::string type{"aircraft"};     // aircraft | ship | munition
  std::string model{"live"};
  std::int64_t parent{-1};          // launching entity's ord, or -1
  std::int64_t t0_ns{0};            // sim time of this entity's first sample
};

struct StreamConfig {
  unsigned short port{8787};
  std::int64_t dt_ns{20000000};     // sample interval; times are t0 + i*dt_ns
  std::int64_t epoch_unix_ns{0};    // wall clock of sim t=0
  double origin_lat_deg{0}, origin_lon_deg{0}, origin_alt_m{0};
  std::string name{"live sim"};
  std::string dynamics{"live stream (producer-authoritative)"};
  double extent_m{20000};           // initial ground-grid size hint
};

namespace detail {

inline void put_u32(std::string& s, std::uint32_t v) {
  char b[4];
  std::memcpy(b, &v, 4);
  s.append(b, 4);
}

inline void put_record(std::string& s, const State& st) {
  char b[64];
  std::memcpy(b, st.pos, 24);
  std::memcpy(b + 24, st.vel, 12);
  std::memcpy(b + 36, st.quat, 16);
  std::memcpy(b + 52, st.omega, 12);
  s.append(b, 64);
}

inline std::string json_escape(const std::string& in) {
  std::string out;
  out.reserve(in.size() + 2);
  for (char c : in) {
    switch (c) {
      case '"': out += "\\\""; break;
      case '\\': out += "\\\\"; break;
      case '\n': out += "\\n"; break;
      case '\r': out += "\\r"; break;
      case '\t': out += "\\t"; break;
      default:
        if (static_cast<unsigned char>(c) < 0x20) out += ' ';
        else out += c;
    }
  }
  return out;
}

inline std::string json_kv(const std::string& k, const std::string& v) {
  return "\"" + k + "\":\"" + json_escape(v) + "\"";
}

inline std::string entity_json(const EntityDesc& e) {
  std::string s = "{\"ord\":" + std::to_string(e.ord) + ",";
  s += json_kv("id", e.id) + "," + json_kv("team", e.team) + ",";
  s += json_kv("type", e.type) + "," + json_kv("model", e.model) + ",";
  s += "\"parent\":" + (e.parent < 0 ? std::string("null") : std::to_string(e.parent)) + ",";
  s += "\"t0_ns\":" + std::to_string(e.t0_ns) + ",\"layout\":1}";
  return s;
}

struct Message {
  bool binary{};
  std::string payload;
};
using MessagePtr = std::shared_ptr<const Message>;

/// One connected viewer. Everything here runs on the io_context thread.
///
/// A session accepts messages from the moment it is created, before the
/// WebSocket handshake finishes — a busy producer broadcasts during the
/// handshake, and those records must queue rather than be lost (or, worse, make
/// the server treat the session as dead).
class Session : public std::enable_shared_from_this<Session> {
 public:
  /// Queued messages before a slow viewer is considered hopeless. Overflow drops
  /// the oldest *record* batches (never control messages); the viewer's
  /// gap-padding keeps its clock exact, so a slow link degrades to a coarser
  /// trail instead of unbounded memory here.
  static constexpr std::size_t kMaxQueue = 2048;

  explicit Session(tcp::socket sock) : ws_(std::move(sock)) {}

  void start(MessagePtr hello) {
    auto self = shared_from_this();
    ws_.set_option(websocket::stream_base::timeout::suggested(beast::role_type::server));
    queue_.push_front(std::move(hello));   // hello precedes anything already queued
    ws_.async_accept([self](beast::error_code ec) {
      if (ec) { self->closed_ = true; return; }
      self->open_ = true;
      if (!self->queue_.empty()) self->write_next();
      self->read();   // client frames are ignored; the read detects close
    });
  }

  bool closed() const { return closed_; }

  void send(MessagePtr m) {
    if (closed_) return;
    if (queue_.size() >= kMaxQueue) drop_oldest_batch();
    queue_.push_back(std::move(m));
    if (open_ && queue_.size() == 1) write_next();
  }

 private:
  // Never drop queue_.front(): it may be the buffer of an in-flight async_write.
  void drop_oldest_batch() {
    for (auto it = queue_.begin() + 1; it != queue_.end(); ++it) {
      if ((*it)->binary) { queue_.erase(it); return; }
    }
    if (queue_.size() > 1) queue_.erase(queue_.begin() + 1);
  }

  void read() {
    auto self = shared_from_this();
    ws_.async_read(rbuf_, [self](beast::error_code ec, std::size_t) {
      if (ec) { self->closed_ = true; return; }
      self->rbuf_.consume(self->rbuf_.size());
      self->read();
    });
  }

  void write_next() {
    auto self = shared_from_this();
    const auto& m = *queue_.front();
    ws_.binary(m.binary);
    ws_.async_write(net::buffer(m.payload), [self](beast::error_code ec, std::size_t) {
      if (ec) { self->closed_ = true; self->queue_.clear(); return; }
      self->queue_.pop_front();
      if (!self->queue_.empty()) self->write_next();
    });
  }

  websocket::stream<tcp::socket> ws_;
  beast::flat_buffer rbuf_;
  std::deque<MessagePtr> queue_;
  bool open_{false};                 // handshake complete (io thread only)
  std::atomic<bool> closed_{false};
};

}  // namespace detail

class StreamServer {
 public:
  explicit StreamServer(StreamConfig cfg)
      : cfg_(std::move(cfg)),
        acceptor_(ioc_, tcp::endpoint(tcp::v4(), cfg_.port)) {}

  ~StreamServer() { stop(); }

  StreamServer(const StreamServer&) = delete;
  StreamServer& operator=(const StreamServer&) = delete;

  /// Bind, accept, and run the io_context on a background thread.
  void start() {
    if (thread_.joinable()) return;
    accept();
    thread_ = std::thread([this] {
      auto guard = net::make_work_guard(ioc_);
      ioc_.run();
    });
  }

  void stop() {
    if (!thread_.joinable()) return;
    ioc_.stop();
    thread_.join();
  }

  unsigned short port() const { return acceptor_.local_endpoint().port(); }

  std::size_t viewers() const {
    std::lock_guard<std::mutex> lk(mu_);
    return viewers_;
  }

  /// Announce an entity. Must precede any push() for that ord; viewers that
  /// connect later receive it in their hello.
  void add_entity(const EntityDesc& e) {
    {
      std::lock_guard<std::mutex> lk(mu_);
      entities_.push_back(e);
    }
    broadcast_text("{\"type\":\"entity\",\"entity\":" + detail::entity_json(e) + "}");
  }

  /// Queue one record. `sample_index` counts that entity's own samples from its
  /// t0_ns; the viewer reconstructs time as t0_ns + sample_index * dt_ns.
  void push(std::uint32_t ord, std::uint32_t sample_index, const State& st) {
    std::lock_guard<std::mutex> lk(batch_mu_);
    detail::put_u32(batch_, ord);
    detail::put_u32(batch_, sample_index);
    detail::put_record(batch_, st);
    ++batch_count_;
  }

  /// Send everything queued since the last flush as one binary frame. Call once
  /// per tick — batching keeps a 100-entity sim to one frame per tick.
  void flush() {
    std::string body;
    std::uint32_t n;
    {
      std::lock_guard<std::mutex> lk(batch_mu_);
      if (batch_count_ == 0) return;
      body.swap(batch_);
      n = batch_count_;
      batch_count_ = 0;
      batch_.clear();
      batch_.reserve(body.size());
    }
    std::string frame;
    frame.reserve(4 + body.size());
    detail::put_u32(frame, n);
    frame += body;
    broadcast(std::make_shared<detail::Message>(detail::Message{true, std::move(frame)}));
  }

  /// A discrete happening — same vocabulary as the .tspi footer event log
  /// ("launch", "intercept", "ground_impact", ...). `data_json` is an optional
  /// raw JSON object body, e.g. R"({"miss_m":3.2})".
  void event(std::int64_t t_ns, const std::string& kind, std::int64_t src = -1,
             std::int64_t dst = -1, const std::string& data_json = "") {
    std::string s = "{\"type\":\"event\",\"t_ns\":" + std::to_string(t_ns) + ",";
    s += detail::json_kv("kind", kind);
    if (src >= 0) s += ",\"src\":" + std::to_string(src);
    if (dst >= 0) s += ",\"dst\":" + std::to_string(dst);
    if (!data_json.empty()) s += ",\"data\":" + data_json;
    s += "}";
    broadcast_text(s);
  }

  /// Tell viewers the run is over (they stop following the head and keep the
  /// buffered trail scrubbable).
  void end() { broadcast_text("{\"type\":\"end\"}"); }

 private:
  void accept() {
    acceptor_.async_accept([this](beast::error_code ec, tcp::socket sock) {
      if (!ec) {
        auto s = std::make_shared<detail::Session>(std::move(sock));
        sessions_.push_back(s);
        s->start(hello_message());
        {
          std::lock_guard<std::mutex> lk(mu_);
          viewers_ = sessions_.size();
        }
      }
      accept();
    });
  }

  detail::MessagePtr hello_message() {
    std::lock_guard<std::mutex> lk(mu_);
    std::string s = "{\"type\":\"hello\",\"protocol\":1,";
    s += detail::json_kv("name", cfg_.name) + ",";
    s += "\"dt_ns\":" + std::to_string(cfg_.dt_ns) + ",";
    s += "\"epoch_unix_ns\":\"" + std::to_string(cfg_.epoch_unix_ns) + "\",";
    s += "\"origin\":{\"lat_deg\":" + std::to_string(cfg_.origin_lat_deg) +
         ",\"lon_deg\":" + std::to_string(cfg_.origin_lon_deg) +
         ",\"alt_m\":" + std::to_string(cfg_.origin_alt_m) + "},";
    s += "\"extent_m\":" + std::to_string(cfg_.extent_m) + ",";
    s += detail::json_kv("dynamics", cfg_.dynamics) + ",\"entities\":[";
    for (std::size_t i = 0; i < entities_.size(); ++i) {
      if (i) s += ",";
      s += detail::entity_json(entities_[i]);
    }
    s += "]}";
    return std::make_shared<detail::Message>(detail::Message{false, std::move(s)});
  }

  void broadcast_text(std::string s) {
    broadcast(std::make_shared<detail::Message>(detail::Message{false, std::move(s)}));
  }

  // Hand the message to the io thread; sessions_ is only ever touched there.
  void broadcast(detail::MessagePtr m) {
    net::post(ioc_, [this, m] {
      std::size_t live = 0;
      for (auto it = sessions_.begin(); it != sessions_.end();) {
        auto s = it->lock();
        if (!s || s->closed()) { it = sessions_.erase(it); continue; }
        s->send(m);
        ++live;
        ++it;
      }
      std::lock_guard<std::mutex> lk(mu_);
      viewers_ = live;
    });
  }

  StreamConfig cfg_;
  net::io_context ioc_{1};
  tcp::acceptor acceptor_;
  std::thread thread_;
  std::vector<std::weak_ptr<detail::Session>> sessions_;   // io thread only

  mutable std::mutex mu_;                                  // entities_, viewers_
  std::vector<EntityDesc> entities_;
  std::size_t viewers_{0};

  std::mutex batch_mu_;
  std::string batch_;
  std::uint32_t batch_count_{0};
};

}  // namespace tspi
