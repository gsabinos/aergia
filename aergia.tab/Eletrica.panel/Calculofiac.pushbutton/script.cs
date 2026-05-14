using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ComponentModel;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;

// WPF Aliases
using WWindow = System.Windows.Window;
using WButton = System.Windows.Controls.Button;
using WCheckBox = System.Windows.Controls.CheckBox;
using WLabel = System.Windows.Controls.Label;
using WTextBox = System.Windows.Controls.TextBox;
using WTabControl = System.Windows.Controls.TabControl;
using WTabItem = System.Windows.Controls.TabItem;
using WTreeView = System.Windows.Controls.TreeView;
using WTreeViewItem = System.Windows.Controls.TreeViewItem;



using WStackPanel = System.Windows.Controls.StackPanel;
using WScrollViewer = System.Windows.Controls.ScrollViewer;
using WCanvas = System.Windows.Controls.Canvas;
using WThickness = System.Windows.Thickness;
using WMessageBox = System.Windows.MessageBox;
using WMessageBoxButton = System.Windows.MessageBoxButton;
using WMessageBoxImage = System.Windows.MessageBoxImage;
using WColor = System.Windows.Media.Color;
using WColors = System.Windows.Media.Colors;
using WSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Aegia_GestorRotas
{
    [Transaction(TransactionMode.Manual)]
    public class GestorCommand : IExternalCommand
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
                try
                {
                    if (!q.IsValidObject || q.Category == null) continue;
                    var circs = Utils.ObterCircuitosDoQuadro(q);
                    if (circs.Count > 0) arvoreDados[q] = circs;
                }
                catch (Exception ex) {  }
            }

            if (arvoreDados.Count == 0)
            {
                WMessageBox.Show("Nenhum quadro com circuitos configurados foi encontrado no projeto.", "Aegia", WMessageBoxButton.OK, WMessageBoxImage.Warning);
                return Result.Cancelled;
            }

            GestorEventHandler handler = new GestorEventHandler();
            ExternalEvent exEvent = ExternalEvent.Create(handler);
            
            bool isPlanta = doc.ActiveView is ViewPlan;

            GestorForm form = new GestorForm(doc, arvoreDados, handler, exEvent, isPlanta);
            
            // Delegate atribuído ANTES de exibir o formulário
            handler.OnCalculationDone = () => {
                form.Dispatcher.Invoke(new System.Action(() => {
                    form.RecarregarDadosDoRevit();
                }));
            };

            form.Show();

            return Result.Succeeded;
        }
    }

    // ==========================================
    // O CARTEIRO - EVENT HANDLER
    // ==========================================
    public class GestorEventHandler : IExternalEventHandler
    {
        public enum ModoAcao { CalcularLote, Selecionar, Transparencia, Isolar, Resetar, Adicionar, Remover, ConfigurarProjeto, ComandoExternoShift }
        
        public ModoAcao AcaoAtual { get; set; } = ModoAcao.Isolar;
        
        public List<FamilyInstance> QuadrosParaCalcular { get; set; }
        public Action OnCalculationDone { get; set; }

        public string QuadroIdStr { get; set; }
        public string CircuitoIdStr { get; set; }

        private List<ElementId> elementosComOverride = new List<ElementId>();
        private Dictionary<string, List<ElementId>> mapaRotas = null; 
        private bool modoFantasmaAtivo = false;
        private ModoAcao modoRetornoEdicao = ModoAcao.Isolar;

        public void Execute(UIApplication app)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc.Document;
            View view = doc.ActiveView;

            if (AcaoAtual == ModoAcao.CalcularLote)
            {
                ExecutarCalculo(doc);
                return;
            }

            if (AcaoAtual == ModoAcao.ComandoExternoShift)
            {
                WorksetConfigForm configForm = new WorksetConfigForm(doc);
                configForm.Show(); 
                return;
            }

            using (Transaction t = new Transaction(doc, "Aegia: Gestor de Rotas"))
            {
                t.Start();

                if (AcaoAtual == ModoAcao.ConfigurarProjeto)
                {
                    ConfigurarProjetoETabelas(doc);
                    t.Commit();
                    return;
                }

                if (!(view is View3D || view is ViewPlan || view is ViewSection))
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aviso", "Abra uma Vista 3D, Planta ou Corte para visualizar ou editar as rotas.");
                    t.RollBack();
                    return;
                }

                if (AcaoAtual == ModoAcao.Resetar)
                {
                    ResetarGraficos(doc, view);
                    mapaRotas = null; 
                }
                else if (!string.IsNullOrEmpty(QuadroIdStr) && !string.IsNullOrEmpty(CircuitoIdStr))
                {
                    MapearInfraestrutura(doc, view);

                    ElementId qId = Utils.CriarElementId(QuadroIdStr);
                    ElementId cId = Utils.CriarElementId(CircuitoIdStr);
                    ElectricalSystem circ = doc.GetElement(cId) as ElectricalSystem;
                    
                    if (circ == null || !circ.IsValidObject) {
                        t.RollBack(); return;
                    }

                    if (AcaoAtual == ModoAcao.Adicionar || AcaoAtual == ModoAcao.Remover)
                    {
                        var selecionados = uidoc.Selection.GetElementIds();
                        if (selecionados.Count == 0)
                        {
                            Autodesk.Revit.UI.TaskDialog.Show("Aviso", "Selecione as infraestruturas (Eletrodutos/Eletrocalhas).");
                            t.RollBack(); return;
                        }

                        var catsInfra = new HashSet<long> { 
                            (long)BuiltInCategory.OST_Conduit, (long)BuiltInCategory.OST_ConduitFitting,
                            (long)BuiltInCategory.OST_CableTray, (long)BuiltInCategory.OST_CableTrayFitting
                        };

                        bool alterouAlgo = false;
                        foreach (var id in selecionados)
                        {
                            Element el = doc.GetElement(id);
                            if (el == null || !el.IsValidObject || el.Category == null) continue;

                            if (!catsInfra.Contains(el.Category.Id.Value)) continue;

                            string zidsAtual = Utils.LerParametro(el, "ZIDS");
                            string novoZids = AcaoAtual == ModoAcao.Adicionar 
                                ? Utils.AddToZids(zidsAtual, QuadroIdStr, CircuitoIdStr) 
                                : Utils.RemoveFromZids(zidsAtual, QuadroIdStr, CircuitoIdStr);

                            if (zidsAtual != novoZids)
                            {
                                Utils.WriteParam(el, "ZIDS", novoZids);
                                alterouAlgo = true;
                            }
                        }

                        if (alterouAlgo) MapearInfraestrutura(doc, view);
                        AcaoAtual = modoRetornoEdicao; 
                    }

                    string chaveBusca = $"{QuadroIdStr}_{CircuitoIdStr}"; 
                    mapaRotas.TryGetValue(chaveBusca, out List<ElementId> tubosDaRota);
                    if (tubosDaRota == null) tubosDaRota = new List<ElementId>();

                    List<ElementId> elementosCircuito = new List<ElementId>();
                    List<ElementId> hostsAninhados = new List<ElementId>();

                    if (circ.Elements != null)
                    {
                        foreach (Element el in circ.Elements)
                        {
                            if (!el.IsValidObject) continue;
                            elementosCircuito.Add(el.Id);
                            if (el is FamilyInstance fi && fi.SuperComponent != null)
                                hostsAninhados.Add(fi.SuperComponent.Id);
                        }
                    }

                    if (AcaoAtual == ModoAcao.Selecionar)
                    {
                        ResetarGraficos(doc, view);
                        var selecao = tubosDaRota.Concat(elementosCircuito).Concat(hostsAninhados).Concat(new[] { qId }).Distinct().ToList();
                        uidoc.Selection.SetElementIds(selecao);
                    }
                    else if (AcaoAtual == ModoAcao.Transparencia || AcaoAtual == ModoAcao.Isolar)
                    {
                        modoRetornoEdicao = AcaoAtual; 
                        PrepararVista(doc, view);
                        LimparOverridesAtuais(view);

                        AplicarDestaque(doc, view, tubosDaRota, new Color(50, 255, 50), 8); 
                        AplicarDestaque(doc, view, new List<ElementId> { qId }, new Color(255, 0, 0), 10); 
                        AplicarDestaque(doc, view, elementosCircuito, new Color(0, 100, 255), 6); 

                        if (AcaoAtual == ModoAcao.Isolar)
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

        private void ExecutarCalculo(Document doc)
        {
            if (QuadrosParaCalcular == null || QuadrosParaCalcular.Count == 0) return;

            NetworkRouter router = new NetworkRouter(doc);

            using (Transaction t = new Transaction(doc, "Calcular Cabos e Rotas em Lote"))
            {
                t.Start();
                try
                {
                    // Limpeza em lote: Varre toda a infraestrutura e limpa os dados dos quadros que serão recalculados
                    LimparInfraestruturaDosQuadros(doc, QuadrosParaCalcular);

                    foreach (var quadro in QuadrosParaCalcular)
                    {
                        if (quadro.IsValidObject) ProcessadorQuadro.Processar(doc, quadro, router);
                    }
                    doc.Regenerate();
                    t.Commit();
                    
                    WMessageBox.Show("Cálculo e Roteamento concluídos com sucesso!", "Aegia", WMessageBoxButton.OK, WMessageBoxImage.Information);
                    OnCalculationDone?.Invoke();
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    WMessageBox.Show($"Ocorreu um erro técnico:\n\n{ex.Message}\n{ex.StackTrace}", "Erro na Transação", WMessageBoxButton.OK, WMessageBoxImage.Error);
                }
            }
        }

        private void LimparInfraestruturaDosQuadros(Document doc, List<FamilyInstance> quadros)
        {
            var cats = new List<BuiltInCategory> { 
                BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting, 
                BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting 
            };
            
            var infraElementos = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(new ElementMulticategoryFilter(cats))
                .ToElements();

            foreach (Element tubo in infraElementos)
            {
                try 
                {
                    if (!tubo.IsValidObject || tubo.Category == null) continue;

                    string oldCirc = Utils.LerParametro(tubo, "ZIDS");
                    string oldTag = Utils.LerParametro(tubo, "ZFIACAO");

                    if (string.IsNullOrEmpty(oldCirc) && string.IsNullOrEmpty(oldTag)) continue;

                    string newCirc = oldCirc;
                    string newTag = oldTag;

                    foreach (var q in quadros)
                    {
                        if (!q.IsValidObject) continue;
                        string qNome = Utils.LerParametro(q, "Panel Name");
                        if (string.IsNullOrEmpty(qNome)) qNome = q.Name;
                        string qId = q.Id.ToString();

                        newCirc = Utils.CleanString(newCirc, false, qNome, qId);
                        newTag = Utils.CleanString(newTag, true, qNome, qId);
                    }

                    if (oldCirc != newCirc) Utils.WriteParam(tubo, "ZIDS", newCirc);
                    if (oldTag != newTag) Utils.WriteParam(tubo, "ZFIACAO", newTag);
                }
                catch (Exception ex) {  }
            }
        }

        private void ConfigurarProjetoETabelas(Document doc)
        {
            try
            {
                string nomeTabela = "Aegia - Quantitativo de Fiação e Tubos";
                var tabelas = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().ToList();
                
                if (!tabelas.Any(x => x.Name == nomeTabela))
                {
                    ElementId categoryId = new ElementId((long)BuiltInCategory.OST_Conduit);
                    ViewSchedule schedule = ViewSchedule.CreateSchedule(doc, categoryId);
                    schedule.Name = nomeTabela;

                    var schedulableFields = schedule.Definition.GetSchedulableFields();
                    
                    void AddFieldSafely(BuiltInParameter bip)
                    {
                        var field = schedulableFields.FirstOrDefault(f => f.ParameterId.Value == (long)bip);
                        if (field != null) schedule.Definition.AddField(field);
                    }

                    AddFieldSafely(BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM);
                    AddFieldSafely(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                    AddFieldSafely(BuiltInParameter.CURVE_ELEM_LENGTH);

                    foreach (var field in schedulableFields)
                    {
                        string fn = field.GetName(doc);
                        if (fn == "ZIDS" || fn == "ZFIACAO")
                        {
                            schedule.Definition.AddField(field);
                        }
                    }
                }
                Autodesk.Revit.UI.TaskDialog.Show("Configuração Aegia", "Estrutura base de tabelas do projeto verificada com sucesso!");
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Erro de Configuração", $"Não foi possível criar as tabelas: {ex.Message}");
            }
        }

        private void PrepararVista(Document doc, View view)
        {
            if (AcaoAtual == ModoAcao.Transparencia)
            {
                if (view.IsTemporaryHideIsolateActive()) view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
                if (!modoFantasmaAtivo) { AtivarModoFantasma(doc, view); modoFantasmaAtivo = true; }
            }
            else
            {
                if (modoFantasmaAtivo) { DesativarModoFantasma(doc, view); modoFantasmaAtivo = false; }
                if (view.IsTemporaryHideIsolateActive()) view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate);
            }
        }

        private void AplicarDestaque(Document doc, View view, List<ElementId> ids, Color cor, int peso)
        {
            if (ids == null || ids.Count == 0) return;
            OverrideGraphicSettings ogs = new OverrideGraphicSettings();
            ogs.SetSurfaceTransparency(0); 
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
            var cats = new List<BuiltInCategory> { 
                BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting, 
                BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting 
            };
            var infra = new FilteredElementCollector(doc, view.Id).WherePasses(new ElementMulticategoryFilter(cats)).ToElements();

            foreach (Element tubo in infra)
            {
                string zids = Utils.LerParametro(tubo, "ZIDS");
                if (string.IsNullOrEmpty(zids)) continue;

                foreach (var bloco in zids.Split('|'))
                {
                    int s = bloco.IndexOf('['), e = bloco.IndexOf(']');
                    if (s >= 0 && e > s)
                    {
                        string qId = bloco.Substring(s + 1, e - s - 1).Trim();
                        foreach (var cId in bloco.Substring(e + 1).Trim().Split(';'))
                        {
                            string chave = $"{qId}_{cId.Trim()}";
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

        public string GetName() => "Gestor Integrado Aegia";
    }

    // ==========================================
    // GERENCIADOR GLOBAL DE CONFIGURAÇÕES (DRY + Zero Dependências)
    // ==========================================
    public class WorksetItem
    {
        public string Workset { get; set; }
        public bool Dados { get; set; }
        public bool Tomadas { get; set; }
        public bool Ilu { get; set; }
        public bool Forca { get; set; }
    }

    public static class WorksetConfigManager
    {
        public static Dictionary<string, string> CarregarConfiguracoesGlobais(string caminhoArquivoJson)
        {
            var config = new Dictionary<string, string>();
            if (!File.Exists(caminhoArquivoJson)) return config;
            
            try 
            {
                string json = File.ReadAllText(caminhoArquivoJson);
                
                string[] lines = json.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in lines)
                {
                    string tLine = line.Trim();
                    if (!tLine.StartsWith("\"")) continue;
                    int colonIdx = tLine.IndexOf(':');
                    if (colonIdx < 0) continue;
                    
                    int keyEnd = tLine.LastIndexOf('"', colonIdx - 1);
                    if (keyEnd <= 0) continue;
                    string key = UnescapeJsonString(tLine.Substring(1, keyEnd - 1));
                    
                    int valStart = tLine.IndexOf('"', colonIdx);
                    if (valStart < 0) continue;
                    int valEnd = tLine.LastIndexOf('"');
                    if (valEnd <= valStart) continue;
                    string val = UnescapeJsonString(tLine.Substring(valStart + 1, valEnd - valStart - 1));
                    
                    config[key] = val;
                }
            } 
            catch (Exception ex) {  }
            
            return config;
        }

        public class WorksetRowData
        {
            public string Workset { get; set; }
            public bool Dados { get; set; }
            public bool Tomadas { get; set; }
            public bool Ilu { get; set; }
            public bool Forca { get; set; }
        }

        public static List<WorksetRowData> PreencherWorksets(Document doc, WStackPanel dgv, Dictionary<string, string> configSalva)
        {
            List<string> nomesWorksets = new List<string>();
            try 
            {
                if (doc.IsWorkshared)
                {
                    var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset);
                    foreach (Workset ws in worksets) nomesWorksets.Add(ws.Name);
                }
                else nomesWorksets.Add("Projeto Padrão (Local)");

                List<WorksetRowData> dataSource = new List<WorksetRowData>();
                foreach (string wsName in nomesWorksets)
                {
                    string keyDados = $"WS_{wsName}_Dados";
                    string keyTomadas = $"WS_{wsName}_Tomadas";
                    string keyIlu = $"WS_{wsName}_Ilu";
                    string keyForca = $"WS_{wsName}_Forca";

                    bool valDados = configSalva.ContainsKey(keyDados) && configSalva[keyDados] == "True";
                    bool valTomadas = configSalva.ContainsKey(keyTomadas) && configSalva[keyTomadas] == "True";
                    bool valIlu = configSalva.ContainsKey(keyIlu) && configSalva[keyIlu] == "True";
                    bool valForca = configSalva.ContainsKey(keyForca) && configSalva[keyForca] == "True";

                    dataSource.Add(new WorksetRowData { Workset = wsName, Dados = valDados, Tomadas = valTomadas, Ilu = valIlu, Forca = valForca });
                }
                
                dgv.Children.Clear();
                WStackPanel header = new WStackPanel() { Orientation = System.Windows.Controls.Orientation.Horizontal, Background = new WSolidColorBrush(WColors.LightGray), Margin = new WThickness(0,0,0,5) };
                header.Children.Add(new WLabel() { Content = "Workset", Width = 140, FontWeight = System.Windows.FontWeights.Bold });
                header.Children.Add(new WLabel() { Content = "Dados", Width = 60, FontWeight = System.Windows.FontWeights.Bold });
                header.Children.Add(new WLabel() { Content = "Tomadas", Width = 70, FontWeight = System.Windows.FontWeights.Bold });
                header.Children.Add(new WLabel() { Content = "Ilu", Width = 80, FontWeight = System.Windows.FontWeights.Bold });
                header.Children.Add(new WLabel() { Content = "Força", Width = 60, FontWeight = System.Windows.FontWeights.Bold });
                dgv.Children.Add(header);

                foreach (var row in dataSource) {
                    WStackPanel pnl = new WStackPanel() { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new WThickness(0,2,0,2) };
                    pnl.Children.Add(new WLabel() { Content = row.Workset, Width = 140 });
                    
                    var chkDados = new WCheckBox() { IsChecked = row.Dados, Width = 60, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                    chkDados.Checked += (s, e) => row.Dados = true; chkDados.Unchecked += (s, e) => row.Dados = false;
                    pnl.Children.Add(chkDados);
                    
                    var chkTom = new WCheckBox() { IsChecked = row.Tomadas, Width = 70, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                    chkTom.Checked += (s, e) => row.Tomadas = true; chkTom.Unchecked += (s, e) => row.Tomadas = false;
                    pnl.Children.Add(chkTom);
                    
                    var chkIlu = new WCheckBox() { IsChecked = row.Ilu, Width = 80, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                    chkIlu.Checked += (s, e) => row.Ilu = true; chkIlu.Unchecked += (s, e) => row.Ilu = false;
                    pnl.Children.Add(chkIlu);
                    
                    var chkFor = new WCheckBox() { IsChecked = row.Forca, Width = 60, VerticalAlignment = System.Windows.VerticalAlignment.Center };
                    chkFor.Checked += (s, e) => row.Forca = true; chkFor.Unchecked += (s, e) => row.Forca = false;
                    pnl.Children.Add(chkFor);
                    
                    dgv.Children.Add(pnl);
                }
                return dataSource;
            }
            catch (Exception ex) { WMessageBox.Show("Erro ao carregar Worksets: " + ex.Message, "Erro", WMessageBoxButton.OK, WMessageBoxImage.Error); return new List<WorksetRowData>(); }
        }

        public static void SalvarConfiguracoes(List<WorksetRowData> rows, Dictionary<string, string> configSalva, string caminhoArquivoJson, WWindow formParaFechar = null)
        {
            try 
            {
                if (rows != null)
                {
                    foreach (var row in rows)
                    {
                        string wsName = row.Workset;
                        configSalva[$"WS_{wsName}_Dados"] = row.Dados.ToString();
                        configSalva[$"WS_{wsName}_Tomadas"] = row.Tomadas.ToString();
                        configSalva[$"WS_{wsName}_Ilu"] = row.Ilu.ToString();
                        configSalva[$"WS_{wsName}_Forca"] = row.Forca.ToString();
                    }
                }

                // Serialização manual à prova de falhas para dicionários chave-valor plano
                List<string> linhas = new List<string>();
                foreach (var kvp in configSalva) 
                {
                    string key = EscapeJsonString(kvp.Key);
                    string val = EscapeJsonString(kvp.Value);
                    linhas.Add($"  \"{key}\": \"{val}\"");
                }
                string jsonOut = "{\n" + string.Join(",\n", linhas) + "\n}";

                File.WriteAllText(caminhoArquivoJson, jsonOut);
                
                WMessageBox.Show("Regras salvas com sucesso no nwrconfig.json!", "Aegia", WMessageBoxButton.OK, WMessageBoxImage.Information);
                if (formParaFechar != null) formParaFechar.Close();
            } 
            catch (Exception ex) { WMessageBox.Show("Erro crítico ao salvar: " + ex.Message, "Erro", WMessageBoxButton.OK, WMessageBoxImage.Error); }
        }

        private static string EscapeJsonString(string s) 
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string UnescapeJsonString(string s) 
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }

    // ==========================================
    // INTERFACE PRINCIPAL UNIFICADA (WPF)
    // ==========================================
    public class GestorForm : WWindow
    {
        private Document docAtivo;
        private GestorEventHandler handler;
        private ExternalEvent exEvent;
        private GestorEventHandler.ModoAcao modoVisualizacaoAtivo;
        
        private Dictionary<FamilyInstance, List<ElectricalSystem>> dadosProjeto;
        
        // Tab 1
        private System.Windows.Controls.StackPanel chkListQuadros;
        private System.Windows.Controls.ScrollViewer scrollQuadros;
        private WTextBox txtBuscaQuadros;
        private List<FamilyInstance> listaCompletaQuadros;
        private Dictionary<ElementId, bool> estadoChecksQuadros;

        // Tab 2
        private WTextBox txtBuscaEditor;
        private WTabControl tabCategorias;
        private Dictionary<string, WTreeView> arvoreAbas = new Dictionary<string, WTreeView>();
        private string[] nomesAbas = { "Tomadas", "Força", "Iluminação", "Dados/CFTV", "Outros" };

        // Tab 3
        private WStackPanel dgvWorksets;
        private WScrollViewer scrollWorksets;
        private List<WorksetConfigManager.WorksetRowData> currentWorksetRows = new List<WorksetConfigManager.WorksetRowData>();
        private Dictionary<string, string> configSalva = new Dictionary<string, string>();
        private string caminhoArquivoJson;

        public GestorForm(Document doc, Dictionary<FamilyInstance, List<ElectricalSystem>> arvoreDados, GestorEventHandler h, ExternalEvent ev, bool isPlanta)
        {
            docAtivo = doc;
            handler = h; exEvent = ev;
            dadosProjeto = arvoreDados;
            modoVisualizacaoAtivo = isPlanta ? GestorEventHandler.ModoAcao.Transparencia : GestorEventHandler.ModoAcao.Isolar;

            this.Title = "Aegia - Gestor Integrado de Rotas"; 
            this.Width = 540; this.Height = 650; 
            this.Topmost = true; this.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            this.ResizeMode = System.Windows.ResizeMode.NoResize;

            WTabControl tabMain = new WTabControl();
            
            WTabItem pageCalc = new WTabItem() { Header = "1. Calcular Rotas" };
            WCanvas canvasCalc = new WCanvas();
            pageCalc.Content = canvasCalc;

            WTabItem pageEdit = new WTabItem() { Header = "2. Auditoria e Edição" };
            WCanvas canvasEdit = new WCanvas();
            pageEdit.Content = canvasEdit;

            WTabItem pageConfig = new WTabItem() { Header = "3. Configurações" };
            WCanvas canvasConfig = new WCanvas();
            pageConfig.Content = canvasConfig;

            MontarAbaCalculadora(canvasCalc);
            MontarAbaEditor(canvasEdit, isPlanta);
            MontarAbaConfiguracoes(canvasConfig);

            tabMain.Items.Add(pageCalc);
            tabMain.Items.Add(pageEdit);
            tabMain.Items.Add(pageConfig);
            this.Content = tabMain;

            this.Closed += (s, e) => {
                handler.AcaoAtual = GestorEventHandler.ModoAcao.Resetar;
                exEvent.Raise();
            };
        }

        private void MontarAbaCalculadora(WCanvas page)
        {
            listaCompletaQuadros = dadosProjeto.Keys.OrderBy(q => q.Name).ToList();
            estadoChecksQuadros = listaCompletaQuadros.ToDictionary(q => q.Id, q => false);

            WLabel lbl = new WLabel() { Content = "Selecione os quadros para roteamento automático:", Width = 400 };
            WCanvas.SetLeft(lbl, 15); WCanvas.SetTop(lbl, 15);
            
            WLabel lblBusca = new WLabel() { Content = "Buscar:", Width = 50, Height = 25 };
            WCanvas.SetLeft(lblBusca, 15); WCanvas.SetTop(lblBusca, 42);

            txtBuscaQuadros = new WTextBox() { Width = 430, Height = 22 };
            WCanvas.SetLeft(txtBuscaQuadros, 65); WCanvas.SetTop(txtBuscaQuadros, 40);
            txtBuscaQuadros.TextChanged += (s, e) => AtualizarListaQuadros(txtBuscaQuadros.Text);

            
            chkListQuadros = new System.Windows.Controls.StackPanel() { Width = 460 };
            scrollQuadros = new System.Windows.Controls.ScrollViewer() { Width = 480, Height = 400, Content = chkListQuadros, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };

            
            WCanvas.SetLeft(scrollQuadros, 15); WCanvas.SetTop(scrollQuadros, 70);

            AtualizarListaQuadros("");

            WButton btnOk = new WButton() { Content = "CALCULAR ROTAS", Width = 230, Height = 40, Background = new WSolidColorBrush(WColors.LightGreen), FontWeight = System.Windows.FontWeights.Bold };
            WCanvas.SetLeft(btnOk, 15); WCanvas.SetTop(btnOk, 480);
            btnOk.Click += (s, e) => {
                handler.QuadrosParaCalcular = listaCompletaQuadros.Where(q => estadoChecksQuadros[q.Id]).ToList();
                if (handler.QuadrosParaCalcular.Count == 0) {
                    WMessageBox.Show("Selecione ao menos um quadro.", "Aviso", WMessageBoxButton.OK, WMessageBoxImage.Warning);
                    return;
                }
                handler.AcaoAtual = GestorEventHandler.ModoAcao.CalcularLote;
                exEvent.Raise();
            };

            WButton btnAll = new WButton() { Content = "Todos Visíveis", Width = 110, Height = 40 };
            WCanvas.SetLeft(btnAll, 255); WCanvas.SetTop(btnAll, 480);
            btnAll.Click += (s, e) => { 
                foreach (System.Windows.UIElement el in chkListQuadros.Children) {
                    if (el is WCheckBox chk) chk.IsChecked = true;
                }
            };

            WButton btnNone = new WButton() { Content = "Nenhum", Width = 120, Height = 40 };
            WCanvas.SetLeft(btnNone, 375); WCanvas.SetTop(btnNone, 480);
            btnNone.Click += (s, e) => { 
                foreach (System.Windows.UIElement el in chkListQuadros.Children) {
                    if (el is WCheckBox chk) chk.IsChecked = false;
                }
            };

            page.Children.Add(lbl); page.Children.Add(lblBusca); page.Children.Add(txtBuscaQuadros);
            page.Children.Add(scrollQuadros); page.Children.Add(btnOk); page.Children.Add(btnAll); page.Children.Add(btnNone);
        }

        private void AtualizarListaQuadros(string filtro)
        {
            if(chkListQuadros != null) chkListQuadros.Children.Clear();
            var itensFiltrados = string.IsNullOrWhiteSpace(filtro) 
                ? listaCompletaQuadros 
                : listaCompletaQuadros.Where(q => q.Name.IndexOf(filtro, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            foreach (var q in itensFiltrados)
            {
                string nome = Utils.LerParametro(q, "Panel Name");
                if (string.IsNullOrEmpty(nome)) nome = q.Name;
                var item = new ComboItemStr { Id = q.Id, Texto = $"{nome} (ID: {q.Id})" };
                var chk = new WCheckBox() { Content = item.Texto, Tag = item.Id, Margin = new WThickness(2) };
                if (estadoChecksQuadros.ContainsKey(q.Id) && estadoChecksQuadros[q.Id]) chk.IsChecked = true;
                
                chk.Checked += (s, e) => { estadoChecksQuadros[(ElementId)((WCheckBox)s).Tag] = true; };
                chk.Unchecked += (s, e) => { estadoChecksQuadros[(ElementId)((WCheckBox)s).Tag] = false; };
                
                chkListQuadros.Children.Add(chk);
            }
        }

        private void MontarAbaEditor(WCanvas page, bool isPlanta)
        {
            WLabel lblInfo = new WLabel() { Content = "Buscar Quadro ou Circuito:", Width = 350, FontWeight = System.Windows.FontWeights.Bold };
            WCanvas.SetLeft(lblInfo, 10); WCanvas.SetTop(lblInfo, 10);
            
            txtBuscaEditor = new WTextBox() { Width = 480, Height = 22 };
            WCanvas.SetLeft(txtBuscaEditor, 10); WCanvas.SetTop(txtBuscaEditor, 35);
            txtBuscaEditor.TextChanged += (s, e) => RecarregarArvoresEditor(txtBuscaEditor.Text.Trim());

            tabCategorias = new WTabControl() { Width = 480, Height = 300 };
            WCanvas.SetLeft(tabCategorias, 10); WCanvas.SetTop(tabCategorias, 65);
            
            foreach (var aba in nomesAbas)
            {
                WTabItem p = new WTabItem() { Header = aba };
                WTreeView tv = new WTreeView() { FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12 };
                
                tv.SelectedItemChanged += (s, e) => { 
                    if (System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftShift) || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightShift))
                    {
                        DispararEdicao(GestorEventHandler.ModoAcao.ComandoExternoShift);
                    }
                    else
                    {
                        ExecutarAcaoGrafica(tv); 
                    }
                };

                arvoreAbas[aba] = tv;
                p.Content = tv;
                tabCategorias.Items.Add(p);
            }

            RecarregarArvoresEditor(""); 

            int yBtns = 370;
            WButton btnSelecionar = new WButton() { Content = "Apenas Selecionar", Width = 155, Height = 28 };
            WCanvas.SetLeft(btnSelecionar, 10); WCanvas.SetTop(btnSelecionar, yBtns);

            WButton btnTransparencia = new WButton() { Content = "Transparência", Width = 155, Height = 28 };
            WCanvas.SetLeft(btnTransparencia, 175); WCanvas.SetTop(btnTransparencia, yBtns);

            WButton btnIsolar = new WButton() { Content = "Isolar Rota", Width = 150, Height = 28 };
            WCanvas.SetLeft(btnIsolar, 340); WCanvas.SetTop(btnIsolar, yBtns);
            
            yBtns += 35;
            WLabel lblEdicao = new WLabel() { Content = "Edição Manual de Infraestrutura Selecionada:", Width = 420, Foreground = new WSolidColorBrush(WColors.DarkGray) };
            WCanvas.SetLeft(lblEdicao, 10); WCanvas.SetTop(lblEdicao, yBtns);

            yBtns += 25;
            WButton btnAdicionar = new WButton() { Content = "+ Adicionar à Rota", Width = 235, Height = 32, Background = new WSolidColorBrush(WColors.LightGreen) };
            WCanvas.SetLeft(btnAdicionar, 10); WCanvas.SetTop(btnAdicionar, yBtns);

            WButton btnRemover = new WButton() { Content = "- Remover da Rota", Width = 235, Height = 32, Background = new WSolidColorBrush(WColors.LightSalmon) };
            WCanvas.SetLeft(btnRemover, 255); WCanvas.SetTop(btnRemover, yBtns);
            
            yBtns += 40;
            WButton btnReset = new WButton() { Content = "RESETAR VISTA GRÁFICA", Width = 480, Height = 35, FontWeight = System.Windows.FontWeights.Bold };
            WCanvas.SetLeft(btnReset, 10); WCanvas.SetTop(btnReset, yBtns);

            if (isPlanta) btnTransparencia.FontWeight = System.Windows.FontWeights.Bold;
            else btnIsolar.FontWeight = System.Windows.FontWeights.Bold;

            btnSelecionar.Click += (s, e) => { modoVisualizacaoAtivo = GestorEventHandler.ModoAcao.Selecionar; AtualizarNegrito(btnSelecionar, btnTransparencia, btnIsolar); ExecutarDaAbaAtiva(); };
            btnTransparencia.Click += (s, e) => { modoVisualizacaoAtivo = GestorEventHandler.ModoAcao.Transparencia; AtualizarNegrito(btnTransparencia, btnSelecionar, btnIsolar); ExecutarDaAbaAtiva(); };
            btnIsolar.Click += (s, e) => { modoVisualizacaoAtivo = GestorEventHandler.ModoAcao.Isolar; AtualizarNegrito(btnIsolar, btnSelecionar, btnTransparencia); ExecutarDaAbaAtiva(); };
            
            btnAdicionar.Click += (s, e) => DispararEdicao(GestorEventHandler.ModoAcao.Adicionar);
            btnRemover.Click += (s, e) => DispararEdicao(GestorEventHandler.ModoAcao.Remover);
            
            btnReset.Click += (s, e) => { handler.AcaoAtual = GestorEventHandler.ModoAcao.Resetar; exEvent.Raise(); };

            page.Children.Add(lblInfo); page.Children.Add(txtBuscaEditor); page.Children.Add(tabCategorias);
            page.Children.Add(btnSelecionar); page.Children.Add(btnTransparencia); page.Children.Add(btnIsolar); 
            page.Children.Add(lblEdicao); page.Children.Add(btnAdicionar); page.Children.Add(btnRemover);
            page.Children.Add(btnReset);
        }

        public void RecarregarDadosDoRevit()
        {
            var quadrosCollector = new FilteredElementCollector(docAtivo).OfCategory(BuiltInCategory.OST_ElectricalEquipment).WhereElementIsNotElementType().Cast<FamilyInstance>().ToList();
            dadosProjeto.Clear();
            foreach (var q in quadrosCollector)
            {
                if (!q.IsValidObject) continue;
                var circs = Utils.ObterCircuitosDoQuadro(q);
                if (circs.Count > 0) dadosProjeto[q] = circs;
            }
            RecarregarArvoresEditor(txtBuscaEditor.Text.Trim());
        }

        private void RecarregarArvoresEditor(string filtroBusca)
        {
            filtroBusca = filtroBusca.ToLower();
            foreach (var tv in arvoreAbas.Values) tv.Items.Clear();

            foreach (var kvp in dadosProjeto)
            {
                var quadro = kvp.Key;
                string qNome = quadro.get_Parameter(BuiltInParameter.RBS_ELEC_PANEL_NAME)?.AsString();
                if (string.IsNullOrEmpty(qNome)) qNome = quadro.Name;
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
                        nosFilhos.Add(new WTreeViewItem() { Header = $"[ {cNumFormatado} ] - {cNome}", Tag = $"{quadro.Id}|{circ.Id}" });
                    }

                    if (nosFilhos.Count > 0)
                    {
                        WTreeViewItem noQ = new WTreeViewItem() { Header = $"{qNome} (ID: {quadro.Id})", Tag = "QUADRO" };
                        foreach (var nf in nosFilhos) noQ.Items.Add(nf);
                        tvTarget.Items.Add(noQ);
                        if (!string.IsNullOrEmpty(filtroBusca)) noQ.IsExpanded = true;
                    }
                }
            }

            var tabsSnapshot = new List<WTabItem>();
            for (int i = 0; i < tabCategorias.Items.Count; i++)
                if (tabCategorias.Items[i] is WTabItem tbi) tabsSnapshot.Add(tbi);

            foreach (WTabItem tb in tabsSnapshot)
            {
                WTreeView t = tb.Content as WTreeView;
                if (t != null)
                {
                    if (t.Items.Count == 0 && tabCategorias.Items.Contains(tb)) tabCategorias.Items.Remove(tb);
                    else if (t.Items.Count > 0 && !tabCategorias.Items.Contains(tb)) tabCategorias.Items.Add(tb);
                }
            }
        }

        private void AtualizarNegrito(WButton ativo, WButton inativo1, WButton inativo2)
        {
            ativo.FontWeight = System.Windows.FontWeights.Bold;
            inativo1.FontWeight = System.Windows.FontWeights.Normal;
            inativo2.FontWeight = System.Windows.FontWeights.Normal;
        }

        private void ExecutarDaAbaAtiva()
        {
            if (tabCategorias.SelectedItem != null)
            {
                WTabItem tab = tabCategorias.SelectedItem as WTabItem;
                if (tab != null && tab.Content is WTreeView tv)
                {
                    ExecutarAcaoGrafica(tv);
                }
            }
        }

        private void ExecutarAcaoGrafica(WTreeView tv)
        {
            if (tv == null || tv.SelectedItem == null) return;
            var node = tv.SelectedItem as WTreeViewItem;
            if (node == null || node.Tag?.ToString() == "QUADRO") return;
            var ids = node.Tag.ToString().Split('|');
            handler.QuadroIdStr = ids[0]; handler.CircuitoIdStr = ids[1];
            handler.AcaoAtual = modoVisualizacaoAtivo; exEvent.Raise();
        }

        private void DispararEdicao(GestorEventHandler.ModoAcao acao)
        {
            if (tabCategorias.SelectedItem != null)
            {
                WTabItem tab = tabCategorias.SelectedItem as WTabItem;
                if (tab != null && tab.Content is WTreeView tv)
                {
                    var node = tv.SelectedItem as WTreeViewItem;
                    if (node == null || node.Tag?.ToString() == "QUADRO")
                    {
                        WMessageBox.Show("Selecione um circuito na lista.", "Aviso", WMessageBoxButton.OK, WMessageBoxImage.Warning);
                        return;
                    }
                    var ids = node.Tag.ToString().Split('|');
                    handler.QuadroIdStr = ids[0]; handler.CircuitoIdStr = ids[1];
                    handler.AcaoAtual = acao; 
                    exEvent.Raise();
                }
            }
        }

        private class ComboItemStr
        {
            public ElementId Id { get; set; }
            public string Texto { get; set; }
            public override string ToString() => Texto;
        }

        // ================= ABA 3: CONFIGURAÇÕES E WORKSETS =================
        private void MontarAbaConfiguracoes(WCanvas page)
        {
            this.caminhoArquivoJson = Utils.ObterCaminhoConfigLib();
            configSalva = WorksetConfigManager.CarregarConfiguracoesGlobais(caminhoArquivoJson);

            WLabel lblInjecao = new WLabel() { 
                Content = "Configuração Inicial do Projeto:", 
                Width = 480, Height = 25, 
                FontWeight = System.Windows.FontWeights.Bold 
            };
            WCanvas.SetLeft(lblInjecao, 15); WCanvas.SetTop(lblInjecao, 15);
            
            WLabel lblInjecaoDesc = new WLabel() { 
                Content = "Verifica parâmetros necessários e cria a tabela de quantitativos Aegia.", 
                Width = 480, Height = 25, Foreground = new WSolidColorBrush(WColors.DarkGray)
            };
            WCanvas.SetLeft(lblInjecaoDesc, 15); WCanvas.SetTop(lblInjecaoDesc, 35);

            WButton btnInjetar = new WButton() { 
                Content = "INJETAR TABELAS E PARÂMETROS", 
                Width = 480, Height = 35, 
                Background = new WSolidColorBrush(WColors.LightSkyBlue), 
                FontWeight = System.Windows.FontWeights.Bold 
            };
            WCanvas.SetLeft(btnInjetar, 15); WCanvas.SetTop(btnInjetar, 60);
            btnInjetar.Click += (s, e) => {
                handler.AcaoAtual = GestorEventHandler.ModoAcao.ConfigurarProjeto;
                exEvent.Raise();
            };

            System.Windows.Shapes.Line linha = new System.Windows.Shapes.Line() { X1 = 0, Y1 = 0, X2 = 480, Y2 = 0, Stroke = new WSolidColorBrush(WColors.LightGray), StrokeThickness = 2 };
            WCanvas.SetLeft(linha, 15); WCanvas.SetTop(linha, 110);

            WLabel lblRegras = new WLabel() { 
                Content = "Regras de Roteamento por Workset (Rede / Local):", 
                Width = 480, Height = 25, 
                FontWeight = System.Windows.FontWeights.Bold 
            };
            WCanvas.SetLeft(lblRegras, 15); WCanvas.SetTop(lblRegras, 120);

            dgvWorksets = new WStackPanel() { Width = 460, Background = new WSolidColorBrush(WColors.White) };
            scrollWorksets = new WScrollViewer() { Width = 480, Height = 310, Content = dgvWorksets, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
            WCanvas.SetLeft(scrollWorksets, 15); WCanvas.SetTop(scrollWorksets, 145);

            currentWorksetRows = WorksetConfigManager.PreencherWorksets(docAtivo, dgvWorksets, configSalva);

            WButton btnSalvar = new WButton() { 
                Content = "SALVAR REGRAS DE ROTEAMENTO", 
                Width = 480, Height = 40, 
                Background = new WSolidColorBrush(System.Windows.Media.Color.FromRgb(91, 204, 46)), 
                Foreground = new WSolidColorBrush(WColors.White), 
                FontWeight = System.Windows.FontWeights.Bold 
            };
            WCanvas.SetLeft(btnSalvar, 15); WCanvas.SetTop(btnSalvar, 465);
            btnSalvar.Click += (s, e) => WorksetConfigManager.SalvarConfiguracoes(currentWorksetRows, configSalva, caminhoArquivoJson);

            page.Children.Add(lblInjecao); page.Children.Add(lblInjecaoDesc); page.Children.Add(btnInjetar);
            page.Children.Add(linha);
            page.Children.Add(lblRegras); page.Children.Add(scrollWorksets); page.Children.Add(btnSalvar);
        }
    }

    // ==========================================
    // CLASSES AUTONOMAS (UI DE SHIFT+CLIQUE)
    // ==========================================
    public class WorksetConfigForm : WWindow
    {
        private WStackPanel dgvWorksets;
        private WScrollViewer scrollWorksets;
        private List<WorksetConfigManager.WorksetRowData> currentWorksetRows = new List<WorksetConfigManager.WorksetRowData>();
        private Dictionary<string, string> configSalva;
        private Document doc;
        private string caminhoArquivoJson;

        public WorksetConfigForm(Document document)
        {
            this.doc = document;
            this.caminhoArquivoJson = Utils.ObterCaminhoConfigLib();
            
            this.Title = "Configuração de Regras de Worksets";
            this.Width = 550; 
            this.Height = 560; 
            this.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            this.Topmost = true; 
            this.ResizeMode = System.Windows.ResizeMode.NoResize;
            this.Background = new WSolidColorBrush(WColors.White);
            
            configSalva = WorksetConfigManager.CarregarConfiguracoesGlobais(caminhoArquivoJson);
            InitializeComponents();
        }

        private void InitializeComponents()
        {
            WCanvas canvas = new WCanvas();
            this.Content = canvas;

            WLabel lblRegras = new WLabel() { 
                Content = "Defina quais tipos de circuitos podem trafegar em cada Workset de infraestrutura:", 
                Width = 500, Height = 35, 
                FontWeight = System.Windows.FontWeights.Bold 
            };
            WCanvas.SetLeft(lblRegras, 15); WCanvas.SetTop(lblRegras, 15);

            dgvWorksets = new WStackPanel() { Width = 460, Background = new WSolidColorBrush(WColors.White) };
            scrollWorksets = new WScrollViewer() { Width = 500, Height = 380, Content = dgvWorksets, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
            WCanvas.SetLeft(scrollWorksets, 15); WCanvas.SetTop(scrollWorksets, 60);

            currentWorksetRows = WorksetConfigManager.PreencherWorksets(doc, dgvWorksets, configSalva);

            WButton btnSalvar = new WButton() { 
                Content = "SALVAR REGRAS DE WORKSETS", 
                Width = 500, Height = 45, 
                Background = new WSolidColorBrush(System.Windows.Media.Color.FromRgb(91, 204, 46)), 
                Foreground = new WSolidColorBrush(WColors.White), 
                FontWeight = System.Windows.FontWeights.Bold 
            };
            WCanvas.SetLeft(btnSalvar, 15); WCanvas.SetTop(btnSalvar, 455);
            btnSalvar.Click += (s, e) => WorksetConfigManager.SalvarConfiguracoes(currentWorksetRows, configSalva, caminhoArquivoJson, this);

            canvas.Children.Add(lblRegras);
            canvas.Children.Add(scrollWorksets);
            canvas.Children.Add(btnSalvar);
        }
    }
    // ==========================================
    // CLASSES DE DADOS E MOTOR DE CÁLCULO
    // ==========================================
    public class FioData
    {
        public string Num { get; set; }
        public bool Fase { get; set; }
        public bool Neutro { get; set; }
        public bool Terra { get; set; }
        public HashSet<string> Retornos { get; set; } = new HashSet<string>();
        public HashSet<string> Paralelos { get; set; } = new HashSet<string>();
    }

    public class CircuitoTotais
    {
        public double F { get; set; } = 0.0;
        public double N { get; set; } = 0.0;
        public double T { get; set; } = 0.0;
        public double R { get; set; } = 0.0;
        public double MaxPath { get; set; } = 0.0;
    }

    public class NetworkRouter
    {
        private Document doc;
        private HashSet<long> catFilter;
        private List<Element> infraFisica;
        private List<Element> conduitsAndTrays;
        private List<Element> allFittings; 
        private Dictionary<string, string> configJson;
        private Dictionary<string, Element> cacheProximidade = new Dictionary<string, Element>();

        public NetworkRouter(Document document)
        {
            doc = document;
            
            catFilter = new HashSet<long> {
                (long)BuiltInCategory.OST_Conduit, (long)BuiltInCategory.OST_ConduitFitting,
                (long)BuiltInCategory.OST_CableTray, (long)BuiltInCategory.OST_CableTrayFitting
            };

            var filterIds = catFilter.Select(id => new ElementId(id)).ToList();

            infraFisica = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(new ElementMulticategoryFilter(filterIds))
                .ToElements()
                .ToList();

            conduitsAndTrays = infraFisica.Where(e => e.Category != null &&
                (e.Category.Id.Value == (long)BuiltInCategory.OST_Conduit || e.Category.Id.Value == (long)BuiltInCategory.OST_CableTray)).ToList();

            var catsConex = new List<ElementId> { new ElementId((long)BuiltInCategory.OST_ConduitFitting), new ElementId((long)BuiltInCategory.OST_CableTrayFitting) };

            allFittings = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(new ElementMulticategoryFilter(catsConex)).ToElements().ToList();

            configJson = WorksetConfigManager.CarregarConfiguracoesGlobais(Utils.ObterCaminhoConfigLib());
        }

        public bool IsWsAllowed(Element infraEl, string tipoCircBruto)
        {
            if (string.IsNullOrEmpty(tipoCircBruto) || infraEl == null || !infraEl.IsValidObject) return true;
            string tipo = tipoCircBruto.ToUpper().Trim();
            string domain = null;
            if (tipo == "ILU" || tipo.Contains("ILUMINAÇÃO")) domain = "Ilu";
            else if (tipo.Contains("TOM") || tipo.Contains("TOMADAS")) domain = "Tomadas";
            else if (tipo.Contains("FOR") || tipo.Contains("FORÇA") || tipo.Contains("FORCA")) domain = "Forca";
            else if (tipo.Contains("DAD") || tipo.Contains("COMUNIC") || tipo.Contains("CFTV")) domain = "Dados";
            
            if (domain == null) return true;

            string wsName = "Projeto Padrão (Local)";
            try {
                if (doc.IsWorkshared && infraEl.WorksetId != WorksetId.InvalidWorksetId) {
                    var ws = doc.GetWorksetTable().GetWorkset(infraEl.WorksetId);
                    if (ws != null) wsName = ws.Name;
                }
            } catch (Exception ex) {  }

            string key = $"WS_{wsName}_{domain}";
            if (configJson.TryGetValue(key, out string val)) return val == "True" || val == "true";
            return true; 
        }

        public bool IsInfra(Element el)
        {
            return el != null && el.IsValidObject && el.Category != null && catFilter.Contains(el.Category.Id.Value);
        }

        public List<Element> GetNeighbors(Element element)
        {
            List<Element> neighbors = new List<Element>();
            try {
                if (element == null || !element.IsValidObject) return neighbors;
                ConnectorManager manager = null;
                if (element is FamilyInstance fi && fi.MEPModel != null) manager = fi.MEPModel.ConnectorManager;
                else if (element is MEPCurve mepCurve) manager = mepCurve.ConnectorManager;

                if (manager != null) {
                    foreach (Connector conn in manager.Connectors) {
                        if (conn.IsConnected) {
                            foreach (Connector refConn in conn.AllRefs) {
                                if (refConn.Owner != null && refConn.Owner.IsValidObject && refConn.Owner.Id != element.Id && !refConn.Owner.GetType().Name.Contains("ElectricalSystem")) {
                                    neighbors.Add(refConn.Owner);
                                }
                            }
                        }
                    }
                }
            } catch (Exception ex) {  }
            return neighbors;
        }

        public HashSet<ElementId> GetPanelEndpoints(Element panel)
        {
            HashSet<ElementId> ends = new HashSet<ElementId>();
            foreach (var n in GetNeighbors(panel)) {
                if (IsInfra(n)) ends.Add(n.Id);
            }

            BoundingBoxXYZ bb = panel.get_BoundingBox(null);
            if (bb != null) {
                XYZ min = new XYZ(bb.Min.X - Utils.PANEL_TOL_FT, bb.Min.Y - Utils.PANEL_TOL_FT, bb.Min.Z - Utils.PANEL_TOL_FT);
                XYZ max = new XYZ(bb.Max.X + Utils.PANEL_TOL_FT, bb.Max.Y + Utils.PANEL_TOL_FT, bb.Max.Z + Utils.PANEL_TOL_FT);

                bool IsInside(XYZ pt) => (pt.X >= min.X && pt.X <= max.X && pt.Y >= min.Y && pt.Y <= max.Y && pt.Z >= min.Z && pt.Z <= max.Z);

                foreach (var infra in infraFisica) {
                    if (ends.Contains(infra.Id)) continue;
                    
                    if (infra.Location is LocationCurve loc) {
                        if (loc.Curve != null && (IsInside(loc.Curve.GetEndPoint(0)) || IsInside(loc.Curve.GetEndPoint(1))))
                            ends.Add(infra.Id);
                    }
                    else if (infra.Location is LocationPoint locPt) {
                        if (IsInside(locPt.Point)) ends.Add(infra.Id);
                    }
                }
            }
            return ends;
        }

        public Element GetClosestInfra(Element element, string tipoCirc)
        {
            if (element == null || !element.IsValidObject) return null;
            string cacheKey = $"{element.Id}_{tipoCirc}";
            if (cacheProximidade.ContainsKey(cacheKey)) return cacheProximidade[cacheKey];

            XYZ ponto = (element.Location as LocationPoint)?.Point;
            if (ponto == null) return null;

            Element infraOk = null;
            double distMin = double.MaxValue;

            foreach (var infra in infraFisica) {
                if (!IsWsAllowed(infra, tipoCirc)) continue;
                LocationCurve loc = infra.Location as LocationCurve;
                if (loc != null && loc.Curve != null) {
                    IntersectionResult proj = loc.Curve.Project(ponto);
                    if (proj != null) {
                        double d = ponto.DistanceTo(proj.XYZPoint);
                        if (d < distMin) { distMin = d; infraOk = infra; }
                    }
                }
            }
            cacheProximidade[cacheKey] = infraOk;
            return infraOk;
        }

        public Element GetConLumin(Element luminaria, string tipoCirc)
        {
            if (luminaria == null || !luminaria.IsValidObject) return null;
            string cacheKey = $"{luminaria.Id}_{tipoCirc}";
            if (cacheProximidade.ContainsKey(cacheKey)) return cacheProximidade[cacheKey];

            string zidcStr = Utils.LerParametro(luminaria, "ZIDC");
            if (!string.IsNullOrWhiteSpace(zidcStr))
            {
                try {
                    string rawId = new string(zidcStr.Where(char.IsDigit).ToArray());
                    if (!string.IsNullOrEmpty(rawId))
                    {
                        Element forcedElement = Utils.GetElementSafe(doc, rawId);
                        if (forcedElement != null && forcedElement.IsValidObject)
                        {
                            cacheProximidade[cacheKey] = forcedElement;
                            return forcedElement;
                        }
                    }
                } catch (Exception ex) {  } 
            }

            XYZ ponto = (luminaria.Location as LocationPoint)?.Point;
            if (ponto == null) return null;

            Parameter pElev = luminaria.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            if (pElev != null && pElev.HasValue) {
                if (pElev.StorageType == StorageType.Double) ponto = new XYZ(ponto.X, ponto.Y, ponto.Z + pElev.AsDouble());
                else if (pElev.StorageType == StorageType.String) {
                    string strElev = pElev.AsString();
                    if (!string.IsNullOrEmpty(strElev)) {
                        string limpa = new string(strElev.Where(c => char.IsDigit(c) || c == '.' || c == ',' || c == '-').ToArray());
                        double val;
                        if (double.TryParse(limpa.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out val)) {
                            ponto = new XYZ(ponto.X, ponto.Y, ponto.Z + (val / Utils.FT_TO_M));
                        }
                    }
                }
            }

            long lumWsId = Utils.GetWorksetId(luminaria);
            Element infraOkConex = null, infraOkWs = null;
            double distMinConex = double.MaxValue, distMinWs = double.MaxValue;

            foreach (var conexao in allFittings) {
                if (lumWsId != -1 && Utils.GetWorksetId(conexao) != lumWsId) continue;
                if (!IsWsAllowed(conexao, tipoCirc)) continue;

                LocationPoint locPt = conexao.Location as LocationPoint;
                LocationCurve locCv = conexao.Location as LocationCurve;
                
                double? dist = null;
                if (locPt != null) dist = ponto.DistanceTo(locPt.Point);
                else if (locCv != null && locCv.Curve != null) {
                    var proj = locCv.Curve.Project(ponto);
                    if (proj != null) dist = ponto.DistanceTo(proj.XYZPoint);
                }

                if (dist.HasValue) {
                    if (dist.Value < distMinWs) { distMinWs = dist.Value; infraOkWs = conexao; }
                    if (Utils.LerParametro(conexao, "ZTIPOFAM").Trim().ToLower() == "conex") {
                        if (dist.Value < distMinConex) { distMinConex = dist.Value; infraOkConex = conexao; }
                    }
                }
            }

            Element infraOk = infraOkConex ?? infraOkWs;
            if (infraOk != null) Utils.WriteParam(luminaria, "ZIDC", infraOk.Id.ToString());
            
            cacheProximidade[cacheKey] = infraOk;
            return infraOk;
        }

        public List<Element> FindPath(Element startEl, Element endEl, string tipoCirc)
        {
            if (startEl == null || endEl == null) return new List<Element>();

            Element start = IsInfra(startEl) ? startEl :
                ((startEl.Category?.Id.Value == (long)BuiltInCategory.OST_LightingFixtures) ? GetConLumin(startEl, tipoCirc) : GetClosestInfra(startEl, tipoCirc));

            Element end = IsInfra(endEl) ? endEl :
                ((endEl.Category?.Id.Value == (long)BuiltInCategory.OST_LightingFixtures) ? GetConLumin(endEl, tipoCirc) : GetClosestInfra(endEl, tipoCirc));

            if (start == null || end == null) return new List<Element>();
            if (start.Id == end.Id) return new List<Element> { start };

            HashSet<ElementId> ends = GetPanelEndpoints(endEl);
            if (ends.Count == 0) return new List<Element>();
            if (ends.Contains(start.Id)) return new List<Element> { start };

            var queue = new System.Collections.Generic.List<Element>();
            HashSet<ElementId> visited = new HashSet<ElementId>();
            Dictionary<ElementId, Element> parentMap = new Dictionary<ElementId, Element>();

            queue.Add(start);
            visited.Add(start.Id);
            parentMap[start.Id] = null;

            Element foundEnd = null;

            while (queue.Count > 0)
            {
                Element node = queue[0]; queue.RemoveAt(0);

                if (ends.Contains(node.Id)) 
                {
                    foundEnd = node; 
                    break; 
                }

                foreach (Element neighbor in GetNeighbors(node))
                {
                    if (!visited.Contains(neighbor.Id))
                    {
                        if (IsInfra(neighbor) && !IsWsAllowed(neighbor, tipoCirc)) continue;

                        visited.Add(neighbor.Id);
                        parentMap[neighbor.Id] = node;
                        queue.Add(neighbor);
                    }
                }
            }

            List<Element> path = new List<Element>();
            if (foundEnd != null)
            {
                Element curr = foundEnd;
                while (curr != null)
                {
                    path.Add(curr);
                    parentMap.TryGetValue(curr.Id, out curr);
                }
                path.Reverse();
            }
            return path;
        }
    }

    public static class ProcessadorQuadro
    {
        public static void Processar(Document doc, FamilyInstance quadro, NetworkRouter router)
        {
            var circuitos = Utils.ObterCircuitosDoQuadro(quadro);
            if (circuitos.Count == 0) return;

            string quadroNome = Utils.LerParametro(quadro, "Panel Name");
            if (string.IsNullOrEmpty(quadroNome)) quadroNome = quadro.Name;
            string quadroIdStr = quadro.Id.ToString();

            var dictInfra = new Dictionary<ElementId, Dictionary<ElementId, FioData>>();
            var compCircuito = new Dictionary<ElementId, CircuitoTotais>();

            foreach (var circ in circuitos) compCircuito[circ.Id] = new CircuitoTotais();

            void RegistrarFio(Element tubo, ElementId circId, string circNum, bool fase = false, bool neutro = false, bool terra = false, string retornoCmd = null, string paraleloCmd = null)
            {
                if (tubo == null) return;
                if (!dictInfra.ContainsKey(tubo.Id)) dictInfra[tubo.Id] = new Dictionary<ElementId, FioData>();
                if (!dictInfra[tubo.Id].ContainsKey(circId)) dictInfra[tubo.Id][circId] = new FioData { Num = circNum };

                var d = dictInfra[tubo.Id][circId];
                if (fase) d.Fase = true;
                if (neutro) d.Neutro = true;
                if (terra) d.Terra = true;
                if (!string.IsNullOrEmpty(retornoCmd)) d.Retornos.Add(retornoCmd);
                if (!string.IsNullOrEmpty(paraleloCmd)) d.Paralelos.Add(paraleloCmd);
            }

            foreach (ElectricalSystem circ in circuitos)
            {
                string nomeCirc = circ.CircuitNumber;
                string tipoCirc = Utils.LerParametro(circ, "Tipo Circuito").Trim().ToUpper();
                List<Element> elementos = circ.Elements?.Cast<Element>().ToList() ?? new List<Element>();
                if (elementos.Count == 0) continue;

                double maiorDistancia = 0.0;
                foreach (var el in elementos) {
                    if (!el.IsValidObject || el.Id == quadro.Id) continue; 
                    var rotaDireta = router.FindPath(el, quadro, tipoCirc);
                    double distRota = rotaDireta.Where(e => router.IsInfra(e)).Sum(e => Utils.GetLength(e));
                    if (distRota > maiorDistancia) maiorDistancia = distRota;
                }
                compCircuito[circ.Id].MaxPath = maiorDistancia;

                int fasesReais = Utils.ReadParamInt(circ, "FASE");
                if (fasesReais <= 0 && circ.SystemType == ElectricalSystemType.PowerCircuit) fasesReais = circ.PolesNumber; 
                if (fasesReais <= 0) fasesReais = 1;

                bool checkN = Utils.ReadParamInt(circ, "NEUTRO") > 0;
                bool checkT = Utils.ReadParamInt(circ, "TERRA") > 0;

                if (tipoCirc == "ILU")
                {
                    var lumsByCmd = new Dictionary<string, List<Element>>();
                    var intsByCmd = new Dictionary<string, List<Element>>();
                    var outrosEquipamentos = new List<Element>();

                    foreach (var el in elementos) {
                        if (!el.IsValidObject || el.Id == quadro.Id) continue;
                        if (el.Category?.Id.Value == (long)BuiltInCategory.OST_LightingFixtures) {
                            string cmd = Utils.ObterIdComando(el);
                            if (!lumsByCmd.ContainsKey(cmd)) lumsByCmd[cmd] = new List<Element>();
                            lumsByCmd[cmd].Add(el);
                        }
                        else if (el.Category?.Id.Value == (long)BuiltInCategory.OST_LightingDevices) {
                            string cmd = Utils.ObterIdComando(el);
                            if (!intsByCmd.ContainsKey(cmd)) intsByCmd[cmd] = new List<Element>();
                            intsByCmd[cmd].Add(el);
                        } else {
                            outrosEquipamentos.Add(el);
                        }
                    }

                    var todosCmds = new HashSet<string>(lumsByCmd.Keys);
                    todosCmds.UnionWith(intsByCmd.Keys);

                    foreach (string cmd in todosCmds) {
                        var lums = lumsByCmd.ContainsKey(cmd) ? lumsByCmd[cmd] : new List<Element>();
                        var ints = intsByCmd.ContainsKey(cmd) ? intsByCmd[cmd] : new List<Element>();

                        if (ints.Count > 0) {
                            ints = ints.OrderBy(x => router.FindPath(x, quadro, tipoCirc).Count).ToList();
                            var intPrincipal = ints.First();
                            var intFinal = ints.Last();

                            foreach (var e in router.FindPath(intPrincipal, quadro, tipoCirc))
                                if (router.IsInfra(e)) RegistrarFio(e, circ.Id, nomeCirc, fase: true);

                            if (ints.Count > 1) {
                                for (int k = 0; k < ints.Count - 1; k++) {
                                    foreach (var e in router.FindPath(ints[k], ints[k + 1], tipoCirc))
                                        if (router.IsInfra(e)) RegistrarFio(e, circ.Id, nomeCirc, paraleloCmd: cmd);
                                }
                            }

                            foreach (var lum in lums) {
                                foreach (var e in router.FindPath(lum, intFinal, tipoCirc))
                                    if (router.IsInfra(e)) RegistrarFio(e, circ.Id, nomeCirc, retornoCmd: cmd);
                            }
                        }

                        foreach (var lum in lums) {
                            foreach (var e in router.FindPath(lum, quadro, tipoCirc))
                                if (router.IsInfra(e)) 
                                    RegistrarFio(e, circ.Id, nomeCirc, fase: (ints.Count == 0), neutro: checkN, terra: checkT);
                        }
                    }

                    foreach (var el in outrosEquipamentos) {
                        foreach (var e in router.FindPath(el, quadro, tipoCirc))
                            if (router.IsInfra(e)) RegistrarFio(e, circ.Id, nomeCirc, fase: true, neutro: checkN, terra: checkT);
                    }
                }
                else
                {
                    foreach (var el in elementos) {
                        if (!el.IsValidObject || el.Id == quadro.Id) continue;
                        foreach (var e in router.FindPath(el, quadro, tipoCirc))
                            if (router.IsInfra(e)) RegistrarFio(e, circ.Id, nomeCirc, fase: true, neutro: checkN, terra: checkT);
                    }
                }
            }

            foreach (var kvp in dictInfra)
            {
                ElementId tuboId = kvp.Key;
                var trechoData = kvp.Value;
                Element tubo = doc.GetElement(tuboId);
                if (tubo == null || !tubo.IsValidObject) continue;

                double L = Utils.GetLength(tubo);
                List<string> cIdsTrecho = new List<string>();
                List<string> tagPartsTrecho = new List<string>();

                var sortedCids = trechoData.Keys.OrderBy(k => trechoData[k].Num).ToList();

                foreach (var cid in sortedCids)
                {
                    var d = trechoData[cid];
                    cIdsTrecho.Add(cid.ToString());

                    ElectricalSystem circObj = doc.GetElement(cid) as ElectricalSystem;
                    int fases = Utils.ReadParamInt(circObj, "FASE"); 
                    if (fases <= 0) {
                        fases = (circObj != null && circObj.SystemType == ElectricalSystemType.PowerCircuit) ? circObj.PolesNumber : 1;
                    }
                    
                    string tipo = Utils.LerParametro(circObj, "Tipo Circuito").Trim().ToUpper();
                    if (tipo == "ILU" && fases == 2) d.Neutro = false;

                    int qtdF = d.Fase ? fases : 0;
                    int qtdN = d.Neutro ? 1 : 0;
                    int qtdT = d.Terra ? 1 : 0;

                    var comandosSet = new HashSet<string>(d.Retornos);
                    comandosSet.UnionWith(d.Paralelos);
                    int fiosRTotais = 0;

                    List<string> partesFio = new List<string>();
                    if (qtdF > 0) partesFio.Add($"{qtdF}F");
                    if (qtdN > 0) partesFio.Add($"{qtdN}N");
                    if (qtdT > 0) partesFio.Add($"{qtdT}T");

                    var sortedCmds = comandosSet.OrderBy(x => x).ToList();
                    foreach (string cmd in sortedCmds) {
                        int qtdR = (d.Retornos.Contains(cmd) ? 1 : 0) * fases;
                        int qtdP = (d.Paralelos.Contains(cmd) ? 2 : 0) * fases;
                        int totalRCmd = qtdR + qtdP;
                        if (totalRCmd > 0) {
                            partesFio.Add($"{totalRCmd}R({cmd})");
                            fiosRTotais += totalRCmd;
                        }
                    }

                    if (partesFio.Count > 0) tagPartsTrecho.Add($"{d.Num}: {string.Join(", ", partesFio)}");

                    compCircuito[cid].F += L * qtdF;
                    compCircuito[cid].N += L * qtdN;
                    compCircuito[cid].T += L * qtdT;
                    compCircuito[cid].R += L * fiosRTotais;
                }

                string oldCirc = Utils.LerParametro(tubo, "ZIDS");
                string oldTag = Utils.LerParametro(tubo, "ZFIACAO");

                string cleanCirc = Utils.CleanString(oldCirc, false, quadroNome, quadroIdStr);
                string cleanTag = Utils.CleanString(oldTag, true, quadroNome, quadroIdStr);

                string newCirc = cIdsTrecho.Count > 0 ? $"[{quadroIdStr}] {string.Join(";", cIdsTrecho)}" : "";
                string newTag = tagPartsTrecho.Count > 0 ? $"[{quadroNome}] {string.Join("; ", tagPartsTrecho)}" : "";

                string finalCirc = (!string.IsNullOrEmpty(cleanCirc) && !string.IsNullOrEmpty(newCirc)) ? $"{cleanCirc} | {newCirc}" : cleanCirc + newCirc;
                string finalTag = (!string.IsNullOrEmpty(cleanTag) && !string.IsNullOrEmpty(newTag)) ? $"{cleanTag} | {newTag}" : cleanTag + newTag;

                Utils.WriteParam(tubo, "ZIDS", finalCirc);
                Utils.WriteParam(tubo, "ZFIACAO", finalTag);
            }

            foreach (var circ in circuitos)
            {
                var totais = compCircuito[circ.Id];
                Utils.WriteParam(circ, "Comp", totais.MaxPath);
                if (Utils.ReadParamInt(circ, "FASE") == 0 && Utils.ReadParamInt(circ, "NEUTRO") == 0) continue;
                Utils.WriteParam(circ, "Comprimento Fase", totais.F);
                Utils.WriteParam(circ, "Comprimento Neutro", totais.N);
                Utils.WriteParam(circ, "Comprimento Terra", totais.T);
                Utils.WriteParam(circ, "Comprimento Retorno", totais.R);
            }
        }
    }

    // ==========================================
    // UTILITÁRIOS GERAIS
    // ==========================================
    public static class Utils
    {
        public const double FT_TO_M = 0.3048;
        public const double PANEL_TOL_FT = 0.164;

        public static ElementId CriarElementId(string idStr)
        {
            long id;
            if (long.TryParse(idStr, out id)) {
                return new ElementId(id);
            }
            return ElementId.InvalidElementId;
        }

        public static Element GetElementSafe(Document doc, string idStr)
        {
            try {
                ElementId eId = CriarElementId(idStr);
                return doc.GetElement(eId);
            } catch (Exception ex) {  return null; }
        }

        public static string ObterCaminhoConfigLib()
        {
            List<string> searchDirs = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pyRevit", "Extensions"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "pyRevit", "Extensions")
            };

            foreach (string dir in searchDirs)
            {
                if (Directory.Exists(dir))
                {
                    string libPath = Path.Combine(dir, "BIM.extension", "lib");
                    if (Directory.Exists(libPath)) return Path.Combine(libPath, "nwrconfig.json");
                }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nwrconfig.json"); 
        }

        public static List<ElectricalSystem> ObterCircuitosDoQuadro(FamilyInstance q)
        {
            try { return q.MEPModel?.GetAssignedElectricalSystems()?.Cast<ElectricalSystem>().ToList() ?? new List<ElectricalSystem>(); } 
            catch (Exception ex) {  return new List<ElectricalSystem>(); }
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
            string numStr = new string(texto.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            int result = 0;
            if (!string.IsNullOrEmpty(numStr) && int.TryParse(numStr, out result)) return result;
            return 0;
        }

        public static string LerParametro(Element el, string nome)
        {
            if (el == null || !el.IsValidObject) return "";
            Parameter p = el.LookupParameter(nome);
            if (p != null && p.HasValue) return p.AsString() ?? p.AsValueString() ?? "";
            return "";
        }

        public static void WriteParam(Element el, string paramName, object value)
        {
            try {
                if (el == null || !el.IsValidObject) return;
                Parameter param = el.LookupParameter(paramName);
                if (param == null || param.IsReadOnly) return;

                if (param.StorageType == StorageType.Double && value is double) {
                    double d = (double)value;
                    param.Set(d / FT_TO_M);
                }
                else if (param.StorageType == StorageType.Integer && value is int) {
                    int i = (int)value;
                    param.Set(i);
                }
                else if (param.StorageType == StorageType.String) {
                    if (value is double) {
                        double dv = (double)value;
                        param.Set(dv.ToString("F2"));
                    }
                    else param.Set(value.ToString());
                }
            } catch (Exception ex) {  }
        }

        public static int ReadParamInt(Element el, string paramName)
        {
            try {
                Parameter p = el.LookupParameter(paramName);
                if (p != null && p.HasValue) {
                    if (p.StorageType == StorageType.Integer) return p.AsInteger();
                    if (p.StorageType == StorageType.Double) return (int)p.AsDouble();
                    int val;
                    if (p.StorageType == StorageType.String && int.TryParse(p.AsString().Trim(), out val)) return val;
                }
            } catch (Exception ex) {  }
            return 0;
        }

        public static double GetLength(Element el)
        {
            double comp = 0.0;
            try {
                if (el == null || !el.IsValidObject) return 0.0;
                Parameter p = el.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                if (p != null && p.HasValue) comp = p.AsDouble();
                else {
                    var geom = el.get_Geometry(new Options());
                    if (geom != null) {
                        foreach (var geoObj in geom) {
                            if (geoObj is Curve) comp += ((Curve)geoObj).Length;
                            else if (geoObj is GeometryInstance) {
                                foreach (var gObj in ((GeometryInstance)geoObj).GetInstanceGeometry())
                                    if (gObj is Curve) comp += ((Curve)gObj).Length;
                            }
                        }
                    }
                }
            } catch (Exception ex) {  }
            return comp * FT_TO_M;
        }

        public static string ObterIdComando(Element elem)
        {
            Parameter bip = elem.get_Parameter(BuiltInParameter.RBS_ELEC_SWITCH_ID_PARAM);
            if (bip != null && bip.HasValue && !string.IsNullOrWhiteSpace(bip.AsString())) return bip.AsString().Trim();
            string[] nomes = { "Switch ID", "ID do interruptor", "Comando" };
            foreach (string n in nomes) {
                Parameter p = elem.LookupParameter(n);
                if (p != null && p.HasValue && !string.IsNullOrWhiteSpace(p.AsString())) return p.AsString().Trim();
            }
            return elem.Id.ToString(); 
        }

        public static long GetWorksetId(Element el)
        {
            if (el != null && el.IsValidObject && el.WorksetId != WorksetId.InvalidWorksetId)
                return el.WorksetId.IntegerValue; 
            return -1;
        }

        public static string CleanString(string existingStr, bool isTag, string quadroNome, string quadroIdStr)
        {
            if (string.IsNullOrEmpty(existingStr)) return "";
            var parts = existingStr.Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries);
            List<string> kept = new List<string>();
            foreach (var p in parts) {
                string trimmed = p.Trim();
                if (isTag) { if (!trimmed.StartsWith($"[{quadroNome}]")) kept.Add(trimmed); }
                else { if (!trimmed.StartsWith($"[{quadroIdStr}]") && !trimmed.StartsWith($"{quadroNome} {{")) kept.Add(trimmed); }
            }
            return string.Join(" | ", kept);
        }

        public static string AddToZids(string zids, string qId, string cIdStr)
        {
            var dict = ParseZids(zids);
            if (!dict.ContainsKey(qId)) dict[qId] = new HashSet<string>();
            dict[qId].Add(cIdStr);
            return BuildZids(dict);
        }

        public static string RemoveFromZids(string zids, string qId, string cIdStr)
        {
            var dict = ParseZids(zids);
            if (dict.ContainsKey(qId)) {
                dict[qId].Remove(cIdStr);
                if (dict[qId].Count == 0) dict.Remove(qId);
            }
            return BuildZids(dict);
        }

        private static Dictionary<string, HashSet<string>> ParseZids(string zids)
        {
            var map = new Dictionary<string, HashSet<string>>();
            if (string.IsNullOrWhiteSpace(zids)) return map;
            
            var blocks = zids.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var block in blocks)
            {
                int s = block.IndexOf('['); int e = block.IndexOf(']');
                if (s >= 0 && e > s)
                {
                    string q = block.Substring(s + 1, e - s - 1).Trim();
                    string circsRaw = block.Substring(e + 1);
                    var circs = circsRaw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                                        .Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x));
                    
                    if (!map.ContainsKey(q)) map[q] = new HashSet<string>();
                    foreach (var c in circs) map[q].Add(c);
                }
            }
            return map;
        }

        private static string BuildZids(Dictionary<string, HashSet<string>> map)
        {
            List<string> blocks = new List<string>();
            foreach (var kvp in map)
            {
                if (kvp.Value.Count > 0)
                {
                    var sortedCircs = kvp.Value.OrderBy(x => ExtrairNumero(x)).ToList();
                    blocks.Add($"[{kvp.Key}] {string.Join(";", sortedCircs)}");
                }
            }
            return string.Join(" | ", blocks);
        }
    }
}