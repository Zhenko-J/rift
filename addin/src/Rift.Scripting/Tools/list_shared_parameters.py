# rift-tool: read
# Shared parameters present in the model (project parameters and those embedded in loaded families), unique by GUID.
seen = {}
for spe in collect(SharedParameterElement):
    try:
        d = spe.GetDefinition()
        g = str(spe.GuidValue)
        if g in seen:
            continue
        item = {"id": iid(spe.Id), "name": d.Name, "guid": g}
        try:
            item["data_type"] = LabelUtils.GetLabelForSpec(d.GetDataType())
            item["data_type_id"] = d.GetDataType().TypeId
        except Exception:
            pass
        try:
            item["group"] = LabelUtils.GetLabelForGroup(d.GetGroupTypeId())
        except Exception:
            pass
        seen[g] = item
    except Exception:
        pass
result = sorted(seen.values(), key=lambda x: str(x["name"]).lower())
