extends Camera3D
## Orbit camera: left-drag orbits, right-drag (or shift-drag) pans, wheel zooms.
## Default pose looks north (-Z), matching the web viewer. Setting `follow`
## pins the orbit target to an entity; frame_all() fits the whole scene.

var target := Vector3.ZERO
var yaw := 0.0  # 0 = camera south of target, looking north
var pitch := 0.5
var dist := 3000.0
var follow: Node3D = null


func _ready() -> void:
	far = 200000.0
	_update_transform()


func _unhandled_input(event: InputEvent) -> void:
	if event is InputEventMouseMotion and event.button_mask != 0:
		var pan: bool = (event.button_mask & MOUSE_BUTTON_MASK_RIGHT) != 0 \
				or ((event.button_mask & MOUSE_BUTTON_MASK_LEFT) != 0 and event.shift_pressed)
		if pan:
			var scale := dist * 0.0015
			target += global_transform.basis.x * (-event.relative.x * scale) \
					+ global_transform.basis.y * (event.relative.y * scale)
			follow = null
		elif event.button_mask & MOUSE_BUTTON_MASK_LEFT:
			yaw -= event.relative.x * 0.006
			pitch = clampf(pitch + event.relative.y * 0.006, -1.45, 1.45)
		_update_transform()
	elif event is InputEventMouseButton and event.pressed:
		if event.button_index == MOUSE_BUTTON_WHEEL_UP:
			dist = maxf(10.0, dist * 0.9)
		elif event.button_index == MOUSE_BUTTON_WHEEL_DOWN:
			dist = minf(150000.0, dist * 1.1)
		_update_transform()


func _process(_delta: float) -> void:
	if follow != null:
		if not is_instance_valid(follow) or not follow.visible:
			follow = null
		else:
			target = follow.global_position
	_update_transform()


func _update_transform() -> void:
	var offset := Vector3(
		sin(yaw) * cos(pitch),
		sin(pitch),
		cos(yaw) * cos(pitch)) * dist
	position = target + offset
	look_at(target, Vector3.UP)


func frame_all(controller: Node3D) -> void:
	follow = null
	if not "scene_aabb" in controller:
		return
	var aabb: AABB = controller.scene_aabb
	target = aabb.get_center()
	dist = clampf(aabb.size.length() * 1.1, 100.0, 150000.0)
	_update_transform()
