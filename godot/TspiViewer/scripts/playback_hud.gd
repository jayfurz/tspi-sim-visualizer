extends CanvasLayer
## Transport + panels, built in code so the scene file stays trivial.
## Space play/pause, arrows ±1 s (shift ±10 s), Home/End, F frames the scene,
## scrub bar with event tick marks, 0.25–8x rate, loop toggle, entity list
## (click to follow), event log (click to seek).

const EVENT_COLORS := {
	"launch": Color(1.0, 0.85, 0.3),
	"cpa": Color(0.4, 0.9, 1.0),
	"intercept": Color(1.0, 0.35, 0.3),
	"ground_impact": Color(1.0, 0.55, 0.2),
	"expire": Color(0.6, 0.6, 0.65),
}
const RATES: Array[float] = [0.25, 0.5, 1.0, 2.0, 4.0, 8.0]

var controller: Node3D = null
var cam: Camera3D = null

var _slider: HSlider
var _ticks: Control
var _play_btn: Button
var _time_label: Label
var _meta_label: Label
var _entity_list: ItemList
var _event_list: ItemList
var _drop_hint: Label
var _dialog: FileDialog


class TickOverlay extends Control:
	var marks: Array = []  # {frac: float, color: Color}

	func _draw() -> void:
		for m in marks:
			var x: float = m.frac * size.x
			draw_line(Vector2(x, 0), Vector2(x, size.y), m.color, 2.0)


func _ready() -> void:
	var bottom := MarginContainer.new()
	bottom.set_anchors_preset(Control.PRESET_BOTTOM_WIDE)
	bottom.grow_vertical = Control.GROW_DIRECTION_BEGIN
	for side in ["left", "right", "bottom"]:
		bottom.add_theme_constant_override("margin_" + side, 8)
	add_child(bottom)
	var vbox := VBoxContainer.new()
	bottom.add_child(vbox)

	_slider = HSlider.new()
	_slider.step = 0.0
	_slider.focus_mode = Control.FOCUS_NONE
	_slider.value_changed.connect(_on_scrub)
	vbox.add_child(_slider)
	_ticks = TickOverlay.new()
	_ticks.set_anchors_preset(Control.PRESET_FULL_RECT)
	_ticks.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_slider.add_child(_ticks)

	var bar := HBoxContainer.new()
	vbox.add_child(bar)
	_play_btn = _btn(bar, "Pause", func(): _toggle_play())
	var rate := OptionButton.new()
	rate.focus_mode = Control.FOCUS_NONE
	for r in RATES:
		rate.add_item(str(r) + "x")
	rate.select(RATES.find(1.0))
	rate.item_selected.connect(func(i): controller.time_scale = RATES[i])
	bar.add_child(rate)
	var loop_btn := CheckButton.new()
	loop_btn.text = "loop"
	loop_btn.button_pressed = true
	loop_btn.focus_mode = Control.FOCUS_NONE
	loop_btn.toggled.connect(func(on): controller.loop = on)
	bar.add_child(loop_btn)
	_time_label = Label.new()
	_time_label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_time_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	bar.add_child(_time_label)
	_btn(bar, "Open…", func(): _dialog.popup_centered_ratio(0.6))

	var left := PanelContainer.new()
	left.set_anchors_preset(Control.PRESET_TOP_LEFT)
	left.position = Vector2(8, 8)
	left.custom_minimum_size = Vector2(300, 0)
	add_child(left)
	var lbox := VBoxContainer.new()
	left.add_child(lbox)
	_meta_label = Label.new()
	_meta_label.text = "no file loaded"
	_meta_label.add_theme_font_size_override("font_size", 12)
	lbox.add_child(_meta_label)
	_entity_list = ItemList.new()
	_entity_list.custom_minimum_size = Vector2(0, 110)
	_entity_list.focus_mode = Control.FOCUS_NONE
	_entity_list.item_selected.connect(_on_entity_selected)
	_entity_list.empty_clicked.connect(func(_p, _b):
		_entity_list.deselect_all()
		cam.follow = null)
	lbox.add_child(_entity_list)
	_event_list = ItemList.new()
	_event_list.custom_minimum_size = Vector2(0, 130)
	_event_list.focus_mode = Control.FOCUS_NONE
	_event_list.item_selected.connect(_on_event_selected)
	lbox.add_child(_event_list)

	_drop_hint = Label.new()
	_drop_hint.text = "Drop a .tspi file here, pass one after '--',\nor use Open… below."
	_drop_hint.set_anchors_preset(Control.PRESET_CENTER)
	_drop_hint.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	add_child(_drop_hint)

	_dialog = FileDialog.new()
	_dialog.access = FileDialog.ACCESS_FILESYSTEM
	_dialog.file_mode = FileDialog.FILE_MODE_OPEN_FILE
	_dialog.filters = ["*.tspi ; TSPI trajectory"]
	_dialog.file_selected.connect(func(p): report(controller.load_path(p)))
	add_child(_dialog)


