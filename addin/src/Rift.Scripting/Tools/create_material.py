# rift-tool: write
# Create a material, optionally by duplicating another. inputs: name, color (#RRGGBB|[r,g,b]), transparency 0-100,
# material_class, from_material (duplicate this one instead of starting empty)
name = arg("name", required=True)
if any(m.Name == name for m in collect(Material)):
    raise ValueError("a material named '%s' already exists" % name)
with tx("create_material"):
    if arg("from_material"):
        m = find_one(collect(Material), arg("from_material"), "material").Duplicate(name)
    else:
        m = doc.GetElement(Material.Create(doc, name))
    c = color(arg("color"))
    if c is not None:
        m.Color = c
    if arg("transparency") is not None:
        m.Transparency = int(arg("transparency"))
    if arg("material_class"):
        m.MaterialClass = arg("material_class")
result = {"id": iid(m.Id), "name": m.Name, "color": rgb(m.Color)}
