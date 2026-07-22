extends Object
## NED (right-handed, x-north / y-east / z-down) -> Godot (right-handed, y-up,
## -z-forward) conversion. Convention: godot = (E, -D, -N) — +X = east, +Y = up,
## -Z = north — the same render frame as web/viewer/app.js, and a proper
## rotation (det +1), so handedness is preserved. Attitude converts the
## NedUnity.cs way: rotate the body axes through the quat into NED, map them
## into Godot space, and rebuild an orthonormal basis — one code path, no
## hand-derived quaternion basis change to get subtly wrong.


static func to_godot(ned: Array) -> Vector3:
	return Vector3(ned[1], -ned[2], -ned[0])


static func to_godot_v(ned: Vector3) -> Vector3:
	return Vector3(ned.y, -ned.z, -ned.x)


## Rotate v by unit quaternion q (wxyz array): v + 2w(q̂×v) + 2 q̂×(q̂×v).
static func rotate_wxyz(q: Array, v: Vector3) -> Vector3:
	var qv := Vector3(q[1], q[2], q[3])
	var t := qv.cross(v) * 2.0
	return v + t * q[0] + qv.cross(t)


static func to_godot_basis(q_body_to_ned: Array) -> Basis:
	# Body axes in NED: forward = +X_body, down = +Z_body.
	var fwd := to_godot_v(rotate_wxyz(q_body_to_ned, Vector3(1, 0, 0)))
	var up := -to_godot_v(rotate_wxyz(q_body_to_ned, Vector3(0, 0, 1)))
	if fwd.length_squared() < 1e-10 or up.length_squared() < 1e-10:
		return Basis()
	fwd = fwd.normalized()
	up = up.normalized()
	if absf(fwd.dot(up)) > 0.9999:  # degenerate quat; pick any valid up
		up = Vector3.UP if absf(fwd.dot(Vector3.UP)) < 0.9999 else Vector3.RIGHT
	return Basis.looking_at(fwd, up)
