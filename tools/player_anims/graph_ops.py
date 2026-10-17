import json
A = "Resources/player/anims/"
ops = []
def op(**k): ops.append(k)
def clip(owner, name, alias, looping=None, speed=None):
    op(op="add", type="motion.clip", owner=owner, **{"as": alias})
    op(op="set", node="$" + alias, key="Clip", value=f"{A}{name}.fbx#{name}")
    if looping: op(op="set", node="$" + alias, key="Looping", value=looping)
    if speed: op(op="set", node="$" + alias, key="Speed", value=speed)
def param(owner, name, alias):
    op(op="add", type="motion.parameter", owner=owner, **{"as": alias})
    op(op="set", node="$" + alias, key="Name", value=name)

for n in ["MoveX", "MoveY", "MoveWeight"]: op(op="param", name=n, kind="Number", value=0)
op(op="param", name="Rate", kind="Number", value=1)
op(op="param", name="Grounded", kind="Flag", value=True)
for n in ["Crouch", "Jump", "HardLand"]: op(op="param", name=n, kind="Flag", value=False)

op(op="add", type="motion.stateMachine", **{"as": "sm"})
for st in ["Ground", "Jump", "Fall", "Land"]:
    op(op="state", machine="$sm", name=st, **{"as": st.lower()})

# ---- Ground
g = "$ground"
param(g, "MoveX", "gx"); param(g, "MoveY", "gy"); param(g, "MoveWeight", "gw"); param(g, "Rate", "grate")
param(g, "Crouch", "gcrouch"); param(g, "Grounded", "ggrounded")
def blend(alias, points):
    op(op="add", type="motion.blend2d", owner=g, **{"as": alias})
    op(op="connect", **{"from": "$gx"}, to="$" + alias, pin="X")
    op(op="connect", **{"from": "$gy"}, to="$" + alias, pin="Y")
    for i, (name, x, y) in enumerate(points):
        c = f"{alias}_{i}"
        clip(g, name, c)
        op(op="connect", **{"from": "$" + c}, to="$" + alias, pin="Poses")
        op(op="input", node="$" + alias, pin=2 + i, position=[x, y])
W, R, SP, C = 2.0, 4.5, 7.5, 1.6
blend("stand", [("walk_fwd", 0, W), ("walk_back", 0, -W), ("walk_left", -W, 0), ("walk_right", W, 0),
                ("run_fwd", 0, R), ("run_back", 0, -R), ("run_left", -R, 0), ("run_right", R, 0),
                ("sprint", 0, SP)])
blend("crouch", [("crouch_fwd", 0, C), ("crouch_back", 0, -C), ("crouch_fwd", -C, 0.01), ("crouch_fwd", C, 0.01)])
for base, layer, alias, idle in [("idle", "stand", "standL", "idleC"), ("crouch_idle", "crouch", "crouchL", "cidleC")]:
    clip(g, base, idle)
    op(op="add", type="motion.layer", owner=g, **{"as": alias})
    op(op="connect", **{"from": "$" + idle}, to="$" + alias, pin="Base")
    op(op="connect", **{"from": "$" + layer}, to="$" + alias, pin="Layer")
    op(op="connect", **{"from": "$gw"}, to="$" + alias, pin="Weight")
op(op="add", type="motion.crossfade", owner=g, **{"as": "xf"})
op(op="set", node="$xf", key="Seconds", value=0.3)
op(op="set", node="$xf", key="Easing", value="EaseInOut")
op(op="connect", **{"from": "$standL"}, to="$xf", pin="Off")
op(op="connect", **{"from": "$crouchL"}, to="$xf", pin="On")
op(op="connect", **{"from": "$gcrouch"}, to="$xf", pin="Active")
op(op="add", type="motion.speedScale", owner=g, **{"as": "rate"})
op(op="connect", **{"from": "$xf"}, to="$rate", pin="Pose")
op(op="connect", **{"from": "$grate"}, to="$rate", pin="Speed")
op(op="connect", **{"from": "$rate"}, to="$ground.output", pin="Pose")
op(op="connect", **{"from": "$ggrounded"}, to="$ground.output", pin="Enter")
op(op="gate", state="$ground", exit=True)

# ---- Jump
clip("$jump", "jump", "jumpC", looping="Once", speed=0.9)
op(op="connect", **{"from": "$jumpC"}, to="$jump.output", pin="Pose")
param("$jump", "Jump", "jflag")
op(op="connect", **{"from": "$jflag"}, to="$jump.output", pin="Enter")
op(op="add", type="motion.stateFinished", owner="$jump", **{"as": "jdone"})
op(op="set", node="$jdone", key="Threshold", value=0.9)
op(op="connect", **{"from": "$jdone"}, to="$jump.output", pin="Exit")

# ---- Fall
clip("$fall", "fall", "fallC", looping="Once")
op(op="connect", **{"from": "$fallC"}, to="$fall.output", pin="Pose")
param("$fall", "Grounded", "fgrounded")
op(op="add", type="motion.logic", owner="$fall", **{"as": "fnot"})
op(op="set", node="$fnot", key="Op", value="Not")
op(op="connect", **{"from": "$fgrounded"}, to="$fnot", pin="A")
op(op="connect", **{"from": "$fnot"}, to="$fall.output", pin="Enter")
op(op="gate", state="$fall", exit=True)

# ---- Land
clip("$land", "land", "landC", looping="Once", speed=1.3)
op(op="connect", **{"from": "$landC"}, to="$land.output", pin="Pose")
param("$land", "HardLand", "lflag")
op(op="connect", **{"from": "$lflag"}, to="$land.output", pin="Enter")
op(op="add", type="motion.stateFinished", owner="$land", **{"as": "ldone"})
op(op="set", node="$ldone", key="Threshold", value=0.8)
param("$land", "MoveWeight", "lmove")
op(op="add", type="motion.constFloat", owner="$land", **{"as": "lhalf"})
op(op="set", node="$lhalf", key="Value", value=0.5)
op(op="add", type="motion.floatCompare", owner="$land", **{"as": "lmoving"})
op(op="connect", **{"from": "$lmove"}, to="$lmoving", pin="A")
op(op="connect", **{"from": "$lhalf"}, to="$lmoving", pin="B")
op(op="add", type="motion.logic", owner="$land", **{"as": "lor"})
op(op="set", node="$lor", key="Op", value="Or")
op(op="connect", **{"from": "$ldone"}, to="$lor", pin="A")
op(op="connect", **{"from": "$lmoving"}, to="$lor", pin="B")
op(op="connect", **{"from": "$lor"}, to="$land.output", pin="Exit")

for a, b, d in [("Ground", "Jump", 0.12), ("Ground", "Fall", 0.3),
                ("Jump", "Land", 0.1), ("Jump", "Ground", 0.2), ("Jump", "Fall", 0.35),
                ("Fall", "Land", 0.08), ("Fall", "Ground", 0.18),
                ("Land", "Jump", 0.12), ("Land", "Fall", 0.25), ("Land", "Ground", 0.35)]:
    op(op="transition", machine="$sm", **{"from": a}, to=b, duration=d, easing="EaseInOut")
print(json.dumps(ops))
