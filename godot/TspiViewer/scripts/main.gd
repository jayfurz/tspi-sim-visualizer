extends Node3D
## Bootstrap: wires HUD <-> controller <-> camera, sets scene dressing, and
## loads a file from the command line (first arg after `--`) or OS drag-drop.

@onready var controller: Node3D = $Playback
@onready var cam: Camera3D = $OrbitCamera
@onready var hud: CanvasLayer = $Hud


func _ready() -> void:
	hud.controller = controller
	hud.cam = cam
	controller.loaded.connect(hud.on_loaded)

	var env := Environment.new()
	env.background_mode = Environment.BG_COLOR
	env.background_color = Color(0.043, 0.055, 0.08)
	env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	env.ambient_light_color = Color(0.5, 0.55, 0.65)
	env.ambient_light_energy = 0.6
	var we := WorldEnvironment.new()
	we.environment = env
	add_child(we)
	$Sun.rotation_degrees = Vector3(-50, 35, 0)

	get_window().files_dropped.connect(_on_files_dropped)
	var args := OS.get_cmdline_user_args()
	if args.size() > 0:
		hud.report(controller.load_path(args[0]))


func _on_files_dropped(files: PackedStringArray) -> void:
	if files.size() > 0:
		hud.report(controller.load_path(files[0]))
