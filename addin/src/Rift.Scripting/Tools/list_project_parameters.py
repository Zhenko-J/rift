# rift-tool: read
# Project parameters (Manage > Project Parameters): binding, shared or not, data type, group, categories.
shared = {}
for spe in collect(SharedParameterElement):
    try:
        shared[iid(spe.GetDefinition().Id)] = str(spe.GuidValue)
    except Exception:
        pass
out = []
it = doc.ParameterBindings.ForwardIterator()
it.Reset()
while it.MoveNext():
    try:
        dfn = it.Key
        b = it.Current
        d = {
            "name": dfn.Name,
            "binding": "instance" if isinstance(b, InstanceBinding) else "type",
            "shared": iid(dfn.Id) in shared,
            "guid": shared.get(iid(dfn.Id)),
            "categories": sorted([c.Name for c in b.Categories], key=str),
        }
        try:
            d["data_type"] = LabelUtils.GetLabelForSpec(dfn.GetDataType())
            d["data_type_id"] = dfn.GetDataType().TypeId
        except Exception:
            pass
        try:
            d["group"] = LabelUtils.GetLabelForGroup(dfn.GetGroupTypeId())
        except Exception:
            pass
        out.append(d)
    except Exception:
        pass
result = sorted(out, key=lambda x: str(x["name"]).lower())
