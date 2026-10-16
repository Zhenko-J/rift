# rift-tool: write
# Create a shared parameter definition in the shared parameter file Revit currently uses (Manage > Shared Parameters).
# inputs: name, data_type (text, integer, number, length, area, volume, angle, yesno, url, material, or an
# autodesk.spec id), group (group of the file, created when missing), description, visible=true
# Writes the shared parameter file, not the model: refused when no file is set. Bind it with bind_shared_parameter.
SPECS = {
    "text": SpecTypeId.String.Text, "integer": SpecTypeId.Int.Integer, "number": SpecTypeId.Number,
    "length": SpecTypeId.Length, "area": SpecTypeId.Area, "volume": SpecTypeId.Volume, "angle": SpecTypeId.Angle,
    "yesno": SpecTypeId.Boolean.YesNo, "url": SpecTypeId.String.Url, "material": SpecTypeId.Reference.Material,
}


def spec(name):
    if name in SPECS:
        return SPECS[name]
    if str(name).startswith("autodesk.spec"):
        return ForgeTypeId(name)
    raise ValueError("data_type '%s' unknown (use %s or an autodesk.spec id)" % (name, ", ".join(sorted(SPECS))))


app = doc.Application
if not app.SharedParametersFilename:
    raise ValueError("no shared parameter file is set in Revit (Manage > Shared Parameters)")
df = app.OpenSharedParameterFile()
if df is None:
    raise ValueError("the shared parameter file could not be opened")
name = arg("name", required=True)
for g in df.Groups:
    for d in g.Definitions:
        if d.Name == name:
            raise ValueError("'%s' already exists in group '%s' of the shared parameter file" % (name, g.Name))
gname = arg("group", "RIFT")
data_type = spec(arg("data_type", "text"))
if rift_dry_run:
    # The file is outside the model: the guard cannot roll it back, so a dry run only says what would be written.
    result = {"name": name, "group": gname, "would_create": True, "data_type": data_type.TypeId}
else:
    group = df.Groups.get_Item(gname) or df.Groups.Create(gname)
    opt = ExternalDefinitionCreationOptions(name, data_type)
    opt.Visible = bool(arg("visible", True))
    if arg("description"):
        opt.Description = arg("description")
    d = group.Definitions.Create(opt)
    result = {"name": d.Name, "guid": str(d.GUID), "group": gname, "file": "(the shared parameter file set in Revit)"}
