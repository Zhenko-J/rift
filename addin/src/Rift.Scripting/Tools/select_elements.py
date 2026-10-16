# rift-tool: read
# Sets the Revit selection to these elements (the model does not change, nothing goes to the undo list). inputs: element_ids
if uidoc is None:
    raise ValueError("no document window is active")
refs = inputs.get("element_ids") if inputs is not None else None
if refs is None:
    raise ValueError("argument 'element_ids' is required (an empty list clears the selection)")
els = [element(r) for r in refs]
uidoc.Selection.SetElementIds(id_list([e.Id for e in els]))
result = {"selected": len(els), "elements": [summary(e) for e in els[:50]]}
