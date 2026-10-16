// Copyright 2026 Zhenko-J. Licensed under the Apache License, Version 2.0.
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using Rift.Core;

namespace Rift.Addin;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ProbePythonCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        string report = App.ProbePythonText(commandData.Application);
        var dialog = new TaskDialog(Brand.Product + " — P-D4 IronPython probe")
        {
            MainInstruction = "Embedded IronPython 3.4 inside Revit",
            MainContent = report.Length > 1800 ? report[..1800] + "\n…(see report file)" : report,
        };
        dialog.Show();
        return Result.Succeeded;
    }
}
