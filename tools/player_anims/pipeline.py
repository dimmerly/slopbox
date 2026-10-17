"""Motifect -> humanoid pipeline.

blender -b --factory-startup -P pipeline.py -- jobs.json

Rebuilds the Motifect skeleton as a clean armature with humanoid standard bone names, bakes each clip
onto it (trimmed to one gait cycle, facing normalised, made in place, loop seam blended), and builds a
skinned mannequin on the same skeleton.
"""
import bpy, sys, json, math, os
from mathutils import Matrix, Vector, Quaternion

args = sys.argv[sys.argv.index("--") + 1:]
jobs = json.load(open(args[0]))
SRC = jobs["src"]
OUT = jobs["out"]
FPS = 30

SIDES = ("Left", "Right")
MAP = [("Hips", None, None),
       ("Spine", "Spine1", "Hips"), ("Chest", "Spine2", "Spine"), ("UpperChest", "Chest", "Chest"),
       ("Neck", "Neck1", "UpperChest"), ("Head", "Head", "Neck")]
for s in SIDES:
    MAP += [(f"{s}Shoulder", f"{s}Shoulder", "UpperChest"), (f"{s}UpperArm", f"{s}Arm", f"{s}Shoulder"),
            (f"{s}LowerArm", f"{s}ForeArm", f"{s}UpperArm"), (f"{s}Hand", f"{s}Hand", f"{s}LowerArm"),
            (f"{s}UpperLeg", f"{s}Leg", "Hips"), (f"{s}LowerLeg", f"{s}Shin", f"{s}UpperLeg"),
            (f"{s}Foot", f"{s}Foot", f"{s}LowerLeg"), (f"{s}Toes", f"{s}ToeBase", f"{s}Foot")]
PARENT = {t: p for t, _, p in MAP}
SOURCE = {t: s for t, s, _ in MAP}

R90 = Matrix.Rotation(math.radians(90), 4, 'X')


def load(name):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=os.path.join(SRC, name + ".fbx"))
    return bpy.data.objects["Hips"]


def canon(src):
    m = R90 @ Matrix.Diagonal((0.01, 0.01, 0.01, 1.0))
    low = min((m @ src.data.bones[n].head_local).z for n in ("LeftToeEnd", "RightToeEnd", "LeftFoot", "RightFoot"))
    return Matrix.Translation((0, 0, -low)) @ m


def rot_only(m):
    return Matrix.Translation(m.translation) @ m.to_3x3().normalized().to_4x4()


def build_armature(src, C):
    arm = bpy.data.armatures.new("Armature")
    tgt = bpy.data.objects.new("Armature", arm)
    bpy.context.scene.collection.objects.link(tgt)
    bpy.context.view_layer.objects.active = tgt
    bpy.ops.object.mode_set(mode='EDIT')
    rest = {}
    for t, s, p in MAP:
        if s is None:
            m, length = rot_only(C), 0.1
        else:
            sb = src.data.bones[s]
            m, length = rot_only(C @ sb.matrix_local), max(sb.length * 0.01, 0.03)
        eb = arm.edit_bones.new(t)
        eb.head, eb.tail = (0, 0, 0), (0, length, 0)
        eb.matrix = m
        if p:
            eb.parent = arm.edit_bones[p]
        rest[t] = m.copy()
    # Neck reaches the head now that Neck2 is gone.
    arm.edit_bones["Neck"].tail = arm.edit_bones["Head"].head
    bpy.ops.object.mode_set(mode='OBJECT')
    for t in rest:
        rest[t] = tgt.data.bones[t].matrix_local.copy()
    return tgt, rest


def sample(src, frame):
    """World matrices of each target bone at a frame: source world rotations, hips position."""
    bpy.context.scene.frame_set(frame)
    out = {}
    for t, s, _ in MAP:
        w = src.matrix_world if s is None else src.matrix_world @ src.pose.bones[s].matrix
        out[t] = rot_only(w)
    return out


def yaw_of(q):
    f = q @ Vector((0, -1, 0))  # the character faces -Y at rest
    return math.atan2(f.x, -f.y)


