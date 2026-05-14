// Aegia — Postes na Topo
// Loop interativo: usuário clica em FamilyInstances já colocadas e o comando
// move cada uma verticalmente até que o origin point encoste numa topografia
// nativa do Revit (Toposolid ou TopographySurface).
//
// O cálculo de Z é delegado ao próprio Revit via ReferenceIntersector
// (raycast vertical contra a malha da topografia), escalando para qualquer
// tamanho de terreno sem TIN em memória.
//
// Pré-requisito: ter no projeto pelo menos uma Toposolid (OST_Toposolid)
// ou TopographySurface (OST_Topography). Use o botão "Topo do CAD" para
// gerar a topografia a partir do DWG.
//
// Códigos de erro:
//   E005 - Elemento selecionado sem LocationPoint.
//   E007 - Exceção não tratada no comando.
//   E201 - Nenhuma topografia nativa no documento.
//   E202 - Poste fora da topografia (raycast não atingiu).
//   E203 - Falha ao obter/criar View3D para o raycast.

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace Aegia_PostesEmTopo
{
    [Transaction(TransactionMode.Manual)]
    public class PostesEmTopoCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // 1. Coletar topografias do documento.
                List<Element> topos = ColetarTopografias(doc);
                if (topos.Count == 0)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia [E201]",
                        "Nenhuma Toposolid ou TopographySurface encontrada no projeto.\n" +
                        "Use o botão 'Topo do CAD' primeiro para gerar uma topografia.");
                    return Result.Cancelled;
                }

                Element topo = topos.Count == 1 ? topos[0] : EscolherTopografia(uidoc, topos);
                if (topo == null) return Result.Cancelled;

                // 2. Garantir uma View3D para o ReferenceIntersector.
                View3D view3d = ObterOuCriarView3D(doc);
                if (view3d == null)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia [E203]",
                        "Não foi possível obter nem criar uma View3D necessária ao raycast.");
                    return Result.Failed;
                }

                ReferenceIntersector ri = new ReferenceIntersector(
                    topo.Id, FindReferenceTarget.Mesh, view3d);
                ri.FindReferencesInRevitLinks = false;

                // 3. Loop de descida agrupado em um único undo.
                int movidos = 0, foraDoTopo = 0, semLoc = 0;
                using (TransactionGroup tg = new TransactionGroup(doc, "Aegia: Postes na Topo"))
                {
                    tg.Start();

                    while (true)
                    {
                        try
                        {
                            Reference r = uidoc.Selection.PickObject(
                                ObjectType.Element,
                                new PosteSelectionFilter(),
                                "Selecione um poste para descer ao terreno (ESC para finalizar)");

                            FamilyInstance fi = doc.GetElement(r) as FamilyInstance;
                            if (fi == null) continue;

                            LocationPoint lp = fi.Location as LocationPoint;
                            if (lp == null)
                            {
                                semLoc++;
                                Autodesk.Revit.UI.TaskDialog.Show("Aegia [E005]",
                                    "Este elemento não tem LocationPoint (não é uma família point-based). Pulado.");
                                continue;
                            }
                            XYZ origem = lp.Point;

                            // Lança o raio de bem alto pra baixo (1 km em pés ≈ 3280 ft).
                            XYZ start = new XYZ(origem.X, origem.Y, origem.Z + 3280.0);
                            ReferenceWithContext rwc = ri.FindNearest(start, new XYZ(0, 0, -1));
                            if (rwc == null)
                            {
                                foraDoTopo++;
                                Autodesk.Revit.UI.TaskDialog.Show("Aegia [E202]",
                                    "Este poste está fora da topografia (raycast não atingiu a malha). Pulado.");
                                continue;
                            }

                            double zTerreno = rwc.GetReference().GlobalPoint.Z;
                            double dz = zTerreno - origem.Z;
                            if (Math.Abs(dz) < 1e-6) { movidos++; continue; }

                            using (Transaction t = new Transaction(doc, "Descer poste"))
                            {
                                t.Start();
                                ElementTransformUtils.MoveElement(doc, fi.Id, new XYZ(0, 0, dz));
                                t.Commit();
                            }
                            movidos++;
                        }
                        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                        {
                            break;
                        }
                    }

                    tg.Assimilate();
                }

                Autodesk.Revit.UI.TaskDialog.Show("Aegia",
                    $"Concluído.\n" +
                    $"Postes processados: {movidos}\n" +
                    $"Sem LocationPoint (E005): {semLoc}\n" +
                    $"Fora da topografia (E202): {foraDoTopo}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aegia [E007]",
                    $"Exceção não tratada.\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}");
                return Result.Failed;
            }
        }

        // ===================== Topografias do projeto =====================

        private static List<Element> ColetarTopografias(Document doc)
        {
            List<Element> resultado = new List<Element>();

            // TopographySurface (legacy).
            ICollection<Element> topoSurfs = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Topography)
                .WhereElementIsNotElementType()
                .ToElements();
            foreach (Element e in topoSurfs) resultado.Add(e);

            // Toposolid (Revit 2024+). Protege por try/catch caso o enum
            // OST_Toposolid não exista em alguma das versões alvo.
            try
            {
                ICollection<Element> toposolids = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Toposolid)
                    .WhereElementIsNotElementType()
                    .ToElements();
                foreach (Element e in toposolids) resultado.Add(e);
            }
            catch
            {
                // Versão sem OST_Toposolid — silencioso.
            }

            return resultado;
        }

        private static Element EscolherTopografia(UIDocument uidoc, List<Element> candidatas)
        {
            try
            {
                HashSet<ElementId> ids = new HashSet<ElementId>(candidatas.Select(e => e.Id));
                Reference r = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new TopografiaSelectionFilter(ids),
                    "Clique na topografia (Toposolid ou TopographySurface) a ser usada");
                return uidoc.Document.GetElement(r);
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        // ===================== View3D para o raycast =====================

        private static View3D ObterOuCriarView3D(Document doc)
        {
            // Reusa qualquer View3D não-template existente.
            View3D existente = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => v != null && !v.IsTemplate);
            if (existente != null) return existente;

            // Senão, cria uma 3D isométrica permanente.
            ViewFamilyType vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
            if (vft == null) return null;

            View3D novo = null;
            using (Transaction t = new Transaction(doc, "Aegia: View3D para raycast"))
            {
                t.Start();
                novo = View3D.CreateIsometric(doc, vft.Id);
                if (novo != null)
                {
                    try { novo.Name = "Aegia_Temp_Projecao"; } catch { /* nome em uso */ }
                }
                t.Commit();
            }
            return novo;
        }
    }

    // ===================== Selection filters =====================

    internal class PosteSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element e)
        {
            FamilyInstance fi = e as FamilyInstance;
            if (fi == null) return false;
            return fi.Location is LocationPoint;
        }
        public bool AllowReference(Reference r, XYZ p) => true;
    }

    internal class TopografiaSelectionFilter : ISelectionFilter
    {
        private readonly HashSet<ElementId> _ids;
        public TopografiaSelectionFilter(HashSet<ElementId> ids) { _ids = ids; }
        public bool AllowElement(Element e) => e != null && _ids.Contains(e.Id);
        public bool AllowReference(Reference r, XYZ p) => true;
    }
}
