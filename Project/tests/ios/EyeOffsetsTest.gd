extends SceneTree

## Run with --script res://tests/ios/EyeOffsetsTest.gd, optionally followed by
## -- --pack=/absolute/path/to/Reignition.pck to check the exported game data.
func _initialize() -> void:
	call_deferred("run")


func run() -> void:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--pack="):
			if not ProjectSettings.load_resource_pack(argument.trim_prefix("--pack=")):
				fail("Could not mount exported game pack")
				return

	var scene = load("res://object/player/resource/Sonic.tscn") as PackedScene
	if scene == null:
		fail("Could not load Sonic scene")
		return

	var expected = {&"LEye": Vector2(0.225, 0), &"REye": Vector2(-0.225, 0)}
	var state = scene.get_state()
	for node_index in range(state.get_node_count()):
		var node_name = state.get_node_name(node_index)
		if not expected.has(node_name):
			continue
		var offset = null
		var eye_script = null
		for property_index in range(state.get_node_property_count(node_index)):
			var property_name = state.get_node_property_name(node_index, property_index)
			if property_name == &"pupil_uv_offset":
				offset = state.get_node_property_value(node_index, property_index)
			elif property_name == &"script":
				eye_script = state.get_node_property_value(node_index, property_index)
		if not offset is Vector2 or not offset.is_equal_approx(expected[node_name]):
			fail("%s pupil offset missing or incorrect: %s" % [node_name, offset])
			return
		if not eye_script is Script or not eye_script.resource_path.ends_with("SonicEye.gd"):
			fail("%s pupil-offset script missing" % node_name)
			return
		expected.erase(node_name)

	if not expected.is_empty():
		fail("Sonic eye nodes are missing")
		return
	print("EYE_OFFSETS_PASS: both pupil offsets and their runtime script are preserved")
	quit()


func fail(message: String) -> void:
	push_error("EYE_OFFSETS_FAIL: " + message)
	quit(1)
