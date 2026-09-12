@tool
extends MeshInstance3D

## Keep the pupil offset in a normal exported property: headless scene exports
## can omit instance_shader_parameters because the dummy renderer has no uniforms.
@export var pupil_uv_offset: Vector2 = Vector2.ZERO:
	set(value):
		pupil_uv_offset = value
		set_instance_shader_parameter("uv_offset", pupil_uv_offset)


func _ready() -> void:
	set_instance_shader_parameter("uv_offset", pupil_uv_offset)
