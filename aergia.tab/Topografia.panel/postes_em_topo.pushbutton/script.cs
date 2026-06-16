// Aegia — Postes na Topo  (+ Postes para NWC via Shift)
//
// Clique normal: loop interativo — usuário clica em FamilyInstances já colocadas
// e o comando move cada uma verticalmente até encostar numa topografia nativa do
// Revit (Toposolid ou TopographySurface), esteja ela NO MODELO ou em um LINK.
// O cálculo de Z é delegado ao Revit via ReferenceIntersector (raycast vertical),
// filtrado por categoria de topografia. Funciona com várias topografias.
//
// Seleção: pré-seleção nativa do Revit (arraste/rubber-band, Ctrl) tem prioridade;
// sem nada selecionado entra num loop — clique = aquele poste, Shift+clique = todas
// as instâncias daquele tipo no documento, ESC finaliza. Os postes acumulados ficam
// destacados em LARANJA na view ativa enquanto se seleciona; o destaque é removido
// ao concluir ou cancelar (temporário, nada permanente fica na view).
//
// Shift+clique no botão da ribbon: abre janela com ABAS:
//   - Aba "Link-terreno": designa UM link cujo conteúdo INTEIRO (Generic Model
//     incluso) vira terreno. Salvo global em %APPDATA%, independe do modelo.
//   - Aba "Postes para NWC": round-trip Revit ↔ Navisworks via CSV (a API do Revit
//     NÃO lê geometria de Coordination Model). Modos:
//       Exportar      — coleta postes e grava CSV (unique_id;x_m;y_m;z_m).
//       Aplicar Z     — lê CSV de retorno (unique_id;z_m) e desce os postes.
//       Aplicar Transform — lê CSV (element_id;dx;dy;dz;rotz) e move/gira.
//
// Códigos de erro:
//   E005 - Elemento selecionado sem LocationPoint.
//   E007 - Exceção não tratada no comando.
//   E201 - Nenhuma topografia (modelo, links nem link-terreno configurado).
//   E202 - Poste fora de toda topografia (raycast não atingiu).
//   E203 - Falha ao obter/criar View3D para o raycast.
//   E301 - Nenhum poste selecionado (modo Exportar NWC).
//   E302 - Falha de IO ao escrever CSV.
//   E303 - Falha ao ler CSV (formato).
//   E304 - UniqueId do CSV não encontrado no doc.
//   E305 - Sem coordenada compartilhada (ProjectPosition falhou).
//   E306 - element_id do CSV não encontrado no doc (modo Aplicar Transform).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

using WWindow = System.Windows.Window;
using WButton = System.Windows.Controls.Button;
using WListBox = System.Windows.Controls.ListBox;
using WRadioButton = System.Windows.Controls.RadioButton;
using WStackPanel = System.Windows.Controls.StackPanel;
using WDockPanel = System.Windows.Controls.DockPanel;
using WTextBlock = System.Windows.Controls.TextBlock;
using WTabControl = System.Windows.Controls.TabControl;
using WTabItem = System.Windows.Controls.TabItem;
using WOrientation = System.Windows.Controls.Orientation;
using WThickness = System.Windows.Thickness;

namespace Aegia_PostesEmTopo
{
    [Transaction(TransactionMode.Manual)]
    public class PostesEmTopoCommand : IExternalCommand
    {
        private const double FT = 0.3048; // metros por pé (interno do Revit é pé)

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // 0. Shift ao clicar no botão da ribbon → janela com abas
                //    (config do link-terreno + funções Postes para NWC).
                if ((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0)
                {
                    JanelaPostesShift jan = new JanelaPostesShift(doc);
                    jan.ShowDialog();
                    if (jan.ModoNwc == ModoCM.Cancelar)
                        return Result.Succeeded; // aba de config já agiu inline

                    // ProjectPosition na origem (XYZ.Zero) — transformação
                    // interno↔compartilhada (em pés). Vale pra todo o modelo se a
                    // rotação for igual; postes geralmente em terreno plano, OK.
                    double posEW, posNS, posEl, cosA, sinA;
                    try
                    {
                        ProjectPosition pos =
                            doc.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
                        posEW = pos.EastWest;
                        posNS = pos.NorthSouth;
                        posEl = pos.Elevation;
                        cosA = Math.Cos(pos.Angle);
                        sinA = Math.Sin(pos.Angle);
                    }
                    catch (Exception exP)
                    {
                        Autodesk.Revit.UI.TaskDialog.Show("Aegia [E305]",
                            "Falha ao ler ProjectPosition (coordenada compartilhada).\n\n" +
                            exP.Message);
                        return Result.Failed;
                    }

                    if (jan.ModoNwc == ModoCM.Exportar)
                        return Exportar(uidoc, doc, posEW, posNS, posEl, cosA, sinA);
                    else if (jan.ModoNwc == ModoCM.AplicarZ)
                        return AplicarZ(doc, posEW, posNS, posEl, cosA, sinA);
                    else
                        return AplicarTransform(doc, posEW, posNS, posEl, cosA, sinA);
                }

                // 1. Resolver link-terreno configurado (geometria inteira = terreno).
                List<ElementId> linkTerreno = ResolverLinkInstances(doc);

                // Checagem: existe topografia (categoria) OU link-terreno configurado?
                if (!ExisteAlgumaTopografia(doc) && linkTerreno.Count == 0)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia [E201]",
                        "Nenhuma topografia encontrada — sem Toposolid/TopographySurface " +
                        "no modelo ou links, e sem link-terreno configurado.\n\n" +
                        "Use 'Topo do CAD', carregue o link da topografia, ou configure " +
                        "um link como terreno (Shift+clique neste botão).");
                    return Result.Cancelled;
                }