func _btn(parent: Node, text: String, pressed: Callable) -> Button:
	var b := Button.new()
	b.text = text
	b.focus_mode = Control.FOCUS_NONE
	b.pressed.connect(pressed)
	parent.add_child(b)
	return b


func report(err: String) -> void:
	if err != "":
		_meta_label.text = err
		push_error(err)


func on_loaded(file) -> void:
	_drop_hint.visible = false
	_slider.min_value = controller.min_t
	_slider.max_value = controller.max_t
	var dyn := "?"
	if file.provenance.size() > 0:
		dyn = file.provenance[file.provenance.size() - 1].get("dynamics", "?")
	_meta_label.text = "%s\ndt %.1f ms   %d entities\norigin %.4f°, %.4f°, %.0f m\n%s" % [
		file.source_name, file.dt_ns / 1e6, file.entities.size(),
		file.origin_lat_deg, file.origin_lon_deg, file.origin_alt_m, dyn]

	_entity_list.clear()
	for v in controller.views:
		var e = v.entity
		_entity_list.add_item("%s  (%s/%s)" % [e.id, e.team, e.type])

	var by_ord := {}
	for e in file.entities:
		by_ord[e.ord] = e.id
	_event_list.clear()
	_ticks.marks.clear()
	var span := maxf(controller.max_t - controller.min_t, 1e-9)
	for ev in file.events:
		var t: float = ev["t_ns"] / 1e9
		var line: String = "%7.2fs  %s" % [t, ev["kind"]]
		if ev.get("src") != null:
			line += "  %s" % by_ord.get(int(ev["src"]), "?")
		if ev.get("dst") != null:
			line += " → %s" % by_ord.get(int(ev["dst"]), "?")
		var miss = ev.get("data", {}).get("miss_m")
		if miss != null:
			line += "  (miss %.1f m)" % miss
		_event_list.add_item(line)
		_ticks.marks.append({
			"frac": (t - controller.min_t) / span,
			"color": EVENT_COLORS.get(ev["kind"], Color.WHITE),
		})
	_ticks.queue_redraw()
	cam.frame_all(controller)


func _toggle_play() -> void:
	controller.playing = not controller.playing
	_play_btn.text = "Pause" if controller.playing else "Play"


func _on_scrub(value: float) -> void:
	controller.seek(value)


func _on_entity_selected(idx: int) -> void:
	cam.follow = controller.views[idx].root


func _on_event_selected(idx: int) -> void:
	controller.seek(controller.file.events[idx]["t_ns"] / 1e9)


func _unhandled_key_input(event: InputEvent) -> void:
	if not (event is InputEventKey and event.pressed) or controller.file == null:
		return
	var step := 10.0 if event.shift_pressed else 1.0
	match event.keycode:
		KEY_SPACE: _toggle_play()
		KEY_LEFT: controller.seek(controller.time_sec - step)
		KEY_RIGHT: controller.seek(controller.time_sec + step)
		KEY_HOME: controller.seek(controller.min_t)
		KEY_END: controller.seek(controller.max_t)
		KEY_F: cam.frame_all(controller)
		_: return
	get_viewport().set_input_as_handled()


func _process(_delta: float) -> void:
	if controller == null or controller.file == null:
		return
	_slider.set_value_no_signal(controller.time_sec)
	_time_label.text = "t = %8.2f s   [%.2f … %.2f]" % [
		controller.time_sec, controller.min_t, controller.max_t]
	_play_btn.text = "Pause" if controller.playing else "Play"
	for i in controller.views.size():
		var alive: bool = controller.is_alive(controller.views[i].entity, controller.time_sec)
		_entity_list.set_item_custom_fg_color(
			i, Color.WHITE if alive else Color(1, 1, 1, 0.35))