def to_basis(world, rest):
    basis = {}
    full = {}
    for t, _, p in MAP:
        if p is None:
            full[t] = world[t]
            basis[t] = rest[t].inverted() @ world[t]
        else:
            local_rest = rest[p].inverted() @ rest[t]
            m = full[p] @ local_rest
            m = Matrix.Translation(m.translation) @ world[t].to_3x3().to_4x4()
            full[t] = m
            basis[t] = local_rest.inverted() @ full[p].inverted() @ m
    return basis


def foot_signal(world, direction):
    hips = world["Hips"].translation
    foot = world["LeftFoot"].translation
    return (foot - hips).xy.dot(direction)


def find_cycle(src, f0, f1, k):
    """One gait cycle between two left foot reaches, nearest the middle of the clip."""
    a = sample(src, f0)["Hips"].translation.xy
    b = sample(src, f1)["Hips"].translation.xy
    d = (b - a)
    if d.length < 0.3:
        return None, 0.0
    d.normalize()
    sig = [foot_signal(sample(src, f), d) for f in range(f0, f1 + 1)]
    sm = [sum(sig[max(0, i - 2):i + 3]) / len(sig[max(0, i - 2):i + 3]) for i in range(len(sig))]
    mean = sum(sm) / len(sm)
    peaks = [i for i in range(2, len(sm) - 2) if sm[i] >= max(sm[i - 2:i + 3]) and sm[i] > mean]
    merged = []
    for p in peaks:
        if merged and p - merged[-1] < 10:
            if sm[p] > sm[merged[-1]]:
                merged[-1] = p
        else:
            merged.append(p)
    mid = (f1 - f0) / 2
    best = None
    for i in range(len(merged) - 1):
        s, e = merged[i] + f0, merged[i + 1] + f0
        if s - k < f0 or e > f1 or not 14 <= e - s <= 70:
            continue
        score = abs((s + e) / 2 - f0 - mid)
        if best is None or score < best[0]:
            best = (score, s, e)
    if best is None:
        raise RuntimeError(f"no cycle found, peaks {merged}")
    return (best[1], best[2]), d


def bake_clip(job):
    src = load(job["src"])
    C = canon(src)
    tgt, rest = build_armature(src, C)
    action = src.animation_data.action
    f0, f1 = int(action.frame_range[0]), int(action.frame_range[1])
    loop = job.get("loop", True)
    k = job.get("blend", 8)
    if "range" in job:
        s, e = job["range"]
    else:
        cyc, _ = find_cycle(src, f0, f1, k)
        s, e = cyc if cyc else (f0 + k, f1)
    L = e - s
    first = max(f0, s - k) if loop else s
    frames = {f: sample(src, f) for f in range(first, e + 1)}

    # Face exactly forward on average.
    yaws = [yaw_of(frames[f]["Hips"].to_quaternion() @ C.to_quaternion().inverted()) for f in range(s, e + 1)]
    yaw = math.atan2(sum(math.sin(y) for y in yaws), sum(math.cos(y) for y in yaws))
    turn = Matrix.Rotation(-yaw, 4, 'Z')
    for f in frames:
        frames[f]["Hips"] = turn @ frames[f]["Hips"]
        for t in frames[f]:
            if t != "Hips":
                frames[f][t] = turn @ frames[f][t]

    # In place: remove the steady travel, keep the sway.
    p0 = frames[s]["Hips"].translation.copy()
    p1 = frames[e]["Hips"].translation.copy()
    travel = (p1 - p0).xy
    speed = travel.length / (L / FPS)
    for f, w in frames.items():
        u = (f - s) / L
        h = w["Hips"]
        pos = h.translation.copy()
        pos.x -= p0.x + travel.x * u
        pos.y -= p0.y + travel.y * u
        if "hipsZ" in job:
            pos.z = job["hipsZ"]
        h.translation = pos

    basis = {f: to_basis(w, rest) for f, w in frames.items()}

    # Seam: fade the end of the cycle into the frames that lead up to its start.
    if loop and k > 0:
        for f in range(e - k, e + 1):
            g = f - L
            if g < first:
                continue
            u = (f - (e - k)) / k
            u = u * u * (3 - 2 * u)
            for t in basis[f]:
                a, b = basis[f][t], basis[g][t]
                q = a.to_quaternion().slerp(b.to_quaternion(), u)
                loc = a.translation.lerp(b.translation, u)
                basis[f][t] = Matrix.Translation(loc) @ q.to_matrix().to_4x4()

    tgt.animation_data_create()
    tgt.animation_data.action = bpy.data.actions.new(job["name"])
    prev = {}
    for i, f in enumerate(range(s, e + 1)):
        for t, m in basis[f].items():
            pb = tgt.pose.bones[t]
            pb.rotation_mode = 'QUATERNION'
            q = m.to_quaternion()
            if t in prev and prev[t].dot(q) < 0:
                q.negate()
            prev[t] = q
            pb.rotation_quaternion = q
            pb.keyframe_insert("rotation_quaternion", frame=i)
            if t == "Hips":
                pb.location = m.translation
                pb.keyframe_insert("location", frame=i)

    # A rest key well before the clip, so the file's default node transforms are the T-pose.
    for pb in tgt.pose.bones:
        pb.rotation_quaternion = Quaternion()
        pb.keyframe_insert("rotation_quaternion", frame=-20)
        if pb.name == "Hips":
            pb.location = Vector()
            pb.keyframe_insert("location", frame=-20)
    bpy.data.objects.remove(src)
    sc = bpy.context.scene
    sc.frame_start, sc.frame_end = 0, L
    sc.frame_set(-20)
    sc.render.fps = FPS
    export(os.path.join(OUT, job["name"] + ".fbx"), tgt, anim=True)
    return {"name": job["name"], "frames": L, "seconds": L / FPS, "speed": round(speed, 3),
            "travel": [round(travel.x, 3), round(travel.y, 3)], "range": [s, e], "yawDeg": round(math.degrees(yaw), 1)}


