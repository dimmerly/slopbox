import json
SM = "f8dfa156"
ops = []
def op(**k): ops.append(k)
def param(name, alias):
    op(op="add", type="motion.parameter", **{"as": alias}); op(op="set", node="$"+alias, key="Name", value=name)
for n in ["AimTarget", "RightHandTarget", "LeftHandTarget"]: op(op="param", name=n, kind="Target")
for n in ["AimWeight", "RightHandWeight", "LeftHandWeight"]: op(op="param", name=n, kind="Number", value=0)
param("AimTarget", "pAim"); param("AimWeight", "pAimW")
param("RightHandTarget", "pR"); param("RightHandWeight", "pRW")
param("LeftHandTarget", "pL"); param("LeftHandWeight", "pLW")
op(op="add", type="motion.lookAt", **{"as": "look"})
op(op="connect", **{"from": SM}, to="$look", pin="Pose")
op(op="connect", **{"from": "$pAim"}, to="$look", pin="Target")
op(op="connect", **{"from": "$pAimW"}, to="$look", pin="setting:Weight")
for k, v in [("Clamp", 0.55), ("Body", 0.45), ("Head", 0.9), ("Eyes", 0.0)]: op(op="set", node="$look", key=k, value=v)
prev = "$look"
for side, t, w in [("Right", "$pR", "$pRW"), ("Left", "$pL", "$pLW")]:
    a = "ik" + side
    op(op="add", type="motion.twoBoneIK", **{"as": a})
    op(op="connect", **{"from": prev}, to="$"+a, pin="Pose")
    op(op="connect", **{"from": t}, to="$"+a, pin="Target")
    op(op="connect", **{"from": w}, to="$"+a, pin="Weight")
    for k, b in [("Upper", "UpperArm"), ("Mid", "LowerArm"), ("End", "Hand")]: op(op="set", node="$"+a, key=k, value=side+b)
    prev = "$"+a
op(op="root", node=prev)
print(json.dumps(ops))
