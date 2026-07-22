extends SceneTree
## Cross-language contract test: reads the committed golden .tspi produced by
## the C# sim, mirroring tools/tspi_py/tests/test_reader.py. Run headless:
##
##   godot --headless --path godot/TspiViewer --script tests/test_reader.gd
##
## If these fail after a format change, either fix the regression or bump the
## format version and regenerate the golden (see docs/FORMAT.md).

const TspiFileScript := preload("res://scripts/tspi_file.gd")
const NedGodot := preload("res://scripts/ned_godot.gd")

const G := 9.80665

var checks := 0
var fails := 0


func ok(cond: bool, what: String) -> void:
	checks += 1
	if not cond:
		fails += 1
		printerr("FAIL: " + what)


func approx(a: float, b: float, tol: float, what: String) -> void:
	ok(absf(a - b) <= tol, "%s: %.9f vs %.9f (tol %s)" % [what, a, b, tol])


func vec_approx(v: Array, want: Array, tol: float, what: String) -> void:
	for k in want.size():
		approx(v[k], want[k], tol, "%s[%d]" % [what, k])


func _init() -> void:
	var golden := ProjectSettings.globalize_path("res://").path_join(
		"../../tools/tspi_py/tests/data/golden-v1.tspi")
	var f = TspiFileScript.new()
	var err: String = f.open(golden)
	ok(err == "", "open golden: " + err)
	if err != "":
		quit(1)
		return

	# -- header --------------------------------------------------------------
	ok(f.version == 1, "version")
	ok(f.dt_ns == 100_000_000, "dt_ns == 0.1 s")
	approx(f.origin_lat_deg, 34.9061, 1e-9, "origin lat")
	approx(f.origin_lon_deg, -117.8839, 1e-9, "origin lon")
	approx(f.origin_alt_m, 700.0, 1e-9, "origin alt")
	ok(f.epoch_unix_ns == 1_767_323_045_000_000_000, "epoch 2026-01-02T03:04:05Z")

	# -- entity table --------------------------------------------------------
	var ids := []
	for e in f.entities:
		ids.append(e.id)
	ids.sort()
	ok(ids == ["blue-01", "dart-01", "red-01"], "entity ids: %s" % str(ids))
	var blue = f.find_entity("blue-01")
	var red = f.find_entity("red-01")
	var dart = f.find_entity("dart-01")
	ok(blue.team == "blue" and blue.type == "aircraft" and blue.parent == null, "blue-01 fields")
	ok(blue.samples == 31, "blue 3 s at 10 Hz inclusive")
	ok(dart.type == "munition" and dart.parent == blue.ord, "dart parent")
	ok(dart.t0_ns == 500_000_000, "dart launched at t=0.5")

	# -- samples match manifest initial state --------------------------------
	vec_approx(f.read_sample(blue, 0).pos, [0.0, 0.0, -5000.0], 1e-9, "blue pos0")
	vec_approx(f.read_sample(blue, 0).vel, [200.0, 0.0, 0.0], 1e-6, "blue vel0")
	vec_approx(f.read_sample(red, 0).pos, [8000.0, 500.0, -5000.0], 1e-9, "red pos0")

	# -- straight flight kinematics: blue pos.N == 200 * t exactly -----------
	for i in blue.samples:
		var t: float = f.start_sec(blue) + i * f.dt_sec
		approx(f.read_sample(blue, i).pos[0], 200.0 * t, 1e-6, "blue N @ i=%d" % i)
		approx(f.read_sample(blue, i).pos[2], -5000.0, 1e-6, "blue D @ i=%d" % i)

	# -- ballistic dart drops under gravity ----------------------------------
	var d0: float = f.read_sample(dart, 0).pos[2]
	for i in dart.samples:
		var tau: float = i * f.dt_sec
		approx(f.read_sample(dart, i).pos[2], d0 + 0.5 * G * tau * tau, 1e-3,
			"dart D @ i=%d" % i)

	# -- quaternions are unit and sign-continuous ----------------------------
	for e in f.entities:
		var prev = null
		for i in e.samples:
			var q: Array = f.read_sample(e, i).quat
			var n: float = sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3])
			approx(n, 1.0, 1e-5, "%s |q| @ i=%d" % [e.id, i])
			if prev != null:
				var dot: float = prev[0] * q[0] + prev[1] * q[1] + prev[2] * q[2] + prev[3] * q[3]
				ok(dot >= 0.0, "%s quat sign flip @ i=%d" % [e.id, i])
			prev = q

	# -- events --------------------------------------------------------------
	var kinds := []
	for ev in f.events:
		kinds.append(ev["kind"])
	ok(kinds == ["launch", "expire"], "event kinds: %s" % str(kinds))
	var launch = f.events[0]
	approx(launch["t_ns"] / 1e9, 0.5, 1e-12, "launch t")
	ok(int(launch["src"]) == dart.ord and int(launch["dst"]) == red.ord, "launch src/dst")

	# -- provenance + environment --------------------------------------------
	ok(f.provenance.size() == 1, "one provenance record")
	var rec = f.provenance[0]
	ok(rec["op"] == "run" and int(rec["seed"]) == 12345, "provenance op/seed")
	ok(rec["manifest_sha256"].length() == 64, "provenance manifest hash")
	ok(rec["dynamics"] == "kinematic-3dof+synth-attitude", "dynamics honesty tag")
	ok(f.environment != null and f.environment["atmosphere"] == "none", "environment persisted")

	# -- time span + implicit times ------------------------------------------
	var span: Dictionary = f.time_span()
	approx(span.min, 0.0, 1e-12, "span min")
	approx(span.max, 3.0, 1e-9, "span max")

	# -- interpolation (identical math to TspiReader.TrySampleAt) ------------
	# Hermite with stored-velocity tangents reproduces linear and quadratic
	# motion exactly, so off-grid samples have analytic truth.
	var st = f.sample_at(blue, 1.234)
	vec_approx(st.pos, [246.8, 0.0, -5000.0], 1e-6, "blue interp pos @1.234")
	vec_approx(st.vel, [200.0, 0.0, 0.0], 1e-4, "blue interp vel @1.234")
	var tau_mid: float = 0.55 + 3 * f.dt_sec - f.start_sec(dart)
	var st_d = f.sample_at(dart, 0.55 + 3 * f.dt_sec)
	approx(st_d.pos[2], d0 + 0.5 * G * tau_mid * tau_mid, 2e-3, "dart interp D mid-grid")
	var q: Array = st_d.quat
	approx(sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]), 1.0, 1e-6,
		"interp quat unit")
	ok(f.sample_at(blue, 99.0) == null, "outside window -> null")
	ok(f.sample_at(blue, -1.0) == null, "before spawn -> null")
	var clamped = f.sample_at(blue, 99.0, true)
	approx(clamped.pos[0], 600.0, 1e-6, "clamped to end")

	# -- NED -> Godot mapping ------------------------------------------------
	ok(NedGodot.to_godot([1.0, 2.0, 3.0]).is_equal_approx(Vector3(2, -3, -1)),
		"pos map godot = (E, -D, -N)")
	var ident: Basis = NedGodot.to_godot_basis([1.0, 0.0, 0.0, 0.0])
	ok((ident * Vector3(0, 0, -1)).is_equal_approx(Vector3(0, 0, -1)),
		"identity attitude -> forward north (-Z)")
	ok((ident * Vector3.UP).is_equal_approx(Vector3.UP), "identity attitude -> up +Y")
	var c45 := cos(PI / 4)
	var east: Basis = NedGodot.to_godot_basis([c45, 0.0, 0.0, c45])  # yaw +90°
	ok((east * Vector3(0, 0, -1)).is_equal_approx(Vector3(1, 0, 0)),
		"yaw 90° -> forward east (+X)")
	var up90: Basis = NedGodot.to_godot_basis([c45, 0.0, c45, 0.0])  # pitch +90°
	ok((up90 * Vector3(0, 0, -1)).is_equal_approx(Vector3(0, 1, 0)),
		"pitch 90° -> forward up (+Y)")

	print("test_reader.gd: %d checks, %d failures" % [checks, fails])
	quit(1 if fails > 0 else 0)
