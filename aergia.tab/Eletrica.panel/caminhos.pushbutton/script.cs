using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;

using WWindow = System.Windows.Window;
using WButton = System.Windows.Controls.Button;
using WTextBox = System.Windows.Controls.TextBox;
using WLabel = System.Windows.Controls.Label;
using WTabControl = System.Windows.Controls.TabControl;
using WTabItem = System.Windows.Controls.TabItem;
using WTreeView = System.Windows.Controls.TreeView;
using WTreeViewItem = System.Windows.Controls.TreeViewItem;
using WCanvas = System.Windows.Controls.Canvas;
using WFontWeights = System.Windows.FontWeights;
using WFontFamily = System.Windows.Media.FontFamily;

namespace Aegia_VisualizadorRotas
{
    [Transaction(TransactionMode.Manual)]
    public class VisualizadorCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument.Document;

            var quadrosCollector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .ToList();

            var arvoreDados = new Dictionary<FamilyInstance, List<ElectricalSystem>>();
            foreach (var q in quadrosCollector)
            {
                var circs = Utils.ObterCircuitosDoQuadro(q);
                if (circs.Count > 0) arvoreDados[q] = circs;
            }

            if (arvoreDados.Count == 0)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aegia", "Nenhum quadro com circuitos foi encontrado.");
                return Result.Cancelled;
            }

            HighlightRouteHandler handler = new HighlightRouteHandler();
            ExternalEvent exEvent = ExternalEvent.Create(handler);

            bool isPlanta = doc.ActiveView is ViewPlan;

            VisualizadorForm form = new VisualizadorForm(doc, arvoreDados, handler, exEvent, isPlanta);
            form.Show(); 

            return Result.Succeeded;
        }
    }

    // ==========================================
    // O CARTEIRO - MOTOR GRÁFICO (2D e 3D)
    // ==========================================
    public class HighlightRouteHandler : IExternalEventHandler
    {
        public enum ModoVisualizacao { Selecionar, Transparencia, Isolar, Resetar }
        
        public string QuadroIdStr { get; set; }
        public string CircuitoIdStr { get; set; }
        public ModoVisualizacao ModoAtual { get; set; } = ModoVisualizacao.Isolar;

        private List<ElementId> elementosComOverride = new List<ElementId>();
        private Dictionary<string, List<ElementId>> mapaRotas = null; 
        private bool modoFantasmaAtivo = false;

        public void Execute(UIApplication app)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc.Document;
            View view = doc.ActiveView;

            if (!(view is View3D || view is ViewPlan || view is ViewSection))
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aviso", "Abra uma Vista 3D, Planta ou Corte para visualizar as rotas.");
                return;
            }

            using (Transaction t = new Transaction(doc, "Visualizador Aegia"))
            {
                t.Start();

                if (ModoAtual == ModoVisualizacao.Resetar)
                {
                    ResetarGraficos(doc, view);
                    mapaRotas = null; 
                }
                else if (!string.IsNullOrEmpty(QuadroIdStr) && !string.IsNullOrEmpty(CircuitoIdStr))
                {
                    if (mapaRotas == null) MapearInfraestrutura(doc, view);

                    ElementId qId = new ElementId(Convert.ToInt64(QuadroIdStr));
                    ElementId cId = new ElementId(Convert.ToInt64(CircuitoIdStr));
                    ElectricalSystem circ = doc.GetElement(cId) as ElectricalSystem;

                    string chaveBusca = $"{QuadroIdStr}_{CircuitoIdStr}"; 
                    mapaRotas.TryGetValue(chaveBusca, out List<ElementId> tubosDaRota);
                    if (tubosDaRota == null) tubosDaRota = new List<ElementId>();

                    List<ElementId> elementosCircuito = new List<ElementId>();
                    List<ElementId> hostsAninhados = new List<ElementId>();

                    if (circ.Elements != null)
                    {
                        foreach (Element el in circ.Elements)
                        {
                            elementosCircuito.Add(el.Id);
                            if (el is FamilyInstance fi && fi.SuperComponent != null)
                                hostsAninhados.Add(fi.SuperComponent.Id);
                        }
                    }

                    if (ModoAtual == ModoVisualizacao.Selecionar)
                    {
                        ResetarGraficos(doc, view);
                        var selecao = tubosDaRota.Concat(elementosCircuito).Concat(hostsAninhados).Concat(new[] { qId }).Distinct().ToList();
                        uidoc.Selection.SetElementIds(selecao);
                    }
                    else if (ModoAtual == ModoVisualizacao.Transparencia || ModoAtual == ModoVisualizacao.Isolar)
                    {
                        PrepararVista(doc, view);
                        LimparOverridesAtuais(view);

                        AplicarDestaque(doc, view, tubosDaRota, new Color(50, 255, 50), 8); // Infra = Verde
                        AplicarDestaque(doc, view, new List<ElementId> { qId }, new Color(255, 0, 0), 10); // Origem = Vermelho
                        AplicarDestaque(doc, view, elementosCircuito, new Color(0, 100, 255), 6); // Cargas = Azul

                        if (ModoAtual == ModoVisualizacao.Isolar)
                        {
                            HashSet<ElementId> elementosParaIsolar = new HashSet<ElementId>(elementosComOverride);
                            foreach (var hostId in hostsAninhados) elementosParaIsolar.Add(hostId);

                            if (view is ViewPlan || view is ViewSection)
                            {
                                var elementosNaVista = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElements();
                                foreach (Element el in elementosNaVista)
                                {
                                    if (el.Category != null && (el.Category.CategoryType == CategoryType.Annotation || el.Category.Id.Value == (long)BuiltInCategory.OST_RvtLinks))
                                        elementosParaIsolar.Add(el.Id);
                                }
                            }

                            if (elementosParaIsolar.Count > 0)
                                view.IsolateElementsTemporary(elementosParaIsolar.ToList());
                        }

                        uidoc.Selection.SetElementIds(new List<ElementId>()); 
                    }
                }
                t.Commit();
            }
        }

        private void PrepararVista(Document doc, View view)
        {
            if (ModoAtual == ModoVisualizacao.Transparencia)
            {
                if (view.IsTemporaryHideIsolateActive()) view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                if (!modoFantasmaAtivo) { AtivarModoFantasma(doc, view); modoFantasmaAtivo = true; }
            }
            else // Modo Isolar
            {
                if (modoFantasmaAtivo) { DesativarModoFantasma(doc, view); modoFantasmaAtivo = false; }
                if (view.IsTemporaryHideIsolateActive()) view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
            }
        }

        private void AplicarDestaque(Document doc, View view, List<ElementId> ids, Color cor, int peso)
        {
            if (ids == null || ids.Count == 0) return;

            OverrideGraphicSettings ogs = new OverrideGraphicSettings();
            ogs.SetSurfaceTransparency(1); 
            ogs.SetProjectionLineColor(cor);
            ogs.SetProjectionLineWeight(peso);

            FillPatternElement solidFill = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>().FirstOrDefault(a => a.GetFillPattern().IsSolidFill);
            if (solidFill != null)
            {
                ogs.SetSurfaceForegroundPatternId(solidFill.Id);
                ogs.SetSurfaceForegroundPatternColor(cor);
            }

            foreach (var id in ids)
            {
                if (id == ElementId.InvalidElementId) continue;
                view.SetElementOverrides(id, ogs);
                elementosComOverride.Add(id);
            }
        }

        private void MapearInfraestrutura(Document doc, View view)
        {
            mapaRotas = new Dictionary<string, List<ElementId>>();
            var infra = new FilteredElementCollector(doc, view.Id).WherePasses(new ElementMulticategoryFilter(new List<ElementId> { 
                new ElementId((long)BuiltInCategory.OST_Conduit), new ElementId((long)BuiltInCategory.OST_ConduitFitting),
                new ElementId((long)BuiltInCategory.OST_CableTray), new ElementId((long)BuiltInCategory.OST_CableTrayFitting)
            })).ToElements();

            foreach (Element tubo in infra)
            {
                string zids = tubo.LookupParameter("ZIDS")?.AsString();
                if (string.IsNullOrEmpty(zids)) continue;

                foreach (var bloco in zids.Split('|'))
                {
                    int s = bloco.IndexOf('['), e = bloco.IndexOf(']');
                    if (s >= 0 && e > s)
                    {
                        string qId = bloco.Substring(s + 1, e - s - 1).Trim();
                        foreach (var cNum in bloco.Substring(e + 1).Trim().Split(';'))
                        {
                            string chave = $"{qId}_{cNum.Trim()}";
                            if (!mapaRotas.ContainsKey(chave)) mapaRotas[chave] = new List<ElementId>();
                            mapaRotas[chave].Add(tubo.Id);
                        }
                    }
                }
            }
        }

        private void LimparOverridesAtuais(View view)
        {
            OverrideGraphicSettings reset = new OverrideGraphicSettings();
            foreach (var id in elementosComOverride) view.SetElementOverrides(id, reset);
            elementosComOverride.Clear();
        }

        private void AtivarModoFantasma(Document doc, View view)
        {
            OverrideGraphicSettings ogs = new OverrideGraphicSettings();
            ogs.SetSurfaceTransparency(90);
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat.CategoryType == CategoryType.Model && cat.get_AllowsVisibilityControl(view))
                    view.SetCategoryOverrides(cat.Id, ogs);
            }
        }

        private void DesativarModoFantasma(Document doc, View view)
        {
            OverrideGraphicSettings reset = new OverrideGraphicSettings();
            foreach (Category cat in doc.Settings.Categories)
            {
                if (cat.CategoryType == CategoryType.Model && cat.get_AllowsVisibilityControl(view))
                    view.SetCategoryOverrides(cat.Id, reset);
            }
        }

        private void ResetarGraficos(Document doc, View view)
        {
            if (modoFantasmaAtivo) { DesativarModoFantasma(doc, view); modoFantasmaAtivo = false; }
            LimparOverridesAtuais(view);
            if (view.IsTemporaryHideIsolateActive()) view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
        }

        public string GetName() => "Motor Gráfico Isolador";
    }

    // ==========================================
    // INTERFACE DE USUÁRIO COM BUSCA E TREEVIEW
    // ==========================================
    public class VisualizadorForm : WWindow
    {
        private WTextBox txtBusca;
        private WTabControl tabControl;
        private HighlightRouteHandler handler;
        private ExternalEvent exEvent;
        private HighlightRouteHandler.ModoVisualizacao modoAtivo;
        
        private Dictionary<FamilyInstance, List<ElectricalSystem>> dadosProjeto;
        private Dictionary<string, WTreeView> arvoreAbas = new Dictionary<string, WTreeView>();
        private string[] nomesAbas = { "Tomadas", "Força", "Iluminação", "Dados/CFTV", "Outros" };

        public VisualizadorForm(Document doc, Dictionary<FamilyInstance, List<ElectricalSystem>> arvoreDados, HighlightRouteHandler h, ExternalEvent ev, bool isPlanta)
        {
            handler = h; exEvent = ev;
            dadosProjeto = arvoreDados;
            modoAtivo = isPlanta ? HighlightRouteHandler.ModoVisualizacao.Transparencia : HighlightRouteHandler.ModoVisualizacao.Isolar;

            this.Title = "Auditoria de Rotas Aegia"; 
            this.Width = 460; this.Height = 620; 
            this.Topmost = true; 
            this.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            this.ResizeMode = System.Windows.ResizeMode.NoResize;

            WCanvas canvas = new WCanvas();
            this.Content = canvas;

            WLabel lblInfo = new WLabel() { Content = "Buscar Quadro ou Circuito:", Width = 350, FontWeight = WFontWeights.Bold };
            WCanvas.SetLeft(lblInfo, 10);
            WCanvas.SetTop(lblInfo, 10);
            
            txtBusca = new WTextBox() { Width = 420, Height = 22 };
            WCanvas.SetLeft(txtBusca, 10);
            WCanvas.SetTop(txtBusca, 40);
            txtBusca.TextChanged += (s, e) => RecarregarArvores(txtBusca.Text.Trim());

            tabControl = new WTabControl() { Width = 420, Height = 340 };
            WCanvas.SetLeft(tabControl, 10);
            WCanvas.SetTop(tabControl, 70);
            
            foreach (var aba in nomesAbas)
            {
                WTabItem page = new WTabItem() { Header = aba };
                WTreeView tv = new WTreeView() { FontFamily = new WFontFamily("Consolas"), FontSize = 12 };
                tv.SelectedItemChanged += (s, e) => { ExecutarAtual(tv); };
                arvoreAbas[aba] = tv;
                page.Content = tv;
                tabControl.Items.Add(page);
            }

            RecarregarArvores(""); 

            int yBtns = 420;
            WButton btnSelecionar = new WButton() { Content = "Apenas Selecionar (Revit Nativo)", Width = 420, Height = 28 };
            WCanvas.SetLeft(btnSelecionar, 10);
            WCanvas.SetTop(btnSelecionar, yBtns);
            yBtns += 35;
            WButton btnTransparencia = new WButton() { Content = "Transparência (Modo Fantasma)", Width = 420, Height = 28 };
            WCanvas.SetLeft(btnTransparencia, 10);
            WCanvas.SetTop(btnTransparencia, yBtns);
            yBtns += 35;
            WButton btnIsolar = new WButton() { Content = "Isolar Circuito (Modo Óculos)", Width = 420, Height = 28 };
            WCanvas.SetLeft(btnIsolar, 10);
            WCanvas.SetTop(btnIsolar, yBtns);
            yBtns += 35;
            WButton btnReset = new WButton() { Content = "RESETAR VISTA GRÁFICA", Width = 420, Height = 35, FontWeight = WFontWeights.Bold };
            WCanvas.SetLeft(btnReset, 10);
            WCanvas.SetTop(btnReset, yBtns);

            if (isPlanta) btnTransparencia.FontWeight = WFontWeights.Bold;
            else btnIsolar.FontWeight = WFontWeights.Bold;

            btnSelecionar.Click += (s, e) => { modoAtivo = HighlightRouteHandler.ModoVisualizacao.Selecionar; AtualizarNegrito(btnSelecionar, btnTransparencia, btnIsolar); ExecutarDaAbaAtiva(); };
            btnTransparencia.Click += (s, e) => { modoAtivo = HighlightRouteHandler.ModoVisualizacao.Transparencia; AtualizarNegrito(btnTransparencia, btnSelecionar, btnIsolar); ExecutarDaAbaAtiva(); };
            btnIsolar.Click += (s, e) => { modoAtivo = HighlightRouteHandler.ModoVisualizacao.Isolar; AtualizarNegrito(btnIsolar, btnSelecionar, btnTransparencia); ExecutarDaAbaAtiva(); };
            btnReset.Click += (s, e) => { handler.ModoAtual = HighlightRouteHandler.ModoVisualizacao.Resetar; exEvent.Raise(); };

            // Usamos Closed (EventHandler/EventArgs) em vez de Closing (CancelEventHandler/CancelEventArgs)
            // porque CancelEventArgs vive em System.ComponentModel.dll, que o pyRevit não referencia em .NET 8/10.
            // Como o handler só dispara o reset e não cancela o fechamento, Closed serve.
            this.Closed += (s, e) => {
                handler.ModoAtual = HighlightRouteHandler.ModoVisualizacao.Resetar;
                exEvent.Raise();
            };

            canvas.Children.Add(lblInfo);
            canvas.Children.Add(txtBusca);
            canvas.Children.Add(tabControl);
            canvas.Children.Add(btnSelecionar);
            canvas.Children.Add(btnTransparencia);
            canvas.Children.Add(btnIsolar);
            canvas.Children.Add(btnReset);
        }

        private void RecarregarArvores(string filtroBusca)
        {
            filtroBusca = filtroBusca.ToLower();

            foreach (var tv in arvoreAbas.Values) tv.Items.Clear();

            foreach (var kvp in dadosProjeto)
            {
                var quadro = kvp.Key;
                string qNome = quadro.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString() ?? quadro.Name;
                bool matchQuadro = qNome.ToLower().Contains(filtroBusca);

                var circsAgrupados = kvp.Value.GroupBy(c => Utils.GetCategoriaAba(c));

                foreach (var grupo in circsAgrupados)
                {
                    WTreeView tvTarget = arvoreAbas.ContainsKey(grupo.Key) ? arvoreAbas[grupo.Key] : arvoreAbas["Outros"];
                    var circsOrdenados = grupo.OrderBy(c => Utils.ExtrairNumero(c.CircuitNumber)).ToList();
                    List<WTreeViewItem> nosFilhos = new List<WTreeViewItem>();

                    foreach (var circ in circsOrdenados)
                    {
                        string cNum = circ.CircuitNumber;
                        string cNome = circ.LoadName;
                        if (string.IsNullOrEmpty(cNome)) cNome = circ.Name;

                        bool matchCircuito = cNum.ToLower().Contains(filtroBusca) || cNome.ToLower().Contains(filtroBusca);

                        if (!string.IsNullOrEmpty(filtroBusca) && !matchQuadro && !matchCircuito) continue;

                        string cNumFormatado = cNum.Length <= 3 ? cNum.PadLeft(3) : cNum;
                        string tituloNo = $"[ {cNumFormatado} ] - {cNome}";

                        nosFilhos.Add(new WTreeViewItem() { Header = tituloNo, Tag = $"{quadro.Id}|{circ.Id}" });
                    }

                    if (nosFilhos.Count > 0)
                    {
                        WTreeViewItem noQ = new WTreeViewItem() { Header = $"{qNome} (ID: {quadro.Id})", Tag = "QUADRO" };
                        foreach (var child in nosFilhos) noQ.Items.Add(child);
                        tvTarget.Items.Add(noQ);

                        if (!string.IsNullOrEmpty(filtroBusca)) noQ.IsExpanded = true;
                    }
                }
            }

            // Snapshot manual em vez de tabControl.Items.Cast<WTabItem>().ToList().
            // ItemCollection implementa INotifyCollectionChanged/INotifyPropertyChanged (System.ObjectModel.dll),
            // que o pyRevit não referencia em .NET 8/10. Mesmo o cast direto (IEnumerable)tabControl.Items
            // dispara CS0012 porque o compilador enumera as interfaces da ItemCollection para validar a conversão.
            // Passamos por 'object' primeiro: upcast trivial sem inspeção de interfaces; depois o cast para
            // IEnumerable é verificação runtime entre 'object' e a interface, sem tocar nas demais.
            List<WTabItem> tabsSnapshot = new List<WTabItem>();
            object itemsBox = tabControl.Items;
            foreach (object item in (System.Collections.IEnumerable)itemsBox)
            {
                if (item is WTabItem wti) tabsSnapshot.Add(wti);
            }
            foreach (WTabItem tb in tabsSnapshot)
            {
                WTreeView t = tb.Content as WTreeView;
                if (t.Items.Count == 0 && tabControl.Items.Contains(tb)) tabControl.Items.Remove(tb);
                else if (t.Items.Count > 0 && !tabControl.Items.Contains(tb)) tabControl.Items.Add(tb);
            }
        }

        private void AtualizarNegrito(WButton ativo, WButton inativo1, WButton inativo2)
        {
            ativo.FontWeight = WFontWeights.Bold;
            inativo1.FontWeight = WFontWeights.Normal;
            inativo2.FontWeight = WFontWeights.Normal;
        }

        private void ExecutarDaAbaAtiva()
        {
            if (tabControl.SelectedItem != null)
            {
                WTabItem tab = tabControl.SelectedItem as WTabItem;
                WTreeView tv = tab?.Content as WTreeView;
                ExecutarAtual(tv);
            }
        }

        private void ExecutarAtual(WTreeView tv)
        {
            if (tv == null) return;
            WTreeViewItem selectedNode = tv.SelectedItem as WTreeViewItem;
            if (selectedNode == null || selectedNode.Tag?.ToString() == "QUADRO") return;
            var ids = selectedNode.Tag.ToString().Split('|');
            handler.QuadroIdStr = ids[0]; handler.CircuitoIdStr = ids[1];
            handler.ModoAtual = modoAtivo; exEvent.Raise();
        }
    }

    public static class Utils
    {
        public static List<ElectricalSystem> ObterCircuitosDoQuadro(FamilyInstance q)
        {
            return q.MEPModel?.GetAssignedElectricalSystems()?.Cast<ElectricalSystem>().ToList() ?? new List<ElectricalSystem>();
        }

        public static string GetCategoriaAba(ElectricalSystem circ)
        {
            string tipo = LerParametro(circ, "Tipo Circuito").Trim().ToUpper();
            if (tipo.Contains("ILU")) return "Iluminação";
            if (tipo.Contains("TOM")) return "Tomadas";
            if (tipo.Contains("FOR")) return "Força";
            if (tipo.Contains("DAD") || tipo.Contains("CFTV") || tipo.Contains("COMUNIC")) return "Dados/CFTV";
            return "Outros";
        }

        public static int ExtrairNumero(string texto)
        {
            if (string.IsNullOrEmpty(texto)) return 0;
            string numStr = new string(texto.Where(char.IsDigit).ToArray());
            if (int.TryParse(numStr, out int result)) return result;
            return 0;
        }

        private static string LerParametro(Element el, string nome)
        {
            if (el == null || !el.IsValidObject) return "";
            Parameter p = el.LookupParameter(nome);
            if (p != null && p.HasValue) return p.AsString() ?? p.AsValueString() ?? "";
            return "";
        }
    }
}