                // 2. Garantir uma View3D para o ReferenceIntersector.
                View3D view3d = ObterOuCriarView3D(doc);
                if (view3d == null)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia [E203]",
                        "Não foi possível obter nem criar uma View3D necessária ao raycast.");
                    return Result.Failed;
                }

                // 3. Intersectors:
                //    (a) riCat — categoria de topo no host/links (aceita todos os hits).
                //    (b) riAll — sem filtro, só usado p/ o link-terreno: aceita hit
                //        apenas se a reference vier de um RevitLinkInstance configurado
                //        (assim QUALQUER geometria do link, Generic Model incluso, vira terreno).
                ReferenceIntersector riCat = new ReferenceIntersector(
                    FiltroTopografia(), FindReferenceTarget.Element, view3d);
                riCat.FindReferencesInRevitLinks = true;

                ReferenceIntersector riAll = null;
                HashSet<ElementId> linkSet = null;
                if (linkTerreno.Count > 0)
                {
                    riAll = new ReferenceIntersector(view3d);
                    riAll.FindReferencesInRevitLinks = true;
                    linkSet = new HashSet<ElementId>(linkTerreno);
                }

                // 4. Coletar os postes (pré-seleção ou loop interativo c/ Shift).
                //    Postes acumulados ficam destacados em laranja durante a coleta.
                List<ElementId> postes = ColetarPostes(uidoc, doc);
                if (postes.Count == 0) return Result.Cancelled; // ESC sem nada — silencioso

                // 5. Descida em lote, agrupada num único undo. O reset do destaque
                //    laranja fica num try/finally FORA do grupo (transação própria),
                //    pra sobreviver a rollback/erro e nunca deixar postes coloridos.
                try
                {
                    int movidos = 0, foraDoTopo = 0, semLoc = 0;
                    using (TransactionGroup tg = new TransactionGroup(doc, "Aegia: Postes na Topo"))
                    {
                        tg.Start();
                        foreach (ElementId id in postes)
                        {
                            FamilyInstance fi = doc.GetElement(id) as FamilyInstance;
                            if (fi == null) continue;

                            LocationPoint lp = fi.Location as LocationPoint;
                            if (lp == null) { semLoc++; continue; }
                            XYZ origem = lp.Point;

                            double? zTerreno = ProjetarZ(riCat, riAll, linkSet, origem);
                            if (zTerreno == null) { foraDoTopo++; continue; }

                            double dz = zTerreno.Value - origem.Z;
                            if (Math.Abs(dz) < 1e-6) { movidos++; continue; }

                            using (Transaction t = new Transaction(doc, "Descer poste"))
                            {
                                t.Start();
                                ElementTransformUtils.MoveElement(doc, fi.Id, new XYZ(0, 0, dz));
                                t.Commit();
                            }
                            movidos++;
                        }
                        tg.Assimilate();
                    }

                    // 6. Diálogo só quando houve postes pulados (erro). Sucesso = silencioso.
                    if (semLoc > 0 || foraDoTopo > 0)
                    {
                        Autodesk.Revit.UI.TaskDialog.Show("Aegia",
                            $"Concluído com avisos.\n" +
                            $"Movidos: {movidos}\n" +
                            $"Sem LocationPoint (E005): {semLoc}\n" +
                            $"Fora da topografia (E202): {foraDoTopo}");
                    }
                    return Result.Succeeded;
                }
                finally
                {
                    AplicarDestaque(doc, postes, false); // remove o laranja
                }
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aegia [E007]",
                    $"Exceção não tratada.\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}");
                return Result.Failed;
            }
        }

        // ===================== Modo Exportar (NWC) =====================

        private static Result Exportar(UIDocument uidoc, Document doc,
            double posEW, double posNS, double posEl, double cosA, double sinA)
        {
            // ColetarPostes aplica o destaque laranja; o try/finally garante o
            // reset em qualquer saída (cancelamento, erro de IO ou sucesso).
            List<ElementId> postes = ColetarPostes(uidoc, doc);
            try
            {
                if (postes.Count == 0)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia [E301]",
                        "Nenhum poste selecionado.");
                    return Result.Cancelled;
                }

                Microsoft.Win32.SaveFileDialog sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Salvar CSV de postes (saída p/ Navisworks)",
                    Filter = "CSV (*.csv)|*.csv|Todos (*.*)|*.*",
                    FileName = "postes_para_navis.csv",
                    OverwritePrompt = true
                };
                if (sfd.ShowDialog() != true) return Result.Cancelled;

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("unique_id;x_m;y_m;z_m");
                int n = 0, semLoc = 0;
                foreach (ElementId id in postes)
                {
                    Element el = doc.GetElement(id);
                    if (el == null) continue;
                    LocationPoint lp = el.Location as LocationPoint;
                    if (lp == null) { semLoc++; continue; }

                    XYZ sFt = InternalToShared(lp.Point, posEW, posNS, posEl, cosA, sinA);
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0};{1:0.######};{2:0.######};{3:0.######}",
                        el.UniqueId,
                        sFt.X * FT,
                        sFt.Y * FT,
                        sFt.Z * FT));
                    n++;
                }

                try
                {
                    File.WriteAllText(sfd.FileName, sb.ToString(), new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia [E302]",
                        "Falha ao escrever CSV.\n\n" + ex.Message);
                    return Result.Failed;
                }

                StringBuilder msg = new StringBuilder();
                msg.AppendLine($"{n} poste(s) exportado(s) para:");
                msg.AppendLine(sfd.FileName);
                if (semLoc > 0)
                    msg.AppendLine($"\n{semLoc} sem LocationPoint (ignorado).");
                msg.AppendLine();
                msg.AppendLine("Agora abra o NWD na Navisworks, rode o plugin " +
                               "'Aergia Postes CM' apontando este CSV, e volte " +
                               "aqui escolhendo 'Aplicar Z'.");
                Autodesk.Revit.UI.TaskDialog.Show("Aergia — Exportado", msg.ToString());
                return Result.Succeeded;
            }
            finally
            {
                AplicarDestaque(doc, postes, false); // remove o laranja
            }
        }

        // ===================== Modo Aplicar Z (NWC) =====================

        private static Result AplicarZ(Document doc,
            double posEW, double posNS, double posEl, double cosA, double sinA)
        {
            Microsoft.Win32.OpenFileDialog ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Abrir CSV de retorno do Navisworks",
                Filter = "CSV (*.csv)|*.csv|Todos (*.*)|*.*"
            };
            if (ofd.ShowDialog() != true) return Result.Cancelled;

            List<KeyValuePair<string, double>> linhas;
            try
            {
                linhas = LerCsvRetorno(ofd.FileName);
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aegia [E303]",
                    "Falha ao ler CSV.\n\n" + ex.Message);
                return Result.Failed;
            }
            if (linhas.Count == 0)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aegia [E303]",
                    "CSV sem linhas válidas.");
                return Result.Cancelled;
            }

            int movidos = 0, naoEncontrado = 0, semLoc = 0, semZ = 0;
            using (TransactionGroup tg = new TransactionGroup(doc,
                "Aergia: Postes no Modelo de Coordenação"))
            {
                tg.Start();
                foreach (var kv in linhas)
                {
                    if (double.IsNaN(kv.Value)) { semZ++; continue; }

                    Element el;
                    try { el = doc.GetElement(kv.Key); }
                    catch { el = null; }
                    if (el == null) { naoEncontrado++; continue; }

                    LocationPoint lp = el.Location as LocationPoint;
                    if (lp == null) { semLoc++; continue; }

                    // Z em compartilhadas (pés) -> alvo interno (pés) preservando XY.
                    double zSharedFt = kv.Value / FT;
                    XYZ sAtual = InternalToShared(lp.Point, posEW, posNS, posEl, cosA, sinA);
                    XYZ sAlvo = new XYZ(sAtual.X, sAtual.Y, zSharedFt);
                    XYZ iAlvo = SharedToInternal(sAlvo, posEW, posNS, posEl, cosA, sinA);
                    double dz = iAlvo.Z - lp.Point.Z;
                    if (Math.Abs(dz) < 1e-6) { movidos++; continue; }

                    using (Transaction t = new Transaction(doc, "Aergia: Descer poste"))
                    {
                        t.Start();
                        ElementTransformUtils.MoveElement(doc, el.Id, new XYZ(0, 0, dz));
                        t.Commit();
                    }
                    movidos++;
                }
                tg.Assimilate();
            }

            if (naoEncontrado > 0 || semLoc > 0 || semZ > 0)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Concluído com avisos.");
                sb.AppendLine($"Movidos: {movidos}");
                if (semZ > 0) sb.AppendLine($"Sem hit no Navis (z=NaN): {semZ}");
                if (naoEncontrado > 0) sb.AppendLine($"UniqueId não encontrado [E304]: {naoEncontrado}");
                if (semLoc > 0) sb.AppendLine($"Sem LocationPoint: {semLoc}");
                Autodesk.Revit.UI.TaskDialog.Show("Aergia", sb.ToString());
            }
            return Result.Succeeded;
        }

        // ===================== Modo Aplicar Transform (NWC) =====================

        private static Result AplicarTransform(Document doc,
            double posEW, double posNS, double posEl, double cosA, double sinA)
        {
            Microsoft.Win32.OpenFileDialog ofd = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Abrir CSV de transforms do Navisworks",
                Filter = "CSV (*.csv)|*.csv|Todos (*.*)|*.*"
            };
            if (ofd.ShowDialog() != true) return Result.Cancelled;

            List<LinhaTransform> linhas;
            try
            {
                linhas = LerCsvTransform(ofd.FileName);
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aegia [E303]",
                    "Falha ao ler CSV.\n\n" + ex.Message);
                return Result.Failed;
            }
            if (linhas.Count == 0)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aegia [E303]",
                    "CSV sem linhas válidas.");
                return Result.Cancelled;
            }

            // Pré-processa: caminha SuperComponent até a raiz da família e
            // deduplica (várias linhas do CSV de sub-componentes do mesmo
            // poste resolvem pra mesma raiz e aplicam só uma vez).
            int naoEncontrado = 0, remapeados = 0, duplicados = 0;
            var dedup = new Dictionary<ElementId, LinhaTransform>();
            foreach (LinhaTransform lt in linhas)
            {
                ElementId id;
                try { id = new ElementId(lt.ElementIdLong); }
                catch { naoEncontrado++; continue; }

                Element el;
                try { el = doc.GetElement(id); }
                catch { el = null; }
                if (el == null) { naoEncontrado++; continue; }

                Element raiz = ResolverRaiz(el);
                if (raiz.Id != id) remapeados++;

                if (dedup.ContainsKey(raiz.Id)) { duplicados++; continue; }
                dedup[raiz.Id] = lt;
            }

            int movidos = 0, rotacionados = 0, semLoc = 0;
            using (TransactionGroup tg = new TransactionGroup(doc,
                "Aergia: Postes CM — Aplicar Transform"))
            {
                tg.Start();
                foreach (KeyValuePair<ElementId, LinhaTransform> kv in dedup)
                {
                    ElementId id = kv.Key;
                    LinhaTransform lt = kv.Value;

                    Element el = doc.GetElement(id);
                    if (el == null) continue;

                    LocationPoint lp = el.Location as LocationPoint;
                    if (lp == null) { semLoc++; continue; }

                    // Vetor (dx,dy,dz) em metros, coords compartilhadas.
                    // Para vetor (não ponto): só aplica rotação inversa (-Angle).
                    double dxIntFt = ( lt.Dx * cosA + lt.Dy * sinA) / FT;
                    double dyIntFt = (-lt.Dx * sinA + lt.Dy * cosA) / FT;
                    double dzIntFt =   lt.Dz / FT;
                    XYZ desloc = new XYZ(dxIntFt, dyIntFt, dzIntFt);
                    bool houveMove = desloc.GetLength() > 1e-9;
                    bool houveRot = Math.Abs(lt.RotZ) > 1e-9;

                    if (!houveMove && !houveRot) continue;

                    using (Transaction t = new Transaction(doc, "Aergia: Mover/Girar poste"))
                    {
                        t.Start();
                        if (houveMove)
                        {
                            ElementTransformUtils.MoveElement(doc, id, desloc);
                            movidos++;
                        }
                        if (houveRot)
                        {
                            // Eixo vertical no LocationPoint APÓS o move.
                            XYZ pivot = ((LocationPoint)doc.GetElement(id).Location).Point;
                            Line eixo = Line.CreateBound(pivot, pivot + XYZ.BasisZ);
                            ElementTransformUtils.RotateElement(doc, id, eixo, lt.RotZ);
                            rotacionados++;
                        }
                        t.Commit();
                    }
                }
                tg.Assimilate();
            }

            if (naoEncontrado > 0 || semLoc > 0 || remapeados > 0 || duplicados > 0)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Concluído.");
                sb.AppendLine($"Movidos: {movidos}");
                sb.AppendLine($"Rotacionados: {rotacionados}");
                if (remapeados > 0) sb.AppendLine($"Sub-componentes remapeados p/ família raiz: {remapeados}");
                if (duplicados > 0) sb.AppendLine($"Linhas duplicadas (mesma raiz): {duplicados}");
                if (naoEncontrado > 0) sb.AppendLine($"ElementId não encontrado [E306]: {naoEncontrado}");
                if (semLoc > 0) sb.AppendLine($"Sem LocationPoint: {semLoc}");
                Autodesk.Revit.UI.TaskDialog.Show("Aergia", sb.ToString());
            }
            return Result.Succeeded;
        }

        // Sobe a cadeia FamilyInstance.SuperComponent até a raiz. Sub-componentes
        // aninhados (nested) frequentemente não têm LocationPoint utilizável —
        // o move/rotate só faz sentido na raiz, que é o que o usuário enxergou
        // movendo no Navis (geometricamente as partes se movem juntas).
        private static Element ResolverRaiz(Element el)
        {
            FamilyInstance fi = el as FamilyInstance;
            if (fi == null) return el;
            while (true)
            {
                FamilyInstance pai = fi.SuperComponent as FamilyInstance;
                if (pai == null) return fi;
                fi = pai;
            }
        }

        private class LinhaTransform
        {
            public long ElementIdLong;
            public double Dx;     // metros, shared coords
            public double Dy;
            public double Dz;
            public double RotZ;   // radianos, rotação em torno do eixo vertical
        }

        // Aceita: element_id;dx_m;dy_m;dz_m;rotz_rad
        // Separador ; ou , — primeiro detectado vence. Cabeçalho pulado.
        private static List<LinhaTransform> LerCsvTransform(string path)
        {
            var res = new List<LinhaTransform>();
            string[] linhas = File.ReadAllLines(path);
            foreach (string raw in linhas)
            {
                string l = (raw ?? "").Trim();
                if (l.Length == 0) continue;

                string[] cols = l.Split(';');
                if (cols.Length < 5) cols = l.Split(',');
                if (cols.Length < 5) continue;

                string idStr = cols[0].Trim();
                if (idStr.Equals("element_id", StringComparison.OrdinalIgnoreCase)) continue;

                long eid;
                if (!long.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out eid))
                    continue;

                double dx, dy, dz, rz;
                if (!ParseInv(cols[1], out dx)) continue;
                if (!ParseInv(cols[2], out dy)) continue;
                if (!ParseInv(cols[3], out dz)) continue;
                if (!ParseInv(cols[4], out rz)) rz = 0;

                res.Add(new LinhaTransform
                {
                    ElementIdLong = eid,
                    Dx = dx, Dy = dy, Dz = dz, RotZ = rz
                });
            }
            return res;
        }

        private static bool ParseInv(string s, out double d)
        {
            return double.TryParse((s ?? "").Trim().Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        // Aceita formatos:
        //   unique_id;z_m
        //   unique_id;x_m;y_m;z_m   (último campo é Z)
        // Separador ; ou , — primeiro detectado vence.
        // Cabeçalho começando com "unique_id" é pulado.
        private static List<KeyValuePair<string, double>> LerCsvRetorno(string path)
        {
            var res = new List<KeyValuePair<string, double>>();
            string[] linhas = File.ReadAllLines(path);
            foreach (string raw in linhas)
            {
                string l = (raw ?? "").Trim();
                if (l.Length == 0) continue;

                string[] cols = l.Split(';');
                if (cols.Length < 2) cols = l.Split(',');
                if (cols.Length < 2) continue;

                string id = cols[0].Trim();
                if (id.Equals("unique_id", StringComparison.OrdinalIgnoreCase)) continue;

                string ultima = cols[cols.Length - 1].Trim();
                double z;
                if (!double.TryParse(ultima.Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                {
                    z = double.NaN;
                }
                res.Add(new KeyValuePair<string, double>(id, z));
            }
            return res;
        }

        // ===================== ProjectPosition: internal ↔ shared (em pés) =====================
        // Forward (internal -> shared): rotaciona por +Angle e soma (EW,NS,Elev).
        // Inverso (shared -> internal): subtrai (EW,NS,Elev) e rotaciona por -Angle.
        // Mesma matemática do importar_pontos_navis.

        private static XYZ InternalToShared(XYZ p,
            double posEW, double posNS, double posEl, double cosA, double sinA)
        {
            double sx = p.X * cosA - p.Y * sinA + posEW;
            double sy = p.X * sinA + p.Y * cosA + posNS;
            double sz = p.Z + posEl;
            return new XYZ(sx, sy, sz);
        }

        private static XYZ SharedToInternal(XYZ p,
            double posEW, double posNS, double posEl, double cosA, double sinA)
        {
            double dx = p.X - posEW;
            double dy = p.Y - posNS;
            double ix = dx * cosA + dy * sinA;
            double iy = -dx * sinA + dy * cosA;
            double iz = p.Z - posEl;
            return new XYZ(ix, iy, iz);
        }

        // ===================== Coleta de postes =====================

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private const int VK_SHIFT = 0x10;

        private static bool EhPoste(Element e)
        {
            FamilyInstance fi = e as FamilyInstance;
            return fi != null && fi.Location is LocationPoint;
        }

        private static List<ElementId> ColetarPostes(UIDocument uidoc, Document doc)
        {
            // 1. Pré-seleção nativa do Revit tem prioridade (arraste, Ctrl, etc.).
            List<ElementId> pre = uidoc.Selection.GetElementIds()
                .Where(id => EhPoste(doc.GetElement(id)))
                .ToList();
            if (pre.Count > 0)
            {
                AplicarDestaque(doc, pre, true); // laranja, p/ consistência
                return pre;
            }

            // 2. Sem pré-seleção: loop interativo. Shift+clique = todas do tipo.
            //    Cada poste acumulado recebe destaque laranja imediato.
            HashSet<ElementId> acc = new HashSet<ElementId>();
            while (true)
            {
                try
                {
                    Reference r = uidoc.Selection.PickObject(
                        ObjectType.Element,
                        new PosteSelectionFilter(),
                        "Clique num poste — Shift+clique = todas do mesmo tipo — ESC finaliza");

                    bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                    FamilyInstance fi = doc.GetElement(r) as FamilyInstance;
                    if (fi == null) continue;

                    List<ElementId> novos = new List<ElementId>();
                    if (shift)
                    {
                        ElementId tipo = fi.GetTypeId();
                        foreach (FamilyInstance x in new FilteredElementCollector(doc)
                                     .OfClass(typeof(FamilyInstance))
                                     .Cast<FamilyInstance>())
                        {
                            if (x.GetTypeId() == tipo && x.Location is LocationPoint)
                                if (acc.Add(x.Id)) novos.Add(x.Id);
                        }
                    }
                    else
                    {
                        if (acc.Add(fi.Id)) novos.Add(fi.Id);
                    }

                    if (novos.Count > 0) AplicarDestaque(doc, novos, true);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    break;
                }
            }
            return acc.ToList();
        }

        // ===================== Destaque temporário (laranja) =====================
        // Override de gráfico por-view na ActiveView. ligar=true aplica laranja;
        // ligar=false reseta (OverrideGraphicSettings vazio). Protegido — se a view
        // não aceitar overrides, segue sem destaque. Numa transação curta própria.

        private static readonly Color CorDestaque = new Color(255, 128, 0);

        private static void AplicarDestaque(Document doc, ICollection<ElementId> ids, bool ligar)
        {
            if (ids == null || ids.Count == 0) return;
            View view = doc.ActiveView;
            if (view == null) return;

            // Famílias aninhadas COMPARTILHADAS viram elementos próprios; o override
            // no pai não as colore. Expande cada poste p/ incluir sub-componentes
            // (recursivo) — assim a peça inteira fica laranja.
            HashSet<ElementId> alvos = ExpandirComSubcomponentes(doc, ids);

            try
            {
                OverrideGraphicSettings ogs;
                if (ligar)
                {
                    ogs = new OverrideGraphicSettings();
                    ogs.SetProjectionLineColor(CorDestaque);
                    ogs.SetSurfaceForegroundPatternColor(CorDestaque);
                    ElementId solido = SolidFillPatternId(doc);
                    if (solido != ElementId.InvalidElementId)
                        ogs.SetSurfaceForegroundPatternId(solido);
                }
                else
                {
                    ogs = new OverrideGraphicSettings(); // vazio = remove override
                }

                using (Transaction t = new Transaction(doc, ligar ? "Aegia: Destacar" : "Aegia: Limpar destaque"))
                {
                    t.Start();
                    foreach (ElementId id in alvos)
                    {
                        try { view.SetElementOverrides(id, ogs); }
                        catch { /* elemento não suporta override nesta view — ignora */ }
                    }
                    t.Commit();
                }
            }
            catch { /* view não suporta overrides — segue sem destaque */ }
        }

        // Retorna o conjunto = ids + todos os sub-componentes aninhados (recursivo).
        // Sub-componentes só existem em FamilyInstance via GetSubComponentIds().
        private static HashSet<ElementId> ExpandirComSubcomponentes(Document doc, ICollection<ElementId> ids)
        {
            HashSet<ElementId> res = new HashSet<ElementId>();
            Stack<ElementId> pilha = new Stack<ElementId>(ids);
            while (pilha.Count > 0)
            {
                ElementId id = pilha.Pop();
                if (!res.Add(id)) continue; // já visitado

                FamilyInstance fi = doc.GetElement(id) as FamilyInstance;
                if (fi == null) continue;
                try
                {
                    foreach (ElementId sub in fi.GetSubComponentIds())
                        if (!res.Contains(sub)) pilha.Push(sub);
                }
                catch { /* sem sub-componentes — ignora */ }
            }
            return res;
        }

        private static ElementId SolidFillPatternId(Document doc)
        {
            FillPatternElement fpe = new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
            return fpe != null ? fpe.Id : ElementId.InvalidElementId;
        }

        // Lança o raio de bem alto pra baixo e devolve o Z atingido mais alto.
        // riCat: hits aceitos sempre (já filtrados por categoria de topo).
        // riAll: hits aceitos só se vierem de um link configurado (linkSet) —
        //        cobre QUALQUER geometria do link-terreno (Generic Model etc.).
        private static double? ProjetarZ(ReferenceIntersector riCat,
            ReferenceIntersector riAll, HashSet<ElementId> linkSet, XYZ origem)
        {
            XYZ start = new XYZ(origem.X, origem.Y, origem.Z + 32808.0); // ~10 km em pés
            XYZ dir = new XYZ(0, 0, -1);

            double melhorZ = double.NegativeInfinity;
            bool achou = false;

            IList<ReferenceWithContext> hitsCat = riCat.Find(start, dir);
            if (hitsCat != null)
                foreach (ReferenceWithContext rwc in hitsCat)
                {
                    XYZ gp = rwc.GetReference()?.GlobalPoint;
                    if (gp == null) continue;
                    if (!achou || gp.Z > melhorZ) { melhorZ = gp.Z; achou = true; }
                }

            if (riAll != null && linkSet != null)
            {
                IList<ReferenceWithContext> hitsAll = riAll.Find(start, dir);
                if (hitsAll != null)
                    foreach (ReferenceWithContext rwc in hitsAll)
                    {
                        Reference rf = rwc.GetReference();
                        if (rf == null || !linkSet.Contains(rf.ElementId)) continue;
                        XYZ gp = rf.GlobalPoint;
                        if (gp == null) continue;
                        if (!achou || gp.Z > melhorZ) { melhorZ = gp.Z; achou = true; }
                    }
            }

            return achou ? melhorZ : (double?)null;
        }

        // ===================== Detecção de topografia =====================

        private static ElementFilter FiltroTopografia()
        {
            List<ElementFilter> ors = new List<ElementFilter>
            {
                new ElementCategoryFilter(BuiltInCategory.OST_Topography)
            };
            // OST_Toposolid pode não existir em alguma versão — protege.
            try
            {
                ors.Add(new ElementCategoryFilter(BuiltInCategory.OST_Toposolid));
            }
            catch { }
            return ors.Count == 1 ? ors[0] : new LogicalOrFilter(ors);
        }

        private static bool ExisteAlgumaTopografia(Document doc)
        {
            if (ContaTopografia(doc) > 0) return true;

            // Procura também dentro de cada RevitLinkInstance carregado.
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkInstance))
                         .Cast<RevitLinkInstance>())
            {
                Document ldoc = link.GetLinkDocument();
                if (ldoc == null) continue; // link descarregado
                if (ContaTopografia(ldoc) > 0) return true;
            }
            return false;
        }

        private static int ContaTopografia(Document d)
        {
            int n = new FilteredElementCollector(d)
                .OfCategory(BuiltInCategory.OST_Topography)
                .WhereElementIsNotElementType()
                .GetElementCount();
            try
            {
                n += new FilteredElementCollector(d)
                    .OfCategory(BuiltInCategory.OST_Toposolid)
                    .WhereElementIsNotElementType()
                    .GetElementCount();
            }
            catch { }
            return n;
        }

        // ===================== Link-terreno configurado =====================

        // Resolve quais RevitLinkInstance do doc batem com a config salva
        // (por Title do doc vinculado OU nome do RevitLinkType — qualquer um).
        private static List<ElementId> ResolverLinkInstances(Document doc)
        {
            List<ElementId> res = new List<ElementId>();
            TopoLinkCfg cfg = MemoriaTopoLink.Carregar();
            if (cfg.Vazio) return res;

            foreach (RevitLinkInstance li in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkInstance))
                         .Cast<RevitLinkInstance>())
            {
                Document ld = li.GetLinkDocument();
                string title = ld != null ? ld.Title : null;
                Element te = doc.GetElement(li.GetTypeId());
                string tipo = te != null ? te.Name : null;

                bool casaTitle = !string.IsNullOrWhiteSpace(cfg.Title) && cfg.Title == title;
                bool casaTipo = !string.IsNullOrWhiteSpace(cfg.Type) && cfg.Type == tipo;
                if (casaTitle || casaTipo) res.Add(li.Id);
            }
            return res;
        }

        // ===================== View3D para o raycast =====================

        private static View3D ObterOuCriarView3D(Document doc)
        {
            View3D existente = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => v != null && !v.IsTemplate);
            if (existente != null) return existente;

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
                    try { novo.Name = "Aegia_Temp_Projecao"; } catch { }
                }
                t.Commit();
            }
            return novo;
        }
    }

    // ===================== Selection filter =====================

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

    // ===================== Modos do round-trip NWC =====================

    internal enum ModoCM { Cancelar, Exportar, AplicarZ, AplicarTransform }

    // ===================== Config do link-terreno (global) =====================

    internal class TopoLinkCfg
    {
        public string Title = "";
        public string Type = "";
        public bool Vazio =>
            string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Type);
    }

    internal static class MemoriaTopoLink
    {
        private static string Caminho()
        {
            string b = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(b, "Aegia_TopoLinkConfig.txt");
        }

        public static TopoLinkCfg Carregar()
        {
            TopoLinkCfg c = new TopoLinkCfg();
            try
            {
                string p = Caminho();
                if (!File.Exists(p)) return c;
                foreach (string raw in File.ReadAllLines(p))
                {
                    string l = (raw ?? "").Trim();
                    int eq = l.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = l.Substring(0, eq).Trim();
                    string v = l.Substring(eq + 1).Trim();
                    if (k == "title") c.Title = v;
                    else if (k == "type") c.Type = v;
                }
            }
            catch { }
            return c;
        }

        public static void Salvar(string title, string type)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("title=" + (title ?? ""));
                sb.AppendLine("type=" + (type ?? ""));
                File.WriteAllText(Caminho(), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public static void Limpar()
        {
            try { string p = Caminho(); if (File.Exists(p)) File.Delete(p); }
            catch { }
        }
    }

    // ===================== Janela com abas (Shift+clique) =====================
    // Aba 1: configurar link-terreno (age inline via MemoriaTopoLink).
    // Aba 2: escolher modo NWC (Exportar/Aplicar Z/Aplicar Transform) → ModoNwc.

    internal class JanelaPostesShift : WWindow
    {
        public ModoCM ModoNwc { get; private set; } = ModoCM.Cancelar;

        private class Item
        {
            public string Display;
            public string Title;
            public string Type;
            public override string ToString() => Display;
        }

        private WListBox _lst;

        public JanelaPostesShift(Document doc)
        {
            Title = "Postes — Configuração / NWC";
            Width = 560; Height = 480;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            ResizeMode = System.Windows.ResizeMode.CanResize;
            Topmost = true;

            WTabControl tabs = new WTabControl { Margin = new WThickness(6) };
            Content = tabs;

            tabs.Items.Add(new WTabItem
            {
                Header = "Link-terreno",
                Content = ConstruirAbaLinkTerreno(doc)
            });
            tabs.Items.Add(new WTabItem
            {
                Header = "Postes para NWC",
                Content = ConstruirAbaNwc()
            });
        }

        // ---------- Aba 1: config do link-terreno ----------
        private System.Windows.UIElement ConstruirAbaLinkTerreno(Document doc)
        {
            WDockPanel root = new WDockPanel { LastChildFill = true };

            TopoLinkCfg cfg = MemoriaTopoLink.Carregar();
            WTextBlock info = new WTextBlock
            {
                Text =
                    "Escolha o link cujo conteúdo INTEIRO será tratado como terreno " +
                    "(inclui Generic Model). A escolha é global (salva em %APPDATA%) e " +
                    "vale para qualquer modelo que carregue este link.\n" +
                    "Config atual: " + (cfg.Vazio
                        ? "(nenhuma)"
                        : $"title='{cfg.Title}'  type='{cfg.Type}'"),
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new WThickness(10)
            };
            WDockPanel.SetDock(info, System.Windows.Controls.Dock.Top);
            root.Children.Add(info);

            WStackPanel botoes = new WStackPanel
            {
                Orientation = WOrientation.Horizontal,
                Margin = new WThickness(10),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            WButton btnOk = new WButton
            {
                Content = "Salvar",
                Width = 90, Height = 28,
                Margin = new WThickness(0, 0, 6, 0)
            };
            WButton btnLimpar = new WButton
            {
                Content = "Limpar config",
                Width = 120, Height = 28,
                Margin = new WThickness(0, 0, 6, 0)
            };
            WDockPanel.SetDock(botoes, System.Windows.Controls.Dock.Bottom);
            root.Children.Add(botoes);
            botoes.Children.Add(btnOk);
            botoes.Children.Add(btnLimpar);

            _lst = new WListBox { Margin = new WThickness(10, 0, 10, 0) };
            root.Children.Add(_lst);

            foreach (RevitLinkInstance li in new FilteredElementCollector(doc)
                         .OfClass(typeof(RevitLinkInstance))
                         .Cast<RevitLinkInstance>())
            {
                Document ld = li.GetLinkDocument();
                Element te = doc.GetElement(li.GetTypeId());
                string title = ld != null ? ld.Title : "";
                string tipo = te != null ? te.Name : "";
                Item it = new Item
                {
                    Title = title,
                    Type = tipo,
                    Display = $"{(string.IsNullOrEmpty(tipo) ? "(sem tipo)" : tipo)}  —  " +
                              $"{(string.IsNullOrEmpty(title) ? "(descarregado)" : title)}"
                };
                _lst.Items.Add(it);
            }
            if (_lst.Items.Count == 0)
                info.Text += "\n\nNenhum RevitLinkInstance neste modelo. Abra um modelo " +
                             "com o link da topografia carregado para configurar.";

            btnOk.Click += (s, e) =>
            {
                Item it = _lst.SelectedItem as Item;
                if (it == null)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia", "Selecione um link na lista.");
                    return;
                }
                MemoriaTopoLink.Salvar(it.Title, it.Type);
                Close();
            };
            btnLimpar.Click += (s, e) => { MemoriaTopoLink.Limpar(); Close(); };

            return root;
        }

        // ---------- Aba 2: modos NWC ----------
        private System.Windows.UIElement ConstruirAbaNwc()
        {
            WDockPanel root = new WDockPanel { LastChildFill = false };

            WTextBlock info = new WTextBlock
            {
                Text =
                    "Fluxo Revit ↔ Navisworks via CSV (a API do Revit não lê NWC/NWD):\n\n" +
                    "  Pipeline raycast (Z do terreno):\n" +
                    "    1) Exportar — salva CSV de XY dos postes.\n" +
                    "    2) Na Navisworks: plugin 'Postes CM' faz raycast e\n" +
                    "       devolve CSV com Z.\n" +
                    "    3) Aplicar Z — postes descem ao terreno.\n\n" +
                    "  Pipeline transform (mover/girar por conflito):\n" +
                    "    Na Navisworks, use Item Tools → Transform pra\n" +
                    "    mover/girar postes; o plugin 'Postes CM Transforms'\n" +
                    "    gera CSV com os deltas.\n" +
                    "    4) Aplicar Transform — Revit re-aplica nos elementos.\n\n" +
                    "Pré-requisito: RVT e NWD na MESMA coord compartilhada.\n" +
                    "Não modifique o modelo Revit entre exportar e aplicar.",
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new WThickness(14, 12, 14, 8)
            };
            WDockPanel.SetDock(info, System.Windows.Controls.Dock.Top);
            root.Children.Add(info);

            WStackPanel radios = new WStackPanel { Margin = new WThickness(24, 0, 14, 6) };
            WRadioButton rbExp = new WRadioButton
            {
                Content = "Exportar postes (Revit → CSV)",
                IsChecked = true,
                Margin = new WThickness(0, 4, 0, 4)
            };
            WRadioButton rbApl = new WRadioButton
            {
                Content = "Aplicar Z (CSV → Revit)",
                Margin = new WThickness(0, 4, 0, 4)
            };
            WRadioButton rbTrn = new WRadioButton
            {
                Content = "Aplicar Transform (CSV → Revit)",
                Margin = new WThickness(0, 4, 0, 4)
            };
            radios.Children.Add(rbExp);
            radios.Children.Add(rbApl);
            radios.Children.Add(rbTrn);
            WDockPanel.SetDock(radios, System.Windows.Controls.Dock.Top);
            root.Children.Add(radios);

            WStackPanel botoes = new WStackPanel
            {
                Orientation = WOrientation.Horizontal,
                Margin = new WThickness(14, 8, 14, 14),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            WButton btnExec = new WButton
            {
                Content = "Executar",
                Width = 100, Height = 28
            };
            WDockPanel.SetDock(botoes, System.Windows.Controls.Dock.Bottom);
            root.Children.Add(botoes);
            botoes.Children.Add(btnExec);

            btnExec.Click += (s, e) =>
            {
                if (rbExp.IsChecked == true) ModoNwc = ModoCM.Exportar;
                else if (rbApl.IsChecked == true) ModoNwc = ModoCM.AplicarZ;
                else if (rbTrn.IsChecked == true) ModoNwc = ModoCM.AplicarTransform;
                else ModoNwc = ModoCM.Cancelar;
                Close();
            };

            return root;
        }
    }
}
