using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Hatco.PrecastManholeManager.Infrastructure;
using Hatco.PrecastManholeManager.Services;

namespace Hatco.PrecastManholeManager.Commands
{
    // Add-in Manager regression entry point. Requires an explicitly reviewed fixture manifest.
    // Reads the active test model; does not update even the external review registry.
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ValidateDrawingFixturesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData input, ref string message, ElementSet elements)
        {
            var doc = input.Application.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "Select reviewed drawing-fixture manifest", Filter = "Fixture XML (*.xml)|*.xml" };
            if (picker.ShowDialog() != true) return Result.Cancelled;
            using (var log = new DiagnosticLogger())
            {
                try
                {
                    XDocument manifest;
                    using (var reader = XmlReader.Create(picker.FileName, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                        manifest = XDocument.Load(reader);
                    if (manifest.Root?.Name != "drawing-fixtures" || (string)manifest.Root.Attribute("model-title") != doc.Title)
                        throw new InvalidOperationException("Open the exact test model named by the fixture manifest.");
                    var cases = manifest.Root.Elements("case").ToList();
                    if (cases.Count == 0) throw new InvalidOperationException("No fixtures in the manifest.");
                    var audit = new DrawingAuditService(doc, log);
                    string path = Path.ChangeExtension(log.LogPath, ".fixture-results.csv");
                    int passed = 0;
                    using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)) { AutoFlush = true })
                    {
                        writer.WriteLine("Case,FoundationUniqueId,Result,ExpectedCodes,ActualCodes,Details");
                        foreach (var test in cases)
                        {
                            string name = (string)test.Attribute("name"), uid = (string)test.Attribute("foundation-unique-id");
                            var expected = new HashSet<string>(((string)test.Attribute("expected-codes") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()), StringComparer.Ordinal);
                            string details;
                            var actual = new HashSet<string>(StringComparer.Ordinal);
                            bool executed = false;
                            try
                            {
                                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uid) || test.Attribute("expected-codes") == null)
                                    throw new InvalidOperationException("Each case requires a name, foundation UniqueId and expected-codes (empty for pass).");
                                Element foundation = doc.GetElement(uid);
                                if (foundation == null) throw new InvalidOperationException("Fixture foundation is absent; ElementId fallback is not allowed.");
                                var result = audit.Check(foundation);
                                foreach (string issue in result.Issues) actual.Add(issue.Split(':')[0]);
                                if (test.Attribute("expected-views") != null && result.ViewsChecked != (int)test.Attribute("expected-views")) actual.Add("FIXTURE_VIEW_COUNT");
                                if (test.Attribute("expected-managed-openings") != null && result.ManagedOpenings != (int)test.Attribute("expected-managed-openings")) actual.Add("FIXTURE_OPENING_COUNT");
                                if (test.Attribute("expected-base-pinned") != null && foundation.Pinned != (bool)test.Attribute("expected-base-pinned")) actual.Add("FIXTURE_PIN_CHANGED");
                                details = result.Summary; executed = true;
                            }
                            catch (Exception ex) { actual.Add("FIXTURE_ERROR"); details = ex.Message; }
                            bool ok = executed && actual.SetEquals(expected);
                            if (ok) passed++;
                            writer.WriteLine(string.Join(",", new[] { Quote(name), Quote(uid), ok ? "PASS" : "FAIL", Quote(string.Join(";", expected.OrderBy(x => x))), Quote(string.Join(";", actual.OrderBy(x => x))), Quote(details) }));
                        }
                    }
                    TaskDialog.Show("Drawing fixtures", passed + "/" + cases.Count + " matched the reviewed expectations.\nNo model or review-register changes.\n" + path);
                    return Result.Succeeded;
                }
                catch (Exception ex) { log.Error("Fixture validation failed", ex); message = ex.Message; return Result.Failed; }
            }
        }

        private static string Quote(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
    }
}
