# rift-tool: write
# Create a rule-based view filter. inputs: name, categories [..], rules [{parameter, operator, value}], match "all"|"any"
# operators: equals, not_equals, contains, not_contains, begins_with, ends_with, greater, greater_or_equal, less,
# less_or_equal, has_value, has_no_value. Numbers are millimetres unless "unit": "raw" (internal units) or "integer"
# (yes/no and integer parameters, equals/not_equals only).
name = arg("name", required=True)
if any(name_of(f) == name for f in collect(FilterElement)):
    raise ValueError("a filter named '%s' already exists" % name)
cats = [category(c) for c in arg("categories", required=True)]
cat_ids = id_list([c.Id for c in cats])
filterable = ParameterFilterUtilities.GetFilterableParametersInCommon(doc, cat_ids)


def param_id(pname):
    for pid in filterable:
        if iid(pid) < 0:
            try:
                label = LabelUtils.GetLabelFor(System.Enum.ToObject(BuiltInParameter, iid(pid)))
            except Exception:
                label = None
            if label == pname or str(System.Enum.ToObject(BuiltInParameter, iid(pid))) == pname:
                return pid, None
        else:
            el = doc.GetElement(pid)
            if el is not None and name_of(el) == pname:
                return pid, el
    raise ValueError("parameter '%s' cannot be filtered on these categories" % pname)


F = ParameterFilterRuleFactory


def rule(r):
    pid, _ = param_id(r.get("parameter"))
    op = r.get("operator", "equals")
    v = r.get("value")
    if op == "has_value":
        return F.CreateHasValueParameterRule(pid)
    if op == "has_no_value":
        return F.CreateHasNoValueParameterRule(pid)
    if isinstance(v, str):
        table = {
            "equals": F.CreateEqualsRule, "not_equals": F.CreateNotEqualsRule, "contains": F.CreateContainsRule,
            "not_contains": F.CreateNotContainsRule, "begins_with": F.CreateBeginsWithRule, "ends_with": F.CreateEndsWithRule,
            "greater": F.CreateGreaterRule, "greater_or_equal": F.CreateGreaterOrEqualRule, "less": F.CreateLessRule,
            "less_or_equal": F.CreateLessOrEqualRule,
        }
        if op not in table:
            raise ValueError("operator '%s' is not available for text" % op)
        return table[op](pid, v)
    if isinstance(v, bool) or (isinstance(v, int) and r.get("unit") == "integer"):
        if op not in ("equals", "not_equals"):
            raise ValueError("operator '%s' is not available for yes/no or integer values" % op)
        table = {"equals": F.CreateEqualsRule, "not_equals": F.CreateNotEqualsRule}
        return table[op](pid, int(v))
    # Numbers are millimetres by default (lengths); "unit": "raw" passes the value in Revit's internal units.
    x = float(v) if r.get("unit") == "raw" else float(v) / MM
    eps = 1e-6
    table = {
        "equals": F.CreateEqualsRule, "not_equals": F.CreateNotEqualsRule, "greater": F.CreateGreaterRule,
        "greater_or_equal": F.CreateGreaterOrEqualRule, "less": F.CreateLessRule, "less_or_equal": F.CreateLessOrEqualRule,
    }
    if op not in table:
        raise ValueError("operator '%s' is not available for numbers" % op)
    return table[op](pid, x, eps)


rules = List[FilterRule]()
for r in arg("rules", []) or []:
    rules.Add(rule(r))
with tx("create_view_filter"):
    pf = ParameterFilterElement.Create(doc, name, cat_ids)
    if rules.Count > 0:
        filters = List[ElementFilter]()
        for r in rules:
            filters.Add(ElementParameterFilter(r))
        combined = filters[0] if filters.Count == 1 else (LogicalOrFilter(filters) if arg("match", "all") == "any" else LogicalAndFilter(filters))
        pf.SetElementFilter(combined)
result = {"id": iid(pf.Id), "name": name, "categories": [c.Name for c in cats], "rules": rules.Count}
