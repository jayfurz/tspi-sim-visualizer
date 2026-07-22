extends RefCounted
## .tspi v1 reader — GDScript port of web/viewer/tspi.js (itself a port of
## src/Tspi.Core/Runtime/IO/TspiReader.cs). Keep the three in lockstep.
##
## GDScript floats are 64-bit doubles, so positions keep the format's f64
## precision end to end; the lossy cast to render-space Vector3 (f32) happens
## in ned_godot.gd, at the last moment.
##
## GDScript has no exceptions: `open`/`parse_buffer` return "" on success or an
## error message, mirroring the reference readers' throw sites one-for-one.

const HEADER_SIZE := 128
const TRAILER_SIZE := 32
const LAYOUT_6DOF_V1 := 1
const STRIDE_6DOF_V1 := 64


class Entity:
	var ord: int
	var id: String
	var team: String
	var type: String
	var model: String
	var parent  # launching entity's ord, or null
	var t0_ns: int
	var samples: int
	var offset: int
	var stride: int
	var layout: int


var source_name := ""
var version := 0
var flags := 0
var dt_ns := 0
var epoch_unix_ns := 0
var origin_lat_deg := 0.0
var origin_lon_deg := 0.0
var origin_alt_m := 0.0
var manifest_sha256 := ""
var footer := {}
var entities: Array[Entity] = []
var events: Array = []
var provenance: Array = []
var environment = null

var _bytes := PackedByteArray()

var dt_sec: float:
	get:
		return dt_ns / 1e9

static var _CRC_TABLE := _make_crc_table()


static func _make_crc_table() -> PackedInt64Array:
	var t := PackedInt64Array()
	t.resize(256)
	for i in 256:
		var c := i
		for k in 8:
			c = (0xEDB88320 ^ (c >> 1)) if (c & 1) else (c >> 1)
		t[i] = c
	return t


static func crc32(bytes: PackedByteArray) -> int:
	var c := 0xFFFFFFFF
	for b in bytes:
		c = _CRC_TABLE[(c ^ b) & 0xFF] ^ (c >> 8)
	return c ^ 0xFFFFFFFF


func open(path: String) -> String:
	var bytes := FileAccess.get_file_as_bytes(path)
	if bytes.is_empty():
		return ".tspi: cannot open '%s' (%s)" % [path, error_string(FileAccess.get_open_error())]
	return parse_buffer(bytes, path.get_file())


## Parse a complete .tspi file from a byte buffer. Populates self; "" on success.
func parse_buffer(bytes: PackedByteArray, name_hint := "") -> String:
	source_name = name_hint
	if bytes.size() < HEADER_SIZE + TRAILER_SIZE:
		return ".tspi: file too small"
	if bytes.slice(0, 4).get_string_from_ascii() != "TSPI":
		return ".tspi: bad file magic"

	version = bytes.decode_u32(4)
	flags = bytes.decode_u32(8)
	dt_ns = bytes.decode_s64(16)
	epoch_unix_ns = bytes.decode_s64(24)
	origin_lat_deg = bytes.decode_double(32)
	origin_lon_deg = bytes.decode_double(40)
	origin_alt_m = bytes.decode_double(48)
	manifest_sha256 = bytes.slice(56, 88).hex_encode()
	if version != 1:
		return ".tspi: unsupported format version %d" % version
	if dt_ns <= 0:
		return ".tspi: dt_ns must be positive"

	var t_off := bytes.size() - TRAILER_SIZE
	if bytes.slice(t_off + 24, t_off + 32).get_string_from_ascii() != "TSPIFTR1":
		return ".tspi: no valid trailer at EOF (torn write? run 'tspi recover')"
	var footer_offset := bytes.decode_s64(t_off)
	var footer_len := bytes.decode_s64(t_off + 8)
	var footer_crc := bytes.decode_u32(t_off + 16)
	if footer_offset < 0 or footer_len < 0 or footer_offset + footer_len > bytes.size():
		return ".tspi: footer out of file bounds"
	var footer_bytes := bytes.slice(footer_offset, footer_offset + footer_len)
	if crc32(footer_bytes) != footer_crc:
		return ".tspi: footer CRC mismatch"
	var parsed = JSON.parse_string(footer_bytes.get_string_from_utf8())
	if parsed == null or not parsed is Dictionary:
		return ".tspi: footer is not valid JSON"
	footer = parsed

	entities.clear()
	for e in footer.get("entities", []):
		var ent := Entity.new()
		ent.ord = int(e["ord"])
		ent.id = e["id"]
		ent.team = e.get("team", "")
		ent.type = e.get("type", "")
		ent.model = e.get("model", "")
		ent.parent = null if e.get("parent") == null else int(e["parent"])
		ent.t0_ns = int(e["t0_ns"])
		ent.samples = int(e["samples"])
		ent.offset = int(e["offset"])
		ent.stride = int(e["stride"])
		ent.layout = int(e["layout"])
		entities.append(ent)
	for ent in entities:
		if ent.layout != LAYOUT_6DOF_V1:
			continue  # unknown layouts are legal; unsampleable
		if ent.stride < STRIDE_6DOF_V1:
			return ".tspi: entity '%s' stride below layout-1 prefix size" % ent.id
		var end := ent.offset + ent.samples * ent.stride
		if ent.offset < HEADER_SIZE or end > bytes.size():
			return ".tspi: entity '%s' block out of file bounds" % ent.id

	events = footer.get("events", [])
	provenance = footer.get("provenance", [])
	environment = footer.get("environment")
	_bytes = bytes
	return ""


