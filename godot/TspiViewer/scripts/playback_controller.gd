extends Node3D
## Playback-only viewer: opens a .tspi and drives one child per entity, sampling
## interpolated pose at the current playback time. All simulation happened
## offline in the headless engine — this node never integrates anything.
##
## Scrub, pause, and time-dilation are all just "set time_sec": every pose is an
## O(1) interpolated lookup, so seeking anywhere in a million-sample run is free.

const TspiFileScript := preload("res://scripts/tspi_file.gd")
const NedGodot := preload("res://scripts/ned_godot.gd")

signal loaded(file)

@export var time_scale := 1.0
@export var playing := true
@export var loop := true
@export var trail_seconds := 8.0

const MAX_PATH_POINTS := 2048
const MAX_TRAIL_STEPS := 128

const TEAM_COLORS := {
	"blue": Color(0.30, 0.60, 1.00),
	"red": Color(1.00, 0.35, 0.30),
	"white": Color(0.92, 0.92, 0.95),
}
const NEUTRAL_COLOR := Color(0.62, 0.66, 0.72)

var file = null  # TspiFileScript instance
var time_sec := 0.0
var min_t := 0.0
var max_t := 0.0
var scene_aabb := AABB()
var views: Array = []  # {entity, root: Node3D, trail: ImmediateMesh, mats: {...}}

var _aabb_set := false

var _ground: Node3D = null


static func team_color(team: String) -> Color:
	return TEAM_COLORS.get(team, NEUTRAL_COLOR)


func load_path(path: String, keep_time := false) -> String:
	var f = TspiFileScript.new()
	var err: String = f.open(path)
	if err != "":
		return err
	var prev_t := time_sec
	var had_file := file != null
	unload()
	file = f
	var span: Dictionary = f.time_span()
	min_t = span.min
	max_t = span.max
	scene_aabb = AABB()
	_aabb_set = false
	for e in f.entities:
		_create_view(e)
	_build_ground()
	time_sec = clampf(prev_t, min_t, max_t) if keep_time and had_file else min_t
	loaded.emit(f)
	_apply_poses()
	return ""


func unload() -> void:
	for v in views:
		v.root.queue_free()
	views.clear()
	if _ground != null:
		_ground.queue_free()
		_ground = null
	file = null


func _unshaded_mat(color: Color) -> StandardMaterial3D:
	var m := StandardMaterial3D.new()
	m.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	m.albedo_color = color
	if color.a < 1.0:
		m.transparency = BaseMaterial3D.TRANSPARENCY_ALPHA
	return m


func _create_view(e) -> void:
	var color := team_color(e.team)
	var root := Node3D.new()
	root.name = "%s (%s/%s)" % [e.id, e.team, e.type]
	add_child(root)

	# Entity dart: a cone whose tip points -Z (Godot forward = body +X).
	var cone := CylinderMesh.new()
	cone.top_radius = 0.0
	cone.bottom_radius = 6.0 if e.type == "munition" else 15.0
	cone.height = 24.0 if e.type == "munition" else 50.0
	cone.radial_segments = 12
	var mi := MeshInstance3D.new()
	mi.mesh = cone
	mi.rotation_degrees.x = -90.0
	var body_mat := StandardMaterial3D.new()
	body_mat.albedo_color = color
	mi.material_override = body_mat
	root.add_child(mi)

	# Dim full recorded path (built once) + bright flown-so-far trail with an
	# altitude pole (redrawn each frame from the file — never accumulated).
	var mats := {
		"trail": _unshaded_mat(color),
		"dim": _unshaded_mat(Color(color, 0.22)),
	}
	if e.samples > 1 and e.layout == TspiFileScript.LAYOUT_6DOF_V1:
		var step := maxi(1, int(ceil(float(e.samples) / MAX_PATH_POINTS)))
		var pts := PackedVector3Array()
		var i := 0
		while i < e.samples:
			var p: Vector3 = NedGodot.to_godot(file.read_sample(e, i).pos)
			pts.append(p)
			scene_aabb = scene_aabb.expand(p) if _aabb_set else AABB(p, Vector3.ZERO)
			_aabb_set = true
			i += step
		var arrays := []
		arrays.resize(Mesh.ARRAY_MAX)
		arrays[Mesh.ARRAY_VERTEX] = pts
		var path_mesh := ArrayMesh.new()
		path_mesh.add_surface_from_arrays(Mesh.PRIMITIVE_LINE_STRIP, arrays)
		var path_mi := MeshInstance3D.new()
		path_mi.mesh = path_mesh
		path_mi.material_override = mats.dim
		add_child(path_mi)

	var trail := ImmediateMesh.new()
	var trail_mi := MeshInstance3D.new()
	trail_mi.mesh = trail
	add_child(trail_mi)

	views.append({"entity": e, "root": root, "trail": trail, "mats": mats})