def export(path, tgt, anim, extra=()):
    bpy.ops.object.select_all(action='DESELECT')
    tgt.select_set(True)
    for o in extra:
        o.select_set(True)
    bpy.context.view_layer.objects.active = tgt
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={'ARMATURE', 'MESH'},
        add_leaf_bones=False, primary_bone_axis='Y', secondary_bone_axis='X',
        apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y',
        bake_anim=anim, bake_anim_use_all_actions=False, bake_anim_use_nla_strips=False,
        bake_anim_force_startend_keying=True, bake_anim_simplify_factor=0.0, bake_anim_step=1.0,
        mesh_smooth_type='FACE', use_mesh_modifiers=True)


# ---------------------------------------------------------------- mannequin

def part_ellipsoid(center, radii, rot=None):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=32, ring_count=16, radius=1, location=center)
    o = bpy.context.active_object
    o.scale = radii
    if rot is not None:
        o.rotation_mode = 'QUATERNION'
        o.rotation_quaternion = rot
    return o


def part_capsule(a, b, ra, rb):
    a, b = Vector(a), Vector(b)
    d = b - a
    bpy.ops.mesh.primitive_cone_add(vertices=32, radius1=ra, radius2=rb, depth=d.length, location=(a + b) / 2)
    o = bpy.context.active_object
    o.rotation_mode = 'QUATERNION'
    o.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(d.normalized())
    parts = [o, part_ellipsoid(a, (ra, ra, ra)), part_ellipsoid(b, (rb, rb, rb))]
    return parts


