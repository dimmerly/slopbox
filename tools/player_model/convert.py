"""Cleans the Sketchfab "Rigged T-Pose Human Male" FBX for Prowl.

blender -b --factory-startup -P convert.py -- MikeAlger.fbx Human.fbx

Its Cinema 4D export binds the skins with an offset Prowl's importer ignores, and carries the 50 face shapes a
second time as loose meshes. Blender reads it correctly; this drops the loose meshes, applies the 0.01 scale and
writes a plain FBX.
"""
import bpy, sys
args = sys.argv[sys.argv.index("--") + 1:]
src, dst = args
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=src)
# The 50 loose meshes are the blendshape sources, already on Body as shape keys. The empties hold nothing.
for o in list(bpy.data.objects):
    if (o.type == 'MESH' and not o.modifiers) or (o.type == 'EMPTY' and not o.children):
        bpy.data.objects.remove(o, do_unlink=True)
for o in list(bpy.data.objects):
    if o.type == 'EMPTY':
        print("KEEP EMPTY", o.name, [c.name for c in o.children])
bpy.ops.object.select_all(action='SELECT')
bpy.context.view_layer.objects.active = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
for o in bpy.data.objects:
    print("OBJ", o.type, o.name, tuple(round(v, 3) for v in o.matrix_world.to_scale()))
arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
print("HIPS", arm.data.bones['mixamorig_Hips'].head_local, "BONES", len(arm.data.bones))
bpy.ops.export_scene.fbx(filepath=dst, use_selection=False, object_types={'ARMATURE', 'MESH'},
    add_leaf_bones=False, bake_anim=False, use_mesh_modifiers=False, mesh_smooth_type='FACE',
    apply_scale_options='FBX_SCALE_UNITS', path_mode='STRIP')
