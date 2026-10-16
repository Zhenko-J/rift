# rift-tool: read
# Parameters available on the elements of a category: instance parameters of one element and type parameters of its type.
cat = category(arg("category", required=True))
el = FilteredElementCollector(doc).OfCategoryId(cat.Id).WhereElementIsNotElementType().FirstElement()
if el is None:
    result = {"category": cat.Name, "instance": [], "type": [], "note": "no element of this category in the model"}
else:
    t = doc.GetElement(el.GetTypeId())
    def names(e):
        return sorted(set(p.Definition.Name for p in e.Parameters), key=str) if e is not None else []
    result = {"category": cat.Name, "sample_element": iid(el.Id), "instance": names(el), "type": names(t)}