def part_loft(base, keys, step=0.008, seg=40, power=2.3):
    """A closed tube through (height, half width, half depth, forward offset) keys, Catmull-Rom smoothed."""
    def at(t):
        i = min(int(t), len(keys) - 2)
        u = t - i
        p0, p1, p2, p3 = (keys[max(i - 1, 0)], keys[i], keys[i + 1], keys[min(i + 2, len(keys) - 1)])
        return [0.5 * ((2 * b) + (-a + c) * u + (2 * a - 5 * b + 4 * c - d) * u * u + (-a + 3 * b - 3 * c + d) * u ** 3)
                for a, b, c, d in zip(p0, p1, p2, p3)]
    rings = []
    n = int((keys[-1][0] - keys[0][0]) / step)
    # Parameter by key index, sampled evenly enough since keys are evenly spaced.
    for j in range(n + 1):
        h, w, d, fy = at(j / n * (len(keys) - 1))
        ring = []
        for s in range(seg):
            a = 2 * math.pi * s / seg
            c, si = math.cos(a), math.sin(a)
            x = w * math.copysign(abs(c) ** (2 / power), c)
            y = d * math.copysign(abs(si) ** (2 / power), si)
            ring.append(base + Vector((x, y + fy, h)))
        rings.append(ring)
    verts = [v for r in rings for v in r]
    faces = []
    for j in range(len(rings) - 1):
        for s in range(seg):
            a, b = j * seg + s, j * seg + (s + 1) % seg
            faces.append((a, b, b + seg, a + seg))
    bot = len(verts); verts.append(sum(rings[0], Vector()) / seg)
    top = len(verts); verts.append(sum(rings[-1], Vector()) / seg)
    last = (len(rings) - 1) * seg
    for s in range(seg):
        faces.append((bot, (s + 1) % seg, s))
        faces.append((top, last + s, last + (s + 1) % seg))
    me = bpy.data.meshes.new("loft")
    me.from_pydata([tuple(v) for v in verts], [], faces)
    o = bpy.data.objects.new("loft", me)
    bpy.context.scene.collection.objects.link(o)
    return o


# Palette: one swatch per region in a strip texture, so a skinned mesh (whose renderer replaces the
# material colour with one tint) can still show clothes, skin and hair. Colours are sRGB 0-255.
PALETTE = {
    "skin": (188, 140, 112), "shirt": (100, 118, 138), "trim": (82, 98, 118), "pants": (44, 50, 64),
    "belt": (46, 34, 26), "buckle": (150, 148, 138), "shoe": (32, 28, 25), "sole": (20, 18, 17),
    "hair": (44, 33, 25), "eye": (30, 26, 22), "lip": (156, 100, 88), "brow": (40, 30, 23),
    "eyewhite": (226, 222, 214),
}
SLOTS = list(PALETTE)
SWATCH = 32


def palette_image(path):
    w, h = SWATCH * len(SLOTS), SWATCH
    img = bpy.data.images.new("CitizenPalette", w, h, alpha=False)
    px = [0.0] * (w * h * 4)
    for i, name in enumerate(SLOTS):
        r, g, b = (c / 255 for c in PALETTE[name])
        for y in range(h):
            for x in range(i * SWATCH, (i + 1) * SWATCH):
                o = (y * w + x) * 4
                px[o:o + 4] = (r, g, b, 1.0)
    img.pixels = px
    img.filepath_raw = path
    img.file_format = 'PNG'
    img.save()
    return img


def paint(obj, region_of_face):
    """Points every face's UVs at its region's swatch centre."""
    me = obj.data
    uv = me.uv_layers.get("UVMap") or me.uv_layers.new(name="UVMap")
    for poly in me.polygons:
        slot = SLOTS.index(region_of_face(poly))
        u = (slot + 0.5) / len(SLOTS)
        for li in poly.loop_indices:
            uv.data[li].uv = (u, 0.5)


def solid(obj, region):
    paint(obj, lambda poly: region)


def rigid(obj, bone):
    """Skins a whole accessory to one bone."""
    g = obj.vertex_groups.new(name=bone)
    g.add([v.index for v in obj.data.vertices], 1.0, 'REPLACE')


def follow(obj, body):
    """Skins an accessory like the body under it, by copying the nearest surface's weights."""
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    dt = obj.modifiers.new("weights", 'DATA_TRANSFER')
    dt.object = body
    dt.use_vert_data = True
    dt.data_types_verts = {'VGROUP_WEIGHTS'}
    dt.vert_mapping = 'POLYINTERP_NEAREST'
    dt.layers_vgroup_select_src = 'ALL'
    dt.layers_vgroup_select_dst = 'NAME'
    bpy.ops.object.datalayout_transfer(modifier=dt.name)
    bpy.ops.object.modifier_apply(modifier=dt.name)


def finish_part(o):
    bpy.ops.object.select_all(action='DESELECT')
    o.select_set(True)
    bpy.context.view_layer.objects.active = o
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    bpy.ops.object.shade_smooth()
    return o


def join(objs, name):
    bpy.ops.object.select_all(action='DESELECT')
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    o = bpy.context.active_object
    o.name = o.data.name = name
    return o