func _build_ground() -> void:
	_ground = Node3D.new()
	_ground.name = "Ground"
	add_child(_ground)
	var ext := 1000.0
	if _aabb_set:
		ext = maxf(ext, maxf(absf(scene_aabb.position.x) + scene_aabb.size.x,
				absf(scene_aabb.position.z) + scene_aabb.size.z) * 1.2)
	var step: float = pow(10.0, floor(log(ext / 4.0) / log(10.0)))
	var half: float = ceilf(ext / step) * step
	var grid := ImmediateMesh.new()
	grid.surface_begin(Mesh.PRIMITIVE_LINES, _unshaded_mat(Color(0.35, 0.38, 0.45, 0.35)))
	var x: float = -half
	while x <= half:
		grid.surface_add_vertex(Vector3(x, 0, -half))
		grid.surface_add_vertex(Vector3(x, 0, half))
		grid.surface_add_vertex(Vector3(-half, 0, x))
		grid.surface_add_vertex(Vector3(half, 0, x))
		x += step
	grid.surface_end()
	# North (-Z) and east (+X) axes from the NED origin.
	grid.surface_begin(Mesh.PRIMITIVE_LINES, _unshaded_mat(Color(0.35, 0.65, 1.0)))
	grid.surface_add_vertex(Vector3.ZERO)
	grid.surface_add_vertex(Vector3(0, 0, -half))
	grid.surface_end()
	grid.surface_begin(Mesh.PRIMITIVE_LINES, _unshaded_mat(Color(0.45, 0.9, 0.5)))
	grid.surface_add_vertex(Vector3.ZERO)
	grid.surface_add_vertex(Vector3(half, 0, 0))
	grid.surface_end()
	var mi := MeshInstance3D.new()
	mi.mesh = grid
	_ground.add_child(mi)


func _process(delta: float) -> void:
	if file == null:
		return
	if playing:
		advance(delta * time_scale)
	_apply_poses()


## Advance (or rewind) playback time, honoring loop/clamp at the ends.
func advance(delta_sec: float) -> void:
	time_sec += delta_sec
	if time_sec > max_t:
		time_sec = min_t + (time_sec - max_t) if loop else max_t
	if time_sec < min_t:
		time_sec = max_t - (min_t - time_sec) if loop else min_t


## Jump directly to an absolute time (scrubbing). O(1) per entity.
func seek(t: float) -> void:
	time_sec = clampf(t, min_t, max_t)
	_apply_poses()


func is_alive(e, t: float) -> bool:
	return e.samples > 0 and t >= file.start_sec(e) and t <= file.end_sec(e)


func _apply_poses() -> void:
	for v in views:
		var st = file.sample_at(v.entity, time_sec, false)
		v.trail.clear_surfaces()
		if st == null:
			# Entity not yet spawned or already terminated: hide it.
			v.root.visible = false
			continue
		v.root.visible = true
		var pos: Vector3 = NedGodot.to_godot(st.pos)
		v.root.transform = Transform3D(NedGodot.to_godot_basis(st.quat), pos)
		_draw_trail(v, pos)


func _draw_trail(v: Dictionary, pos: Vector3) -> void:
	var e = v.entity
	var t_from: float = maxf(file.start_sec(e), time_sec - trail_seconds)
	if time_sec - t_from > 1e-9:
		var steps := mini(MAX_TRAIL_STEPS, maxi(2, int(ceil((time_sec - t_from) / file.dt_sec))))
		v.trail.surface_begin(Mesh.PRIMITIVE_LINE_STRIP, v.mats.trail)
		for i in steps:
			var t := t_from + (time_sec - t_from) * i / float(steps)
			var st = file.sample_at(e, t, true)
			v.trail.surface_add_vertex(NedGodot.to_godot(st.pos))
		v.trail.surface_add_vertex(pos)
		v.trail.surface_end()
	# Altitude pole down to the ground plane.
	if absf(pos.y) > 1e-6:
		v.trail.surface_begin(Mesh.PRIMITIVE_LINES, v.mats.dim)
		v.trail.surface_add_vertex(pos)
		v.trail.surface_add_vertex(Vector3(pos.x, 0, pos.z))
		v.trail.surface_end()
