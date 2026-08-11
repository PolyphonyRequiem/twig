#!/usr/bin/env python3
"""Ticket 0001: capture ADO process endpoint payloads + measure descriptor cost.

Run:  python3 probe-all.py
Writes raw payloads to ./raw/, prints a compact summary.
"""
import json, os, pwd, subprocess, sys, time

ORG = "https://dev.azure.com/PolyphonyRequiem"
RES = "499b84ac-1321-427f-aa17-267ca6975798"
RAW = os.path.join(os.path.dirname(os.path.abspath(__file__)), "raw")
os.makedirs(RAW, exist_ok=True)
# az stores its login under the LOGIN user's home. Respect an existing override.
ENV = dict(os.environ)
ENV.setdefault("AZURE_CONFIG_DIR", os.path.join(
    pwd.getpwuid(os.getuid()).pw_dir, ".azure"))

CALLS = 0
BYTES = 0

def get(name, path, count=True):
    """GET org+path, save to raw/<name>.json, return parsed dict or None."""
    global CALLS, BYTES
    url = ORG + path
    p = subprocess.run(["az", "rest", "--method", "get", "--resource", RES, "--url", url],
                       capture_output=True, text=True, env=ENV)
    if p.returncode != 0:
        print(f"  FAIL {name}: {p.stderr.strip().splitlines()[0][:120]}")
        return None
    if count:
        CALLS += 1
        BYTES += len(p.stdout)
    with open(os.path.join(RAW, name + ".json"), "w") as f:
        f.write(p.stdout)
    return json.loads(p.stdout)

INHERITED = ("Niflheim", "7f984e4c-e856-4fc3-8457-fd4e8acf2e57")
STOCK     = ("Basic",    "b8a3a935-7e91-48b8-a94c-606d37c3e9f2")
V = "7.1-preview.1"

def survey(label, pid):
    global CALLS, BYTES
    CALLS = 0; BYTES = 0
    print(f"\n=== {label} ({pid}) ===")

    # process-wide field list
    for ver in ("7.1", V):
        d = get(f"{label}-procfields-{ver}", f"/_apis/work/processes/{pid}/fields?api-version={ver}", count=(ver == V))
        if d is not None:
            cust = [f for f in d["value"] if f["id"].startswith("Custom.")]
            print(f"  process /fields @{ver}: count={d['count']} custom={len(cust)}")

    # types. NOTE: preview.1 returns id/class; preview.2 returns referenceName/customization.
    # We take preview.2 as the descriptive shape and preview.1 for the endpoint-consistent ref.
    t2 = get(f"{label}-types-p2", f"/_apis/work/processes/{pid}/workItemTypes?api-version=7.1-preview.2")
    t = get(f"{label}-types", f"/_apis/work/processes/{pid}/workItemTypes?$expand=none&api-version={V}")
    types = t["value"]
    by_ref = {w["referenceName"]: w for w in (t2["value"] if t2 else [])}
    print(f"  workItemTypes: {len(types)}")
    for wt in types:
        ref = wt["id"]
        d2 = by_ref.get(ref, {})
        print(f"    {ref:<45} class={wt.get('class'):<9} customization={d2.get('customization')!s:<9} inherits={wt.get('inherits')}")

    # expand variants on types
    for ex in ("states", "behaviors", "layout"):
        d = get(f"{label}-types-expand-{ex}", f"/_apis/work/processes/{pid}/workItemTypes?$expand={ex}&api-version={V}")
        if d: print(f"  $expand={ex}: ok ({len(json.dumps(d))} chars)")

    # process-level behaviors
    b = get(f"{label}-behaviors", f"/_apis/work/processes/{pid}/behaviors?api-version=7.1-preview.2")
    if b: print(f"  process /behaviors: {b['count']} -> {[x['name'] for x in b['value']]}")

    # per-type fields + per-type behaviors for every type
    picklist_ids = set()
    per_type_fields = {}
    for wt in types:
        ref = wt["id"]
        slug = ref.replace(".", "_")
        # preview.2 is the RICH shape (required/defaultValue/customization); preview.1 is thin.
        f = get(f"{label}-fields-{slug}", f"/_apis/work/processes/{pid}/workItemTypes/{ref}/fields?api-version=7.1-preview.2")
        if f:
            per_type_fields[ref] = f["value"]
            for fl in f["value"]:
                pl = fl.get("pickList")
                if pl: picklist_ids.add(pl if isinstance(pl, str) else pl.get("id"))
        # per-type behaviors live under workItemTypesBehaviors, NOT workItemTypes/{ref}/behaviors
        get(f"{label}-tbehav-{slug}", f"/_apis/work/processes/{pid}/workItemTypesBehaviors/{ref}/behaviors?api-version=7.1-preview.1")

    # per-type field shape
    if per_type_fields:
        anyref = next(iter(per_type_fields))
        sample = per_type_fields[anyref][0]
        print(f"  per-type /fields attribute keys: {sorted(sample.keys())}")
        allf = [f for v in per_type_fields.values() for f in v]
        print(f"    total per-type field rows: {len(allf)}; "
              f"required=True: {sum(1 for f in allf if f.get('required'))}; "
              f"with defaultValue: {sum(1 for f in allf if f.get('defaultValue') not in (None, ''))}; "
              f"with allowedValues: {sum(1 for f in allf if f.get('allowedValues'))}; "
              f"with pickList: {sum(1 for f in allf if f.get('pickList'))}")

    # picklists
    lists_all = get(f"{label}-lists-all", f"/_apis/work/processes/lists?api-version={V}")
    if lists_all:
        print(f"  /processes/lists (list-all): count={lists_all['count']} "
              f"items-present={[bool(x.get('items')) for x in lists_all['value']]}")
        for pl in lists_all["value"]:
            one = get(f"{label}-list-{pl['id'][:8]}", f"/_apis/work/processes/lists/{pl['id']}?api-version={V}")
            if one:
                print(f"    per-list {one['name']}: items={len(one.get('items') or [])}")
    print(f"  COST: {CALLS} calls, {BYTES} bytes ({BYTES/1024:.1f} KiB)")
    return CALLS, BYTES, len(types)

if __name__ == "__main__":
    for label, pid in (INHERITED, STOCK):
        survey(label, pid)