def part_box(center, size, rot=None, bevel=0.0):
    bpy.ops.mesh.primitive_cube_add(size=1, location=center)
    o = bpy.context.active_object
    o.scale = size
    if rot is not None:
        o.rotation_mode = 'QUATERNION'
        o.rotation_quaternion = rot
    if bevel > 0:
        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
        m = o.modifiers.new("bevel", 'BEVEL')
        m.width, m.segments = bevel, 3
        bpy.ops.object.modifier_apply(modifier="bevel")
    return o


def part_torus(center, major, minor, scale=(1, 1, 1), tilt=0.0):
    bpy.ops.mesh.primitive_torus_add(major_radius=major, minor_radius=minor, major_segments=48, minor_segments=12,
                                     location=center)
    o = bpy.context.active_object
    o.scale = scale
    o.rotation_euler = (math.radians(tilt), 0, 0)
    return o


def build_model(job):
    import bmesh
    src = load(job["src"])
    C = canon(src)
    tgt, rest = build_armature(src, C)

    def P(n):
        return C @ src.data.bones[n].head_local

    parts = []
    add = parts.append
    ext = parts.extend

    hipL, hipR = P("LeftLeg"), P("RightLeg")
    pel = (hipL + hipR) / 2
    head = P("Head")
    neck = P("Neck1")
    # Torso lofted from many overlapping slices, heights relative to the hip joints, so there are no creases.
    # (height, half width, half depth, forward offset; the character faces -Y). A loose work shirt on top.
    profile = [(-0.07, 0.105, 0.08, 0.005), (-0.02, 0.158, 0.106, 0.0), (0.04, 0.166, 0.11, 0.0),
               (0.10, 0.163, 0.106, 0.0), (0.16, 0.154, 0.103, -0.004), (0.22, 0.15, 0.102, -0.008),
               (0.28, 0.155, 0.104, -0.012), (0.34, 0.165, 0.11, -0.016), (0.40, 0.174, 0.113, -0.018),
               (0.46, 0.176, 0.108, -0.014), (0.51, 0.158, 0.092, -0.006), (0.55, 0.095, 0.064, 0.0)]
    base = pel + Vector((0, 0.03, 0))
    add(part_loft(base, profile))
    ext(part_capsule(neck + Vector((0, 0.015, -0.03)), head + Vector((0, 0.01, 0.03)), 0.066, 0.06))
    add(part_ellipsoid(head + Vector((0, -0.01, 0.095)), (0.08, 0.096, 0.11)))
    add(part_ellipsoid(head + Vector((0, -0.036, 0.032)), (0.066, 0.07, 0.066)))  # jaw
    add(part_ellipsoid(head + Vector((0, -0.105, 0.078)), (0.013, 0.022, 0.028)))  # nose
    add(part_ellipsoid(head + Vector((0, -0.088, 0.13)), (0.06, 0.03, 0.016)))  # brow ridge
    for sx in (1, -1):
        add(part_ellipsoid(head + Vector((0.079 * sx, 0.0, 0.085)), (0.012, 0.024, 0.032)))  # ears
        add(part_ellipsoid(head + Vector((0.045 * sx, -0.075, 0.06)), (0.025, 0.02, 0.022)))  # cheeks

    for s, sx in (("Left", 1), ("Right", -1)):
        arm, fore, hand = P(f"{s}Arm"), P(f"{s}ForeArm"), P(f"{s}Hand")
        ext(part_capsule(neck + Vector((0.03 * sx, 0.015, -0.01)), arm + Vector((-0.02 * sx, 0.01, 0.0)), 0.064, 0.06))
        add(part_ellipsoid(arm + Vector((0.0, 0.0, -0.005)), (0.08, 0.068, 0.068)))  # shoulder
        ext(part_capsule(arm, fore, 0.058, 0.049))  # sleeve
        ext(part_capsule(fore, hand + Vector((-0.03 * sx, 0, 0)), 0.049, 0.04))
        tip = P(f"{s}HandMiddleEnd")
        d = (tip - hand)
        ext(part_capsule(hand + Vector((-0.04 * sx, 0, 0)), hand + d * 0.1, 0.03, 0.03))  # wrist
        add(part_ellipsoid(hand + d * 0.3, (0.06, 0.045, 0.021)))  # palm
        add(part_ellipsoid(hand + d * 0.72, (0.058, 0.042, 0.015)))  # fingers
        ext(part_capsule(P(f"{s}HandThumb1"), P(f"{s}HandThumbEnd"), 0.016, 0.011))

        hip, knee, ankle = P(f"{s}Leg"), P(f"{s}Shin"), P(f"{s}Foot")
        toe, toe_end = P(f"{s}ToeBase"), P(f"{s}ToeEnd")
        # Straight work trousers: wide, little taper.
        ext(part_capsule(hip + Vector((0.004 * sx, 0.01, -0.01)), knee, 0.09, 0.064))
        add(part_ellipsoid(hip.lerp(knee, 0.4) + Vector((0.002 * sx, -0.004, 0)), (0.078, 0.084, 0.18)))
        ext(part_capsule(knee, ankle + Vector((0, 0, 0.1)), 0.062, 0.055))
        # Boots, a little oversized.
        ext(part_capsule(ankle + Vector((0, 0.008, 0.05)), ankle + Vector((0, 0.008, 0.14)), 0.052, 0.056))
        heel = Vector((ankle.x, ankle.y + 0.045, 0.05))
        front = Vector((toe_end.x, toe_end.y + 0.012, 0.036))
        ext(part_capsule(heel, front, 0.05, 0.04))
        add(part_ellipsoid(Vector((toe.x, toe.y + 0.025, 0.045)), (0.052, 0.07, 0.044)))

    for o in parts:
        o.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    body = join(parts, "Body")
    rm = body.modifiers.new("remesh", 'REMESH')
    rm.mode = 'VOXEL'
    rm.voxel_size = 0.006
    bpy.ops.object.modifier_apply(modifier="remesh")
    sm = body.modifiers.new("smooth", 'SMOOTH')
    sm.factor, sm.iterations = 0.9, 8
    bpy.ops.object.modifier_apply(modifier="smooth")
    for v in body.data.vertices:
        if v.co.z < 0.0:
            v.co.z = 0.0
    dec = body.modifiers.new("decimate", 'DECIMATE')
    dec.ratio = min(1.0, 16000 / max(1, len(body.data.polygons)))
    bpy.ops.object.modifier_apply(modifier="decimate")
    bpy.ops.object.shade_smooth()

    # Regions, decided in the rest pose (T-pose, character facing -Y).
    neck_line = neck.z + 0.01
    waist = pel.z + 0.105
    hand_x = P("LeftHand").x - 0.035
    shoulder_x = 0.2

    boot_top = 0.165
    # The collar: a short tube round the base of the neck, tipped forward along the neck's lean.
    # Below its top edge (just under the rim, so the border hides inside) is shirt.
    collar_tilt_deg = 17
    collar_centre = neck + Vector((0, 0.004, 0.03))
    collar_up = Matrix.Rotation(math.radians(collar_tilt_deg), 3, 'X') @ Vector((0, 0, 1))
    collar_top = collar_centre + collar_up * 0.016

    # Borders between regions are jagged on the remeshed surface, so each one sits under a ring
    # (cuffs, collar, belt, boot tops) added below.
    def region(poly):
        c = poly.center
        ax = abs(c.x)
        if ax > shoulder_x and c.z > 1.2:
            return "skin" if ax > hand_x else "shirt"
        if (c - collar_top).dot(collar_up) > 0 and c.z > neck_line - 0.02:
            return "skin"
        if c.z > waist:
            return "shirt"
        if c.z > boot_top:
            return "pants"
        return "sole" if c.z < 0.012 else "shoe"
    paint(body, region)

    skin_body(body, tgt, P)

    # Accessories, skinned rigidly to one bone each and joined into the body.
    acc = []

    def accessory(o, bone, reg):
        """Adds a part, skinned rigidly to bone, or like the body under it when bone is None."""
        finish_part(o)
        solid(o, reg)
        if bone:
            rigid(o, bone)
        else:
            follow(o, body)
        acc.append(o)

    # Hair: a shell over the crown that stops at a hairline sloping from forehead to nape.
    hc = head + Vector((0, -0.006, 0.1))
    bpy.ops.mesh.primitive_uv_sphere_add(segments=96, ring_count=48, radius=1, location=hc)
    hair = bpy.context.active_object
    hair.scale = (0.086, 0.102, 0.115)
    finish_part(hair)
    bpy.ops.object.mode_set(mode='EDIT')
    bm = bmesh.from_edit_mesh(hair.data)
    # A clean cut along a plane sloping from the forehead down to the nape.
    normal = Vector((0, 0.9, 1)).normalized()
    bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], dist=1e-5,
                           plane_co=hc + Vector((0, 0, 0.0)), plane_no=normal, clear_inner=True)
    bmesh.update_edit_mesh(hair.data)
    bpy.ops.object.mode_set(mode='OBJECT')
    so = hair.modifiers.new("solid", 'SOLIDIFY')
    so.thickness, so.offset = 0.012, 1.0
    bpy.ops.object.modifier_apply(modifier="solid")
    accessory(hair, "Head", "hair")

    for sx in (1, -1):
        accessory(part_ellipsoid(head + Vector((0.031 * sx, -0.088, 0.108)), (0.015, 0.009, 0.01)), "Head", "eyewhite")
        accessory(part_ellipsoid(head + Vector((0.031 * sx, -0.096, 0.108)), (0.0065, 0.004, 0.0072)), "Head", "eye")
        q = Quaternion((0, 1, 0), math.radians(5 * sx))
        accessory(part_box(head + Vector((0.033 * sx, -0.104, 0.126)), (0.036, 0.012, 0.009), q, 0.003), "Head", "brow")
    accessory(part_box(head + Vector((0, -0.1, 0.045)), (0.034, 0.012, 0.007), None, 0.003), "Head", "lip")

    # Collar, cuffs, pockets, belt, buckle and boot tops, each hiding a colour border.
    # A stand-up work collar: a short open tube round the base of the neck, lower at the front.
    bpy.ops.mesh.primitive_cone_add(vertices=48, radius1=0.092, radius2=0.081, depth=0.052, end_fill_type='NOTHING',
                                    location=collar_centre)
    collar = bpy.context.active_object
    collar.rotation_euler = (math.radians(collar_tilt_deg), 0, 0)
    sol = collar.modifiers.new("solid", 'SOLIDIFY')
    sol.thickness = 0.008
    bpy.ops.object.modifier_apply(modifier="solid")
    accessory(collar, None, "trim")
    for s, sx in (("Left", 1), ("Right", -1)):
        cuff = part_torus(Vector((hand_x * sx, P(f"{s}Hand").y, P(f"{s}Hand").z)), 0.036, 0.011)
        cuff.rotation_euler = (0, math.radians(90), 0)
        accessory(cuff, None, "trim")
        ankle = P(f"{s}Foot")
        accessory(part_torus(Vector((ankle.x, ankle.y + 0.008, boot_top)), 0.058, 0.012), None, "shoe")
    chest_front = base.y - 0.018 - 0.113
    for sx in (1, -1):
        accessory(part_box(Vector((0.075 * sx, chest_front + 0.006, pel.z + 0.405)), (0.065, 0.014, 0.075), None, 0.004),
                  None, "trim")
    accessory(part_torus(Vector((0, base.y - 0.002, waist)), 0.16, 0.024, (1.0, 0.69, 0.9)), None, "belt")
    accessory(part_box(Vector((0, base.y - 0.11, waist)), (0.05, 0.016, 0.036), None, 0.004), None, "buckle")

    body = join([body] + acc, "Body")

    img = palette_image(os.path.join(OUT, "Citizen_Palette.png"))
    mat = bpy.data.materials.new("Citizen")
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
    tex.image = img
    tex.interpolation = 'Closest'
    mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = 0.7
    body.data.materials.clear()
    body.data.materials.append(mat)

    unweighted = sum(1 for v in body.data.vertices if not any(g.weight > 0.001 for g in v.groups))
    print("RES verts", len(body.data.vertices), "unweighted", unweighted)

    bpy.data.objects.remove(src)
    export(os.path.join(OUT, job["name"] + ".fbx"), tgt, anim=False, extra=[body])
    if job.get("preview"):
        preview(body, tgt, job["preview"])
    return {"name": job["name"], "verts": len(body.data.vertices), "unweighted": unweighted}


