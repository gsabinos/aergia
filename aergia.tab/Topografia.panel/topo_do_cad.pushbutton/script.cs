// Aegia — Topo do CAD
// Cria uma topografia nativa do Revit a partir das curvas de nível 3D de
// um CAD link DWG vinculado ao projeto.
//
// Workflow:
//   1) Usuário escolhe o ImportInstance (DWG link).
//   2) Janela WPF mostra os layers do DWG; usuário marca os de curvas.
//   3) Vértices 3D das curvas dos layers escolhidos são extraídos.
//   4) Convex hull (Andrew's monotone chain) em XY define o boundary.
//   5) Tenta criar Toposolid (Revit 2024+) com SlabShapeEditor.DrawPoint
//      para cada vértice; em caso de falha, cai para TopographySurface.
//
// Códigos de erro:
//   E001 - Nenhum CAD link/import no projeto.
//   E002 - GeometryElement do CAD link retornou null.
//   E003 - DWG sem nenhum layer reconhecível.
//   E101 - Layers selecionados não geraram pontos 3D suficientes (<3).
//   E102 - Falha ao criar Toposolid (mostra Message + fallback aplicado).
//   E103 - Falha também no fallback TopographySurface.
//   E104 - Nenhum Level encontrado para hospedar a Toposolid.
//   E007 - Exceção não tratada no comando.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

using WWindow = System.Windows.Window;
using WButton = System.Windows.Controls.Button;
using WCheckBox = System.Windows.Controls.CheckBox;
using WStackPanel = System.Windows.Controls.StackPanel;
using WScrollViewer = System.Windows.Controls.ScrollViewer;
using WDockPanel = System.Windows.Controls.DockPanel;
using WTextBlock = System.Windows.Controls.TextBlock;
using WTextBox = System.Windows.Controls.TextBox;
using WOrientation = System.Windows.Controls.Orientation;
using WThickness = System.Windows.Thickness;

namespace Aegia_TopoDoCad
{
    [Transaction(TransactionMode.Manual)]
    public class TopoDoCadCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // 1. Selecionar o DWG link.
                ImportInstance dwg = SelecionarDwg(uidoc, doc);
                if (dwg == null) return Result.Cancelled;

