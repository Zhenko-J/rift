# rift-tool: write
# Create a project parameter bound to categories. The Revit API cannot create a non-shared project parameter: like the
# other MCP servers, RIFT creates a shared definition in a temporary file, binds it, then restores the shared parameter
# file Revit was using. The parameter is therefore shared (it has a GUID), which the result says.
# inputs: name, data_type (see create_shared_parameter), categories [..], binding "instance"|"type", group (GroupTypeId name)
import System.IO as IO
SPECS = {
    "text": SpecTypeId.String.Text, "integer": SpecTypeId.Int.Integer, "number": SpecTypeId.Number,
    "length": SpecTypeId.Length, "area": SpecTypeId.Area, "volume": SpecTypeId.Volume, "angle": SpecTypeId.Angle,
    "yesno": SpecTypeId.Boolean.YesNo, "url": SpecTypeId.String.Url, "material": SpecTypeId.Reference.Material,
}
name = arg("name", required=True)
it = doc.ParameterBindings.ForwardIterator()
it.Reset()
while it.MoveNext():
    if it.Key.Name == name:
        raise ValueError("a project parameter named '%s' already exists" % name)
dt = arg("data_type", "text")
spec = SPECS.get(dt) or (ForgeTypeId(dt) if str(dt).startswith("autodesk.spec") else None)
if spec is None:
    raise ValueError("data_type '%s' unknown" % dt)
group = getattr(GroupTypeId, arg("group", "Data"), None)
if group is None:
    raise ValueError("group '%s' unknown" % arg("group"))
app = doc.Application
previous = app.SharedParametersFilename
temp = IO.Path.Combine(IO.Path.GetTempPath(), "rift-project-parameter-" + System.Guid.NewGuid().ToString("N") + ".txt")
IO.File.WriteAllText(temp, "")
try:
    app.SharedParametersFilename = temp
    df = app.OpenSharedParameterFile()
    d = df.Groups.Create("RIFT").Definitions.Create(ExternalDefinitionCreationOptions(name, spec))
    cats = app.Create.NewCategorySet()
    for c in arg("categories", required=True):
        cats.Insert(category(c))
    binding = app.Create.NewTypeBinding(cats) if arg("binding", "instance") == "type" else app.Create.NewInstanceBinding(cats)
    with tx("create_project_parameter"):
        if not doc.ParameterBindings.Insert(d, binding, group):
            raise ValueError("Revit refused the binding")
    guid = str(d.GUID)
finally:
    app.SharedParametersFilename = previous or ""
    try:
        IO.File.Delete(temp)
    except Exception:
        pass
result = {"name": name, "shared": True, "guid": guid, "binding": arg("binding", "instance"), "categories": [c.Name for c in cats]}