def skin_body(body, tgt, P):
    """Heat weights against a copy of the rig whose bones point along the limbs, then hands the mesh to the real rig."""
    # The real bones all share the source's rest orientation (pointing up), which heat weighting reads as
    # segments in the wrong places. Aim each one at the joint it drives, and run Hips down into the pelvis.
    rig = tgt.copy()
    rig.data = tgt.data.copy()
    bpy.context.scene.collection.objects.link(rig)
    bpy.ops.object.select_all(action='DESELECT')
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode='EDIT')
    eb = rig.data.edit_bones
    for b in eb:
        b.use_connect = False
    ends = {"Spine": eb["Chest"].head, "Chest": eb["UpperChest"].head, "UpperChest": eb["Neck"].head,
            "Neck": eb["Head"].head, "Head": P("HeadEnd")}
    for s in SIDES:
        ends.update({f"{s}Shoulder": eb[f"{s}UpperArm"].head, f"{s}UpperArm": eb[f"{s}LowerArm"].head,
                     f"{s}LowerArm": eb[f"{s}Hand"].head, f"{s}Hand": P(f"{s}HandMiddleEnd"),
                     f"{s}UpperLeg": eb[f"{s}LowerLeg"].head, f"{s}LowerLeg": eb[f"{s}Foot"].head,
                     f"{s}Foot": eb[f"{s}Toes"].head, f"{s}Toes": P(f"{s}ToeEnd")})
    for name, end in ends.items():
        eb[name].tail = end.copy()
    hip_z = eb["LeftUpperLeg"].head.z
    hb = eb["Hips"]
    hb.tail = hb.head + Vector((0, 0.0, -(hb.head.z - (hip_z - 0.05))))
    for s in SIDES:
        ub = eb[f"{s}UpperLeg"]
        ub.head = ub.head.lerp(ub.tail, 0.1)
    bpy.ops.object.mode_set(mode='OBJECT')
    bpy.ops.object.select_all(action='DESELECT')
    body.select_set(True)
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.parent_set(type='ARMATURE_AUTO')
    for m in body.modifiers:
        if m.type == 'ARMATURE':
            m.object = tgt
    mw = body.matrix_world.copy()
    body.parent = tgt
    body.matrix_world = mw
    bpy.data.objects.remove(rig)
    bpy.ops.object.select_all(action='DESELECT')
    body.select_set(True)
    bpy.context.view_layer.objects.active = body
    bpy.ops.object.mode_set(mode='WEIGHT_PAINT')
    bpy.ops.object.vertex_group_smooth(group_select_mode='ALL', factor=0.5, repeat=3)
    bpy.ops.object.mode_set(mode='OBJECT')