                Options optsCheck = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
                if (dwg.get_Geometry(optsCheck) == null)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia [E002]",
                        "O CAD link selecionado retornou geometria nula.");
                    return Result.Cancelled;
                }

                // 2. Descobrir layers e perguntar via WPF (com memória).
                List<string> todosLayers = DescobrirLayers(dwg, doc);
                if (todosLayers.Count == 0)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia [E003]",
                        "Não foi possível ler nenhum layer da geometria do DWG selecionado.");
                    return Result.Cancelled;
                }

                string chaveDwg = ChaveDoCadLink(doc, dwg);
                HashSet<string> previa = MemoriaLayers.Carregar(chaveDwg);
                HashSet<string> layersEscolhidos = JanelaSelecaoLayers.Mostrar(todosLayers, previa, chaveDwg);
                if (layersEscolhidos == null || layersEscolhidos.Count == 0)
                    return Result.Cancelled;
                MemoriaLayers.Salvar(chaveDwg, layersEscolhidos);

                // 3. Extrair vértices 3D dos layers escolhidos (dedup XY).
                List<XYZ> pontos = ExtrairVertices(dwg, doc, layersEscolhidos);
                if (pontos.Count < 3)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia [E101]",
                        $"Os layers selecionados geraram apenas {pontos.Count} ponto(s) 3D — " +
                        "insuficiente para criar topografia.");
                    return Result.Cancelled;
                }

                // 3.5. Pergunta ajuste de Z (escala + offset em metros).
                //      Útil quando o DWG está em unidade diferente ou referencia outro datum.
                double zMinFt = pontos.Min(p => p.Z), zMaxFt = pontos.Max(p => p.Z);
                AjusteZ ajuste = MemoriaAjusteZ.Carregar(chaveDwg);
                AjusteZ novoAjuste = JanelaAjusteZ.Mostrar(zMinFt, zMaxFt, ajuste);
                if (novoAjuste == null) return Result.Cancelled;
                MemoriaAjusteZ.Salvar(chaveDwg, novoAjuste);

                if (Math.Abs(novoAjuste.Escala - 1.0) > 1e-9 || Math.Abs(novoAjuste.OffsetMetros) > 1e-9)
                {
                    double offsetFt = novoAjuste.OffsetMetros / 0.3048;
                    for (int i = 0; i < pontos.Count; i++)
                    {
                        XYZ p = pontos[i];
                        pontos[i] = new XYZ(p.X, p.Y, p.Z * novoAjuste.Escala + offsetFt);
                    }
                }

                // 4. Convex hull em XY (Andrew's monotone chain) para o boundary.
                List<XYZ> hull = ConvexHullXY(pontos);
                if (hull.Count < 3)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia [E101]",
                        "Não foi possível formar um boundary (convex hull degenerado).");
                    return Result.Cancelled;
                }

                // 5. Level de host.
                Level levelHost = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .FirstOrDefault();
                if (levelHost == null)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia [E104]",
                        "Nenhum Level encontrado no projeto para hospedar a topografia.");
                    return Result.Cancelled;
                }

                // 6. Criar a topografia (Toposolid → fallback TopographySurface).
                ElementId topoCriado;
                string tipoCriado;
                string aviso = null;
                using (Transaction t = new Transaction(doc, "Aegia: Topo do CAD"))
                {
                    t.Start();
                    topoCriado = CriarTopoNativa(doc, hull, pontos, levelHost, out tipoCriado, out aviso);
                    if (topoCriado == ElementId.InvalidElementId)
                    {
                        t.RollBack();
                        Autodesk.Revit.UI.TaskDialog.Show("Aegia [E103]",
                            $"Falha ao criar a topografia.\n\n{aviso}");
                        return Result.Failed;
                    }
                    t.Commit();
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Topografia criada.");
                sb.AppendLine($"Tipo: {tipoCriado}");
                sb.AppendLine($"Pontos: {pontos.Count}");
                sb.AppendLine($"Boundary (hull): {hull.Count} vértices");
                sb.AppendLine($"Ajuste Z aplicado: escala {novoAjuste.Escala:0.######}, offset {novoAjuste.OffsetMetros:0.###} m");
                if (!string.IsNullOrEmpty(aviso)) sb.AppendLine().Append(aviso);
                Autodesk.Revit.UI.TaskDialog.Show("Aegia", sb.ToString());

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aegia [E007]",
                    $"Exceção não tratada.\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}");
                return Result.Failed;
            }
        }

        // ===================== Seleção do DWG =====================

        private static ImportInstance SelecionarDwg(UIDocument uidoc, Document doc)
        {
            List<ImportInstance> imports = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>()
                .ToList();

            if (imports.Count == 0)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aegia [E001]",
                    "Nenhum CAD link/import encontrado no projeto.\nVincule um DWG da topografia primeiro.");
                return null;
            }
            if (imports.Count == 1) return imports[0];

            try
            {
                Reference r = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new ImportSelectionFilter(),
                    "Clique no CAD link da topografia");
                return doc.GetElement(r) as ImportInstance;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return null;
            }
        }

        private static string ChaveDoCadLink(Document doc, ImportInstance imp)
        {
            string n = null;
            Element tipo = doc.GetElement(imp.GetTypeId());
            if (tipo != null) n = tipo.Name;
            if (string.IsNullOrWhiteSpace(n)) n = imp.Name;
            if (string.IsNullOrWhiteSpace(n)) n = "default";
            n = n.Replace('=', '_').Replace('\n', '_').Replace('\r', '_').Trim();
            return n;
        }

        // ===================== Layers do DWG =====================

        private static List<string> DescobrirLayers(ImportInstance imp, Document doc)
        {
            Options opts = new Options
            {
                ComputeReferences = false,
                DetailLevel = ViewDetailLevel.Fine,
                IncludeNonVisibleObjects = false
            };
            HashSet<string> nomes = new HashSet<string>(StringComparer.Ordinal);
            GeometryElement geo = imp.get_Geometry(opts);
            if (geo == null) return new List<string>();

            foreach (GeometryObject go in geo) WalkParaLayers(go, doc, nomes);
            return nomes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void WalkParaLayers(GeometryObject go, Document doc, HashSet<string> nomes)
        {
            if (go is GeometryInstance gi)
            {
                GeometryElement inner = gi.GetInstanceGeometry();
                if (inner == null) return;
                foreach (GeometryObject g in inner) WalkParaLayers(g, doc, nomes);
                return;
            }
            string nome = NomeDoLayer(go, doc);
            if (!string.IsNullOrWhiteSpace(nome)) nomes.Add(nome);
        }

        private static string NomeDoLayer(GeometryObject go, Document doc)
        {
            ElementId gsId = go.GraphicsStyleId;
            if (gsId == null || gsId == ElementId.InvalidElementId) return null;
            GraphicsStyle gs = doc.GetElement(gsId) as GraphicsStyle;
            Category cat = gs?.GraphicsStyleCategory;
            return cat?.Name;
        }

        // ===================== Extração de vértices =====================

        private static List<XYZ> ExtrairVertices(ImportInstance imp, Document doc, HashSet<string> aceitos)
        {
            Options opts = new Options
            {
                ComputeReferences = false,
                DetailLevel = ViewDetailLevel.Fine,
                IncludeNonVisibleObjects = false
            };
            GeometryElement geo = imp.get_Geometry(opts);
            List<XYZ> brutos = new List<XYZ>(4096);
            if (geo == null) return brutos;

            foreach (GeometryObject go in geo)
                WalkParaVertices(go, Transform.Identity, doc, aceitos, brutos);

            // Dedup por (X, Y) arredondado para ~1 mm em pés. Mantém Z do primeiro.
            Dictionary<long, XYZ> dict = new Dictionary<long, XYZ>(brutos.Count);
            foreach (XYZ p in brutos)
            {
                long kx = (long)Math.Round(p.X * 1000.0);
                long ky = (long)Math.Round(p.Y * 1000.0);
                long key = (kx << 32) ^ (ky & 0xFFFFFFFFL);
                if (!dict.ContainsKey(key)) dict[key] = p;
            }
            return dict.Values.ToList();
        }

        private static void WalkParaVertices(GeometryObject go, Transform xform,
            Document doc, HashSet<string> aceitos, List<XYZ> saida)
        {
            if (go is GeometryInstance gi)
            {
                Transform sub = xform.Multiply(gi.Transform);
                GeometryElement inner = gi.GetInstanceGeometry();
                if (inner == null) return;
                foreach (GeometryObject g in inner) WalkParaVertices(g, sub, doc, aceitos, saida);
                return;
            }

            string layer = NomeDoLayer(go, doc);
            if (layer == null || !aceitos.Contains(layer)) return;

            if (go is PolyLine pl)
            {
                IList<XYZ> pts = pl.GetCoordinates();
                if (pts == null) return;
                for (int i = 0; i < pts.Count; i++) saida.Add(xform.OfPoint(pts[i]));
            }
            else if (go is Curve curve)
            {
                IList<XYZ> pts = curve.Tessellate();
                if (pts == null) return;
                for (int i = 0; i < pts.Count; i++) saida.Add(xform.OfPoint(pts[i]));
            }
            else if (go is Mesh mesh)
            {
                for (int i = 0; i < mesh.Vertices.Count; i++)
                    saida.Add(xform.OfPoint(mesh.Vertices[i]));
            }
            else if (go is Solid solid)
            {
                foreach (Face f in solid.Faces)
                {
                    Mesh fm = f.Triangulate();
                    if (fm == null) continue;
                    for (int i = 0; i < fm.Vertices.Count; i++)
                        saida.Add(xform.OfPoint(fm.Vertices[i]));
                }
            }
        }

        // ===================== Convex hull (Andrew's monotone chain) =====================

        private static List<XYZ> ConvexHullXY(List<XYZ> pts)
        {
            if (pts == null || pts.Count < 3) return new List<XYZ>(pts ?? new List<XYZ>());

            List<XYZ> sorted = pts.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();

            // Lower hull.
            List<XYZ> lower = new List<XYZ>();
            foreach (XYZ p in sorted)
            {
                while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], p) <= 0)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(p);
            }

            // Upper hull.
            List<XYZ> upper = new List<XYZ>();
            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                XYZ p = sorted[i];
                while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], p) <= 0)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(p);
            }

            // Junta lower + upper, removendo último de cada (duplicado).
            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        private static double Cross(XYZ o, XYZ a, XYZ b)
            => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        // ===================== Criação da topografia =====================

        private static ElementId CriarTopoNativa(Document doc, List<XYZ> hull, List<XYZ> pontos,
            Level level, out string tipoCriado, out string aviso)
        {
            tipoCriado = "?";
            aviso = null;

            // Path 1: Toposolid (Revit 2024+).
            try
            {
                ElementId id = CriarToposolid(doc, hull, pontos, level);
                if (id != ElementId.InvalidElementId)
                {
                    tipoCriado = "Toposolid";
                    return id;
                }
            }
            catch (Exception ex)
            {
                Exception raiz = ex;
                while (raiz.InnerException != null) raiz = raiz.InnerException;
                aviso = $"Aegia [E102] Falha Toposolid:\n" +
                        $"  Tipo: {raiz.GetType().FullName}\n" +
                        $"  Msg : {raiz.Message}";
            }

            // Path 2: TopographySurface (legacy).
            try
            {
                ElementId id = CriarTopographySurface(doc, pontos);
                if (id != ElementId.InvalidElementId)
                {
                    tipoCriado = "TopographySurface";
                    if (aviso == null) aviso = "Aegia [E102] Toposolid indisponível — usado fallback TopographySurface.";
                    else aviso += "\nFallback aplicado: TopographySurface.";
                    return id;
                }
            }
            catch (Exception ex2)
            {
                string anterior = aviso ?? "";
                aviso = $"{anterior}\nAegia [E103] Falha TopographySurface: {ex2.Message}".Trim();
            }

            return ElementId.InvalidElementId;
        }

        private static ElementId CriarToposolid(Document doc, List<XYZ> hull, List<XYZ> pontos, Level level)
        {
            ElementId typeId = new FilteredElementCollector(doc)
                .OfClass(typeof(ToposolidType))
                .FirstElementId();
            if (typeId == null || typeId == ElementId.InvalidElementId)
                throw new Exception("Nenhum ToposolidType disponível no projeto.");

            // Detecção precoce de coordenadas fora do envelope numérico do Revit
            // (~30 km da origem). Para Toposolid, AddPoint perde precisão
            // bem antes desse limite e o SlabShapeEditor rejeita os pontos.
            const double LIMITE_FT = 30_000.0 / 0.3048; // ~30 km em pés
            foreach (XYZ p in pontos)
            {
                if (Math.Abs(p.X) > LIMITE_FT || Math.Abs(p.Y) > LIMITE_FT)
                    throw new Exception(
                        $"Coordenadas do DWG estão a {Math.Max(Math.Abs(p.X), Math.Abs(p.Y)) * 0.3048 / 1000:0} km " +
                        "da origem do projeto. Toposolid não suporta isso — TopographySurface é mais tolerante.");
            }

            // Boundary CurveLoop achatado na cota do level.
            double zBase = level.Elevation;
            List<Curve> curvas = new List<Curve>(hull.Count);
            for (int i = 0; i < hull.Count; i++)
            {
                XYZ a = new XYZ(hull[i].X, hull[i].Y, zBase);
                XYZ b = new XYZ(hull[(i + 1) % hull.Count].X, hull[(i + 1) % hull.Count].Y, zBase);
                if (a.DistanceTo(b) < 1e-9) continue;
                curvas.Add(Line.CreateBound(a, b));
            }
            CurveLoop loop = CurveLoop.Create(curvas);

            // SubTransaction para que, se a inserção de pontos falhar,
            // a Toposolid recém-criada seja descartada (não fica entulho no doc).
            using (SubTransaction sub = new SubTransaction(doc))
            {
                sub.Start();
                Toposolid topo = Toposolid.Create(
                    doc, new List<CurveLoop> { loop }, typeId, level.Id);
                if (topo == null)
                {
                    sub.RollBack();
                    throw new Exception("Toposolid.Create retornou null.");
                }

                int inseridos;
                try { inseridos = InserirPontosNaToposolid(topo, pontos); }
                catch (Exception)
                {
                    sub.RollBack();
                    throw;
                }

                if (inseridos == 0)
                {
                    sub.RollBack();
                    throw new Exception("Nenhum ponto pôde ser inserido na Toposolid (API incompatível ou pontos rejeitados).");
                }

                sub.Commit();
                return topo.Id;
            }
        }

        private static int InserirPontosNaToposolid(Toposolid topo, List<XYZ> pontos)
        {
            // Caminho A: Toposolid.AddPoints(IList<XYZ>) — Revit 2024+ moderno.
            MethodInfo mAddPointsTopo = FindMethod(topo.GetType(), "AddPoints", typeof(IList<XYZ>));
            if (mAddPointsTopo != null)
            {
                try
                {
                    mAddPointsTopo.Invoke(topo, new object[] { (IList<XYZ>)pontos });
                    return pontos.Count;
                }
                catch (TargetInvocationException tie)
                {
                    // Pode falhar se algum ponto cair fora do boundary; tenta um a um abaixo.
                    if (!(tie.InnerException is Autodesk.Revit.Exceptions.ArgumentException))
                        throw tie.InnerException ?? tie;
                }
            }

            // Caminho B: Toposolid.AddPoint(XYZ) loop.
            MethodInfo mAddPointTopo = FindMethod(topo.GetType(), "AddPoint", typeof(XYZ));
            if (mAddPointTopo != null) return InvokeLoop(topo, mAddPointTopo, pontos);

            // Caminho C: SlabShapeEditor + AddPoints/AddPoint/DrawPoint.
            SlabShapeEditor editor = topo.GetSlabShapeEditor();
            if (editor == null) return 0;

            // Em Revit 2024+ o editor vem desabilitado para Toposolid recém-criada.
            // Tenta habilitar antes de inserir (Enable / EnableShapeEditing /
            // EnableSlabShapeEditing, em Editor ou Toposolid).
            HabilitarSlabShapeEditor(topo, editor);

            Type tEditor = editor.GetType();
            MethodInfo mAddPointsEd = FindMethod(tEditor, "AddPoints", typeof(IList<XYZ>));
            if (mAddPointsEd != null)
            {
                try { mAddPointsEd.Invoke(editor, new object[] { (IList<XYZ>)pontos }); return pontos.Count; }
                catch (TargetInvocationException tie)
                {
                    if (!(tie.InnerException is Autodesk.Revit.Exceptions.ArgumentException))
                        throw tie.InnerException ?? tie;
                }
            }

            MethodInfo mAddPointEd = FindMethod(tEditor, "AddPoint", typeof(XYZ));
            if (mAddPointEd != null) return InvokeLoop(editor, mAddPointEd, pontos);

            MethodInfo mDrawPointEd = FindMethod(tEditor, "DrawPoint", typeof(XYZ));
            if (mDrawPointEd != null) return InvokeLoop(editor, mDrawPointEd, pontos);

            return 0;
        }

        private static MethodInfo FindMethod(Type t, string nome, Type tipoArg)
        {
            try { return t.GetMethod(nome, new[] { tipoArg }); }
            catch { return null; }
        }

        private static MethodInfo FindMethodNoArgs(Type t, string nome)
        {
            try { return t.GetMethod(nome, Type.EmptyTypes); }
            catch { return null; }
        }

        private static void HabilitarSlabShapeEditor(Toposolid topo, SlabShapeEditor editor)
        {
            // Já habilitado?
            try
            {
                PropertyInfo pIsEnabled = editor.GetType().GetProperty("IsEnabled");
                if (pIsEnabled != null && (bool)pIsEnabled.GetValue(editor)) return;
            }
            catch { }

            // Tenta Enable / EnableShapeEditing no próprio editor.
            foreach (string nome in new[] { "Enable", "EnableShapeEditing", "EnableEdit" })
            {
                MethodInfo m = FindMethodNoArgs(editor.GetType(), nome);
                if (m != null)
                {
                    try { m.Invoke(editor, null); return; } catch { }
                }
            }

            // Tenta no Toposolid.
            foreach (string nome in new[] { "EnableShapeEditing", "EnableSlabShapeEditing", "EnableShape" })
            {
                MethodInfo m = FindMethodNoArgs(topo.GetType(), nome);
                if (m != null)
                {
                    try { m.Invoke(topo, null); return; } catch { }
                }
            }
        }

        private static int InvokeLoop(object alvo, MethodInfo m, List<XYZ> pontos)
        {
            int ok = 0;
            foreach (XYZ p in pontos)
            {
                try { m.Invoke(alvo, new object[] { p }); ok++; }
                catch { /* fora do boundary ou degenerado */ }
            }
            return ok;
        }

        private static ElementId CriarTopographySurface(Document doc, List<XYZ> pontos)
        {
            TopographySurface topo = TopographySurface.Create(doc, pontos);
            if (topo == null) throw new Exception("TopographySurface.Create retornou null.");
            return topo.Id;
        }
    }

    // ===================== Selection filter =====================

    internal class ImportSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) => e is ImportInstance;
        public bool AllowReference(Reference r, XYZ p) => true;
    }

    // ===================== Memória das seleções (compartilhada com Postes na Topo) =====================
    //
    // Arquivo: %APPDATA%\Aegia_PostesTopografia.txt  (mesmo do botão de postes —
    // a seleção de layers serve para os dois fluxos no mesmo DWG).

    internal static class MemoriaLayers
    {
        private static string CaminhoArquivo()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(baseDir, "Aegia_PostesTopografia.txt");
        }

        private static Dictionary<string, HashSet<string>> CarregarTudo()
        {
            Dictionary<string, HashSet<string>> mapa =
                new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            try
            {
                string path = CaminhoArquivo();
                if (!File.Exists(path)) return mapa;

                string chaveAtual = null;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string linha = raw == null ? "" : raw.Trim();
                    if (linha.Length == 0) continue;
                    if (linha[0] == '=')
                    {
                        chaveAtual = linha.Substring(1).Trim();
                        if (!mapa.ContainsKey(chaveAtual))
                            mapa[chaveAtual] = new HashSet<string>(StringComparer.Ordinal);
                    }
                    else if (chaveAtual != null)
                    {
                        mapa[chaveAtual].Add(linha);
                    }
                }
            }
            catch { }
            return mapa;
        }

        public static HashSet<string> Carregar(string chave)
        {
            if (string.IsNullOrWhiteSpace(chave)) return null;
            Dictionary<string, HashSet<string>> tudo = CarregarTudo();
            HashSet<string> set;
            return tudo.TryGetValue(chave, out set) ? set : null;
        }

        public static void Salvar(string chave, HashSet<string> selecionados)
        {
            if (string.IsNullOrWhiteSpace(chave) || selecionados == null) return;
            try
            {
                Dictionary<string, HashSet<string>> tudo = CarregarTudo();
                tudo[chave] = new HashSet<string>(selecionados, StringComparer.Ordinal);

                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, HashSet<string>> kv in tudo)
                {
                    sb.Append('=').AppendLine(kv.Key);
                    foreach (string layer in kv.Value.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                        sb.AppendLine(layer);
                }
                File.WriteAllText(CaminhoArquivo(), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    // ===================== Ajuste de Z (escala + offset) =====================

    internal class AjusteZ
    {
        public double Escala = 1.0;
        public double OffsetMetros = 0.0;
    }

    internal static class MemoriaAjusteZ
    {
        private static string CaminhoArquivo()
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(baseDir, "Aegia_TopoZAjuste.txt");
        }

        private static Dictionary<string, AjusteZ> CarregarTudo()
        {
            Dictionary<string, AjusteZ> mapa = new Dictionary<string, AjusteZ>(StringComparer.Ordinal);
            try
            {
                string path = CaminhoArquivo();
                if (!File.Exists(path)) return mapa;

                string chaveAtual = null;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string l = raw == null ? "" : raw.Trim();
                    if (l.Length == 0) continue;
                    if (l[0] == '=')
                    {
                        chaveAtual = l.Substring(1).Trim();
                        if (!mapa.ContainsKey(chaveAtual)) mapa[chaveAtual] = new AjusteZ();
                    }
                    else if (chaveAtual != null)
                    {
                        int eq = l.IndexOf('=');
                        if (eq <= 0) continue;
                        string chave = l.Substring(0, eq).Trim();
                        string val = l.Substring(eq + 1).Trim();
                        double d;
                        if (!double.TryParse(val, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out d)) continue;
                        if (chave == "scale") mapa[chaveAtual].Escala = d;
                        else if (chave == "offset") mapa[chaveAtual].OffsetMetros = d;
                    }
                }
            }
            catch { }
            return mapa;
        }

        public static AjusteZ Carregar(string chave)
        {
            if (string.IsNullOrWhiteSpace(chave)) return new AjusteZ();
            Dictionary<string, AjusteZ> tudo = CarregarTudo();
            AjusteZ a;
            return tudo.TryGetValue(chave, out a) ? a : new AjusteZ();
        }

        public static void Salvar(string chave, AjusteZ ajuste)
        {
            if (string.IsNullOrWhiteSpace(chave) || ajuste == null) return;
            try
            {
                Dictionary<string, AjusteZ> tudo = CarregarTudo();
                tudo[chave] = ajuste;

                StringBuilder sb = new StringBuilder();
                System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
                foreach (KeyValuePair<string, AjusteZ> kv in tudo)
                {
                    sb.Append('=').AppendLine(kv.Key);
                    sb.AppendLine($"scale={kv.Value.Escala.ToString(inv)}");
                    sb.AppendLine($"offset={kv.Value.OffsetMetros.ToString(inv)}");
                }
                File.WriteAllText(CaminhoArquivo(), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }

    internal class JanelaAjusteZ : WWindow
    {
        private WTextBox _txtEscala;
        private WTextBox _txtOffset;
        public AjusteZ Resultado { get; private set; } = null;

        private JanelaAjusteZ(double zMinFt, double zMaxFt, AjusteZ inicial)
        {
            Title = "Ajuste de Z (opcional)";
            Width = 460; Height = 320;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            ResizeMode = System.Windows.ResizeMode.NoResize;
            Topmost = true;

            double zMinM = zMinFt * 0.3048;
            double zMaxM = zMaxFt * 0.3048;
            double escalaAtual = inicial?.Escala ?? 1.0;
            double offsetAtual = inicial?.OffsetMetros ?? 0.0;
            double zMinAjM = zMinM * escalaAtual + offsetAtual;
            double zMaxAjM = zMaxM * escalaAtual + offsetAtual;

            WDockPanel root = new WDockPanel { LastChildFill = false };
            Content = root;

            WTextBlock info = new WTextBlock
            {
                Text =
                    "Z extraído do DWG (em metros): " +
                    $"{zMinM:0.###} → {zMaxM:0.###} m\n" +
                    "Aplique escala (multiplicador) e/ou offset (somado) ao Z de cada ponto.\n" +
                    "Casos comuns:\n" +
                    "  • DWG em metros lido como pés → escala = 3.28084\n" +
                    "  • DWG em milímetros lido como pés → escala = 0.0032808\n" +
                    "  • Datum diferente → use só offset.",
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new WThickness(10, 10, 10, 5)
            };
            WDockPanel.SetDock(info, System.Windows.Controls.Dock.Top);
            root.Children.Add(info);

            WStackPanel painelEscala = new WStackPanel
            {
                Orientation = WOrientation.Horizontal,
                Margin = new WThickness(10, 5, 10, 2)
            };
            painelEscala.Children.Add(new WTextBlock
            {
                Text = "Escala (z'=z×s):",
                Width = 140,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            });
            _txtEscala = new WTextBox
            {
                Width = 120,
                Height = 22,
                Text = escalaAtual.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            painelEscala.Children.Add(_txtEscala);
            WDockPanel.SetDock(painelEscala, System.Windows.Controls.Dock.Top);
            root.Children.Add(painelEscala);

            WStackPanel painelOffset = new WStackPanel
            {
                Orientation = WOrientation.Horizontal,
                Margin = new WThickness(10, 2, 10, 5)
            };
            painelOffset.Children.Add(new WTextBlock
            {
                Text = "Offset em m (z'+=Δ):",
                Width = 140,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            });
            _txtOffset = new WTextBox
            {
                Width = 120,
                Height = 22,
                Text = offsetAtual.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            painelOffset.Children.Add(_txtOffset);
            WDockPanel.SetDock(painelOffset, System.Windows.Controls.Dock.Top);
            root.Children.Add(painelOffset);

            WTextBlock preview = new WTextBlock
            {
                Text = $"Resultado: {zMinAjM:0.###} → {zMaxAjM:0.###} m",
                Margin = new WThickness(10, 5, 10, 5),
                FontStyle = System.Windows.FontStyles.Italic
            };
            WDockPanel.SetDock(preview, System.Windows.Controls.Dock.Top);
            root.Children.Add(preview);

            System.Windows.Controls.TextChangedEventHandler upd = (s, e) =>
            {
                double escala, offset;
                if (!TryParseInv(_txtEscala.Text, out escala)) escala = 1.0;
                if (!TryParseInv(_txtOffset.Text, out offset)) offset = 0.0;
                double a = zMinM * escala + offset;
                double b = zMaxM * escala + offset;
                preview.Text = $"Resultado: {a:0.###} → {b:0.###} m";
            };
            _txtEscala.TextChanged += upd;
            _txtOffset.TextChanged += upd;

            WStackPanel painelBotoes = new WStackPanel
            {
                Orientation = WOrientation.Horizontal,
                Margin = new WThickness(10),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            WButton btnReset = new WButton
            {
                Content = "Sem ajuste",
                Width = 100, Height = 28,
                Margin = new WThickness(0, 0, 6, 0)
            };
            WButton btnOk = new WButton
            {
                Content = "OK",
                Width = 90, Height = 28,
                Margin = new WThickness(0, 0, 6, 0),
                IsDefault = true
            };
            WButton btnCancel = new WButton
            {
                Content = "Cancelar",
                Width = 90, Height = 28,
                IsCancel = true
            };
            btnReset.Click += (s, e) =>
            {
                _txtEscala.Text = "1";
                _txtOffset.Text = "0";
            };
            btnOk.Click += (s, e) =>
            {
                double escala, offset;
                if (!TryParseInv(_txtEscala.Text, out escala)) escala = 1.0;
                if (!TryParseInv(_txtOffset.Text, out offset)) offset = 0.0;
                if (escala == 0.0) escala = 1.0; // proteção
                Resultado = new AjusteZ { Escala = escala, OffsetMetros = offset };
                Close();
            };
            btnCancel.Click += (s, e) => { Resultado = null; Close(); };
            painelBotoes.Children.Add(btnReset);
            painelBotoes.Children.Add(btnOk);
            painelBotoes.Children.Add(btnCancel);
            WDockPanel.SetDock(painelBotoes, System.Windows.Controls.Dock.Bottom);
            root.Children.Add(painelBotoes);
        }

        private static bool TryParseInv(string s, out double valor)
        {
            return double.TryParse((s ?? "").Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out valor);
        }

        public static AjusteZ Mostrar(double zMinFt, double zMaxFt, AjusteZ inicial)
        {
            JanelaAjusteZ win = new JanelaAjusteZ(zMinFt, zMaxFt, inicial);
            win.ShowDialog();
            return win.Resultado;
        }
    }

    // ===================== Janela WPF de seleção de layers =====================

    internal class JanelaSelecaoLayers : WWindow
    {
        private readonly List<WCheckBox> _checks = new List<WCheckBox>();
        private WTextBox _txtBusca;
        public HashSet<string> Selecionados { get; private set; } = null;

        private JanelaSelecaoLayers(List<string> layers, HashSet<string> previaSelecao, string chaveDwg)
        {
            Title = string.IsNullOrEmpty(chaveDwg)
                ? "Selecione os layers da topografia"
                : $"Layers da topografia — {chaveDwg}";
            Width = 420; Height = 560;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            ResizeMode = System.Windows.ResizeMode.CanResize;
            Topmost = true;

            WDockPanel root = new WDockPanel { LastChildFill = true };
            Content = root;

            WTextBlock legenda = new WTextBlock
            {
                Text = "Marque os layers do DWG que representam as curvas de nível 3D. " +
                       "A seleção é lembrada por DWG e compartilhada com 'Postes na Topo'.",
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new WThickness(10, 10, 10, 5)
            };
            WDockPanel.SetDock(legenda, System.Windows.Controls.Dock.Top);
            root.Children.Add(legenda);

            WDockPanel painelBusca = new WDockPanel
            {
                LastChildFill = true,
                Margin = new WThickness(10, 0, 10, 5)
            };
            WTextBlock lblBusca = new WTextBlock
            {
                Text = "Filtrar:",
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new WThickness(0, 0, 6, 0)
            };
            WDockPanel.SetDock(lblBusca, System.Windows.Controls.Dock.Left);
            painelBusca.Children.Add(lblBusca);
            _txtBusca = new WTextBox { Height = 22 };
            _txtBusca.TextChanged += (s, e) => AplicarFiltro();
            painelBusca.Children.Add(_txtBusca);
            WDockPanel.SetDock(painelBusca, System.Windows.Controls.Dock.Top);
            root.Children.Add(painelBusca);

            WStackPanel painelSel = new WStackPanel
            {
                Orientation = WOrientation.Horizontal,
                Margin = new WThickness(10, 0, 10, 5)
            };
            WButton btnTodos = new WButton
            {
                Content = "Marcar visíveis",
                Margin = new WThickness(0, 0, 6, 0),
                Padding = new WThickness(8, 2, 8, 2)
            };
            WButton btnNenhum = new WButton
            {
                Content = "Desmarcar visíveis",
                Padding = new WThickness(8, 2, 8, 2)
            };
            btnTodos.Click += (s, e) =>
            {
                foreach (WCheckBox c in _checks)
                    if (c.Visibility == System.Windows.Visibility.Visible) c.IsChecked = true;
            };
            btnNenhum.Click += (s, e) =>
            {
                foreach (WCheckBox c in _checks)
                    if (c.Visibility == System.Windows.Visibility.Visible) c.IsChecked = false;
            };
            painelSel.Children.Add(btnTodos);
            painelSel.Children.Add(btnNenhum);
            WDockPanel.SetDock(painelSel, System.Windows.Controls.Dock.Top);
            root.Children.Add(painelSel);

            WStackPanel painelOk = new WStackPanel
            {
                Orientation = WOrientation.Horizontal,
                Margin = new WThickness(10),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            WButton btnOk = new WButton
            {
                Content = "OK",
                Width = 90, Height = 28,
                Margin = new WThickness(0, 0, 6, 0),
                IsDefault = true
            };
            WButton btnCancel = new WButton
            {
                Content = "Cancelar",
                Width = 90, Height = 28,
                IsCancel = true
            };
            btnOk.Click += (s, e) =>
            {
                Selecionados = new HashSet<string>(
                    _checks.Where(c => c.IsChecked == true).Select(c => (string)c.Tag));
                Close();
            };
            btnCancel.Click += (s, e) => { Selecionados = null; Close(); };
            painelOk.Children.Add(btnOk);
            painelOk.Children.Add(btnCancel);
            WDockPanel.SetDock(painelOk, System.Windows.Controls.Dock.Bottom);
            root.Children.Add(painelOk);

            WScrollViewer scroll = new WScrollViewer
            {
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Margin = new WThickness(10, 0, 10, 0)
            };
            WStackPanel stack = new WStackPanel();
            bool temPrevia = previaSelecao != null && previaSelecao.Count > 0;
            foreach (string nome in layers)
            {
                bool marcadoInicialmente = temPrevia ? previaSelecao.Contains(nome) : true;
                WCheckBox cb = new WCheckBox
                {
                    Content = nome,
                    Tag = nome,
                    IsChecked = marcadoInicialmente,
                    Margin = new WThickness(0, 2, 0, 2)
                };
                _checks.Add(cb);
                stack.Children.Add(cb);
            }
            scroll.Content = stack;
            root.Children.Add(scroll);
        }

        private void AplicarFiltro()
        {
            string filtro = _txtBusca?.Text ?? string.Empty;
            filtro = filtro.Trim();
            bool semFiltro = filtro.Length == 0;
            foreach (WCheckBox c in _checks)
            {
                string nome = c.Tag as string ?? string.Empty;
                bool combina = semFiltro || nome.IndexOf(filtro, StringComparison.OrdinalIgnoreCase) >= 0;
                c.Visibility = combina
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
            }
        }

        public static HashSet<string> Mostrar(List<string> layers, HashSet<string> previaSelecao, string chaveDwg)
        {
            JanelaSelecaoLayers win = new JanelaSelecaoLayers(layers, previaSelecao, chaveDwg);
            win.ShowDialog();
            return win.Selecionados;
        }
    }
}