func find_entity(id: String) -> Entity:
	for e in entities:
		if e.id == id:
			return e
	return null


func start_sec(e: Entity) -> float:
	return e.t0_ns / 1e9


func end_sec(e: Entity) -> float:
	return (e.t0_ns + (e.samples - 1) * dt_ns) / 1e9


## Raw record i of an entity: {pos: [3] f64, vel: [3], quat: [4] wxyz, omega: [3]}.
func read_sample(e: Entity, i: int) -> Dictionary:
	if i < 0 or i >= e.samples:
		push_error(".tspi: sample index out of range")
		return {}
	var o := e.offset + i * e.stride
	return {
		"pos": [_bytes.decode_double(o), _bytes.decode_double(o + 8), _bytes.decode_double(o + 16)],
		"vel": [_bytes.decode_float(o + 24), _bytes.decode_float(o + 28), _bytes.decode_float(o + 32)],
		"quat": [
			_bytes.decode_float(o + 36), _bytes.decode_float(o + 40),
			_bytes.decode_float(o + 44), _bytes.decode_float(o + 48),
		],
		"omega": [_bytes.decode_float(o + 52), _bytes.decode_float(o + 56), _bytes.decode_float(o + 60)],
	}


static func _norm_q(q: Array) -> Array:
	var m: float = sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3])
	if m < 1e-12:
		return [1.0, 0.0, 0.0, 0.0]
	return [q[0] / m, q[1] / m, q[2] / m, q[3] / m]


## Shortest-path slerp with nlerp fallback — mirrors QuatD.Slerp.
static func slerp_wxyz(a: Array, b: Array, t: float) -> Array:
	var dot: float = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3]
	if dot < 0.0:
		b = [-b[0], -b[1], -b[2], -b[3]]
		dot = -dot
	if dot > 0.9995:
		return _norm_q([
			a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1]),
			a[2] + t * (b[2] - a[2]), a[3] + t * (b[3] - a[3]),
		])
	var th0: float = acos(dot)
	var th: float = th0 * t
	var s0: float = sin(th0)
	var s_a: float = sin(th0 - th) / s0
	var s_b: float = sin(th) / s0
	return [
		s_a * a[0] + s_b * b[0], s_a * a[1] + s_b * b[1],
		s_a * a[2] + s_b * b[2], s_a * a[3] + s_b * b[3],
	]


## Interpolated state at t_sec (seconds since the header epoch), or null when t
## is outside the entity's alive window and clamp_t is false. Cubic Hermite
## position (stored velocities as tangents), Hermite-derivative velocity,
## slerped attitude, lerped body rates — identical to TspiReader.TrySampleAt.
func sample_at(e: Entity, t_sec: float, clamp_t := false) -> Variant:
	if e.samples <= 0 or e.layout != LAYOUT_6DOF_V1:
		return null
	var t0 := start_sec(e)
	var t1 := end_sec(e)
	if t_sec < t0 or t_sec > t1:
		if not clamp_t:
			return null
		t_sec = t0 if t_sec < t0 else t1
	if e.samples == 1:
		var only := read_sample(e, 0)
		return {"pos": only.pos, "vel": only.vel, "quat": _norm_q(only.quat), "omega": only.omega}
	var dt := dt_sec
	var u := (t_sec - t0) / dt
	var i := int(floor(u))
	i = clampi(i, 0, e.samples - 2)
	u -= i

	var a := read_sample(e, i)
	var b := read_sample(e, i + 1)

	var h00 := (2.0 * u - 3.0) * u * u + 1.0
	var h10 := ((u - 2.0) * u + 1.0) * u
	var h01 := (3.0 - 2.0 * u) * u * u
	var h11 := (u - 1.0) * u * u
	var g00 := 6.0 * u * u - 6.0 * u
	var g10 := 3.0 * u * u - 4.0 * u + 1.0
	var g01 := -6.0 * u * u + 6.0 * u
	var g11 := 3.0 * u * u - 2.0 * u

	var pos := [0.0, 0.0, 0.0]
	var vel := [0.0, 0.0, 0.0]
	for k in 3:
		pos[k] = h00 * a.pos[k] + h10 * dt * a.vel[k] + h01 * b.pos[k] + h11 * dt * b.vel[k]
		vel[k] = (g00 / dt) * a.pos[k] + g10 * a.vel[k] + (g01 / dt) * b.pos[k] + g11 * b.vel[k]
	return {
		"pos": pos,
		"vel": vel,
		"quat": slerp_wxyz(_norm_q(a.quat), _norm_q(b.quat), u),
		"omega": [
			a.omega[0] + u * (b.omega[0] - a.omega[0]),
			a.omega[1] + u * (b.omega[1] - a.omega[1]),
			a.omega[2] + u * (b.omega[2] - a.omega[2]),
		],
	}


## Time span {min, max} in seconds across all sampleable entities.
func time_span() -> Dictionary:
	var lo := INF
	var hi := -INF
	for e in entities:
		if e.samples <= 0:
			continue
		lo = minf(lo, start_sec(e))
		hi = maxf(hi, end_sec(e))
	return {"min": lo, "max": hi}