def preview(body, tgt, path):
    sc = bpy.context.scene
    sc.render.engine = 'BLENDER_WORKBENCH'
    sc.display.shading.light = 'STUDIO'
    sc.display.shading.color_type = 'TEXTURE'
    sc.render.resolution_x, sc.render.resolution_y = 900, 900
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    sc.collection.objects.link(cam)
    sc.camera = cam
    cam.data.type = 'ORTHO'
    shots = [((0, -5, 0.9), (90, 0, 0), 2.0), ((5, -5, 0.9), (90, 0, 45), 2.0), ((0, 5, 0.9), (90, 0, 180), 2.0),
             ((0, -5, 1.62), (90, 0, 0), 0.45)]
    for i, (loc, rot, scale) in enumerate(shots):
        cam.location = loc
        cam.rotation_euler = [math.radians(a) for a in rot]
        cam.data.ortho_scale = scale
        sc.render.filepath = path.replace(".png", f"_{i}.png")
        bpy.ops.render.render(write_still=True)


results = []
for job in jobs["jobs"]:
    try:
        r = build_model(job) if job.get("model") else bake_clip(job)
    except Exception as ex:
        import traceback
        traceback.print_exc()
        r = {"name": job["name"], "error": str(ex)}
    print("RES", json.dumps(r))
    results.append(r)
json.dump(results, open(os.path.join(OUT, "_report.json"), "w"), indent=1)
