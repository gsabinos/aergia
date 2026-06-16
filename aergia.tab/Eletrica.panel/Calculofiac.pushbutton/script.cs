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

            var cfgCalc = WorksetConfigManager.CarregarConfiguracoesGlobais(Utils.ObterCaminhoConfigLib());
            bool terraUnico = cfgCalc.TryGetValue("TerraUnicoPorTrecho", out string _tu) && (_tu == "True" || _tu == "true");

            // FCT: temperatura ambiente (default 30 °C) e isolação (PVC default / EPR-XLPE).
            double tempAmb = 30.0;
            if (cfgCalc.TryGetValue("TemperaturaAmbiente", out string _ta))
                double.TryParse(_ta.Replace(",", "."), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out tempAmb);
            if (tempAmb <= 0) tempAmb = 30.0;
            bool isEPR = cfgCalc.TryGetValue("IsolacaoTipo", out string _iso) && _iso != null && _iso.ToUpper().Contains("EPR");

            using (Transaction t = new Transaction(doc, "Calcular Cabos e Rotas em Lote"))
            {
                t.Start();
                try
                {
                    // Limpeza em lote: Varre toda a infraestrutura e limpa os dados dos quadros que serão recalculados
                    LimparInfraestruturaDosQuadros(doc, QuadrosParaCalcular);

                    // Acumulador de ocupação por trecho (compartilhado entre todos os quadros do lote)
                    var ocupacaoPorTubo = new Dictionary<ElementId, OcupacaoTrecho>();

                    foreach (var quadro in QuadrosParaCalcular)
                    {
                        if (quadro.IsValidObject) ProcessadorQuadro.Processar(doc, quadro, router, ocupacaoPorTubo, terraUnico);
                    }
                    doc.Regenerate();

                    int trechosExcedidos = GravarOcupacao(doc, ocupacaoPorTubo);
                    GravarFatoresCorrecao(doc, ocupacaoPorTubo, tempAmb, isEPR);
                    t.Commit();

                    string msg = "Cálculo e Roteamento concluídos com sucesso!";
                    if (trechosExcedidos > 0)
                        msg += $"\n\nATENÇÃO: {trechosExcedidos} trecho(s) acima do limite de ocupação (NBR 5410).";
                    WMessageBox.Show(msg, "Aegia", WMessageBoxButton.OK, WMessageBoxImage.Information);
                    OnCalculationDone?.Invoke();
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    WMessageBox.Show($"Ocorreu um erro técnico:\n\n{ex.Message}\n{ex.StackTrace}", "Erro na Transação", WMessageBoxButton.OK, WMessageBoxImage.Error);
                }
            }
        }

        // Passe final: converte a área de cabos acumulada por trecho em % de ocupação (NBR 5410)
        private int GravarOcupacao(Document doc, Dictionary<ElementId, OcupacaoTrecho> ocupacaoPorTubo)
        {
            int excedidos = 0;
            foreach (var kvp in ocupacaoPorTubo)
            {
                Element tubo = doc.GetElement(kvp.Key);
                if (tubo == null || !tubo.IsValidObject || tubo.Category == null) continue;

                OcupacaoTrecho dados = kvp.Value;
                if (dados.AreaCabosMM2 <= 0) continue;

                long catId = tubo.Category.Id.Value;
                bool isEletroduto = (catId == (long)BuiltInCategory.OST_Conduit);
                bool isEletrocalha = (catId == (long)BuiltInCategory.OST_CableTray);
                if (!isEletroduto && !isEletrocalha) continue;

                double areaInterna = isEletroduto
                    ? NormaNBR.AreaInternaEletroduto(tubo)
                    : NormaNBR.AreaInternaEletrocalha(tubo);
                if (areaInterna <= 0) continue;

                double ocupPct = dados.AreaCabosMM2 / areaInterna * 100.0;

                double limitePct = isEletroduto
                    ? NormaNBR.LimiteEletroduto(dados.NumCondutores) * 100.0
                    : NormaNBR.LIM_ELETROCALHA * 100.0;

                bool excedeu = ocupPct > limitePct;
                if (excedeu) excedidos++;

                Utils.WriteParamNumber(tubo, "ZOCUPACAO", Math.Round(ocupPct, 1));
                Utils.WriteParam(tubo, "ZOCUPSTATUS", excedeu ? "EXCEDIDO" : "OK");
            }
            return excedidos;
        }

        // Passe final: FCA (Tabela 42) e FCT (Tabela 40) por circuito.
        // FCA = fator do MAIOR agrupamento (nº de circuitos) entre os eletrodutos que o circuito percorre.
        // FCT = global, conforme temperatura ambiente e isolação configuradas.
        private void GravarFatoresCorrecao(Document doc, Dictionary<ElementId, OcupacaoTrecho> ocupacaoPorTubo, double tempAmbienteC, bool isEPR)
        {
            // Maior agrupamento experimentado por cada circuito ao longo da sua rota.
            var maxAgrupPorCirc = new Dictionary<ElementId, int>();
            foreach (var kvp in ocupacaoPorTubo)
            {
                int g = kvp.Value.Circuitos.Count;
                foreach (var cid in kvp.Value.Circuitos)
                {
                    if (!maxAgrupPorCirc.TryGetValue(cid, out int atual) || g > atual)
                        maxAgrupPorCirc[cid] = g;
                }
            }

            double fct = Math.Round(NormaNBR.FatorTemperatura(tempAmbienteC, isEPR), 2);

            foreach (var kvp in maxAgrupPorCirc)
            {
                Element circ = doc.GetElement(kvp.Key);
                if (circ == null || !circ.IsValidObject) continue;

                double fca = Math.Round(NormaNBR.FatorAgrupamento(kvp.Value), 2);
                Utils.WriteParamNumber(circ, "FCA", fca);
                Utils.WriteParamNumber(circ, "FCT", fct);
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

                    // Zera a ocupação; o passe final regrava para os trechos roteados nesta execução
                    Utils.ClearParam(tubo, "ZOCUPACAO");
                    Utils.WriteParam(tubo, "ZOCUPSTATUS", "");
                }
                catch (Exception ex) {  }
            }
        }

        private void ConfigurarProjetoETabelas(Document doc)
        {
            try
            {
                // Cria/vincula todos os parâmetros customizados do Aegia antes de montar a tabela
                string relatorio = GarantirParametrosProjeto(doc);

                string nomeTabela = "Aegia - Quantitativo de Fiação e Tubos";
                var tabelas = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().ToList();

                ViewSchedule schedule = tabelas.FirstOrDefault(x => x.Name == nomeTabela);
                if (schedule == null)
                {
                    ElementId categoryId = new ElementId((long)BuiltInCategory.OST_Conduit);
                    schedule = ViewSchedule.CreateSchedule(doc, categoryId);
                    schedule.Name = nomeTabela;

                    var sf0 = schedule.Definition.GetSchedulableFields();
                    void AddFieldSafely(BuiltInParameter bip)
                    {
                        var field = sf0.FirstOrDefault(f => f.ParameterId.Value == (long)bip);
                        if (field != null) schedule.Definition.AddField(field);
                    }
                    AddFieldSafely(BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM);
                    AddFieldSafely(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                    AddFieldSafely(BuiltInParameter.CURVE_ELEM_LENGTH);
                }

                // Garante que os campos Z estejam na tabela (inclui reaparecer após retipagem de parâmetro).
                var jaNoSchedule = new HashSet<string>();
                foreach (var fid in schedule.Definition.GetFieldOrder())
                    jaNoSchedule.Add(schedule.Definition.GetField(fid).GetName());
                foreach (var field in schedule.Definition.GetSchedulableFields())
                {
                    string fn = field.GetName(doc);
                    if ((fn == "ZIDS" || fn == "ZFIACAO" || fn == "ZOCUPACAO" || fn == "ZOCUPSTATUS") && !jaNoSchedule.Contains(fn))
                        schedule.Definition.AddField(field);
                }

                Autodesk.Revit.UI.TaskDialog.Show("Configuração Aegia", "Estrutura base de tabelas e parâmetros verificada.\n\n" + relatorio);
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Erro de Configuração", $"Não foi possível criar as tabelas: {ex.Message}");
            }
        }

        // Conteúdo padrão do arquivo de parâmetros compartilhados Aegia (GUIDs fixos -> consistente
        // entre projetos/máquinas). Usado para criar o arquivo caso ele não exista / esteja corrompido.
        private const string SP_CONTEUDO =
            "# This is a Revit shared parameter file.\r\n" +
            "# Do not edit manually.\r\n" +
            "*META\tVERSION\tMINVERSION\r\n" +
            "META\t2\t1\r\n" +
            "*GROUP\tID\tNAME\r\n" +
            "GROUP\t1\tAegia\r\n" +
            "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n" +
            "PARAM\t7e2a0001-0001-4001-8001-000000000001\tZIDS\tTEXT\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a0002-0001-4001-8001-000000000002\tZFIACAO\tTEXT\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a0011-0001-4001-8001-000000000011\tZOCUPACAO\tNUMBER\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a0004-0001-4001-8001-000000000004\tZOCUPSTATUS\tTEXT\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a0005-0001-4001-8001-000000000005\tZTIPOFAM\tTEXT\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a0006-0001-4001-8001-000000000006\tZIDC\tTEXT\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a0007-0001-4001-8001-000000000007\tTipo Circuito\tTEXT\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a0012-0001-4001-8001-000000000012\tBitola\tNUMBER\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a0009-0001-4001-8001-000000000009\tFASE\tINTEGER\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a0013-0001-4001-8001-000000000013\tNEUTRO\tYESNO\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a0014-0001-4001-8001-000000000014\tTERRA\tYESNO\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a000c-0001-4001-8001-00000000000c\tComp\tLENGTH\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a000d-0001-4001-8001-00000000000d\tComprimento Fase\tLENGTH\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a000e-0001-4001-8001-00000000000e\tComprimento Neutro\tLENGTH\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a000f-0001-4001-8001-00000000000f\tComprimento Terra\tLENGTH\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a0010-0001-4001-8001-000000000010\tComprimento Retorno\tLENGTH\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a0015-0001-4001-8001-000000000015\tCIRCID\tTEXT\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a0016-0001-4001-8001-000000000016\tSWID\tTEXT\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a0017-0001-4001-8001-000000000017\tFCA\tNUMBER\t\t1\t1\t\t1\t0\r\n" +
            "PARAM\t7e2a0018-0001-4001-8001-000000000018\tFCT\tNUMBER\t\t1\t1\t\t1\t0\r\n";

        // Cria/vincula TODOS os parâmetros customizados do Aegia a partir de um arquivo de
        // parâmetros compartilhados (GUIDs fixos). Idempotente: pula o que já existe por nome
        // (não conflita com parâmetros de projeto pré-existentes). Auto-curável: regrava o
        // arquivo SP se estiver ausente/corrompido. Retorna relatório do que foi feito.
        private string GarantirParametrosProjeto(Document doc)
        {
            Autodesk.Revit.ApplicationServices.Application app = doc.Application;

            var infra4 = new[] { BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ConduitFitting, BuiltInCategory.OST_CableTray, BuiltInCategory.OST_CableTrayFitting };
            var infraCurvas = new[] { BuiltInCategory.OST_Conduit, BuiltInCategory.OST_CableTray };
            var fittings = new[] { BuiltInCategory.OST_ConduitFitting, BuiltInCategory.OST_CableTrayFitting };
            var lum = new[] { BuiltInCategory.OST_LightingFixtures };
            var circ = new[] { BuiltInCategory.OST_ElectricalCircuit };
            var anno = new[] { BuiltInCategory.OST_GenericAnnotation };  // tags do SmartTags (CIRCID/SWID)

            var specs = new List<KeyValuePair<string, BuiltInCategory[]>>
            {
                new KeyValuePair<string, BuiltInCategory[]>("ZIDS", infra4),
                new KeyValuePair<string, BuiltInCategory[]>("ZFIACAO", infra4),
                new KeyValuePair<string, BuiltInCategory[]>("ZOCUPACAO", infraCurvas),
                new KeyValuePair<string, BuiltInCategory[]>("ZOCUPSTATUS", infraCurvas),
                new KeyValuePair<string, BuiltInCategory[]>("ZTIPOFAM", fittings),
                new KeyValuePair<string, BuiltInCategory[]>("ZIDC", lum),
                new KeyValuePair<string, BuiltInCategory[]>("Tipo Circuito", circ),
                new KeyValuePair<string, BuiltInCategory[]>("Bitola", circ),
                new KeyValuePair<string, BuiltInCategory[]>("FASE", circ),
                new KeyValuePair<string, BuiltInCategory[]>("NEUTRO", circ),
                new KeyValuePair<string, BuiltInCategory[]>("TERRA", circ),
                new KeyValuePair<string, BuiltInCategory[]>("Comp", circ),
                new KeyValuePair<string, BuiltInCategory[]>("Comprimento Fase", circ),
                new KeyValuePair<string, BuiltInCategory[]>("Comprimento Neutro", circ),
                new KeyValuePair<string, BuiltInCategory[]>("Comprimento Terra", circ),
                new KeyValuePair<string, BuiltInCategory[]>("Comprimento Retorno", circ),
                new KeyValuePair<string, BuiltInCategory[]>("CIRCID", anno),
                new KeyValuePair<string, BuiltInCategory[]>("SWID", anno),
                new KeyValuePair<string, BuiltInCategory[]>("FCA", circ),
                new KeyValuePair<string, BuiltInCategory[]>("FCT", circ),
            };

            // Snapshot dos parâmetros já vinculados (nome -> definição) para detectar tipo divergente.
            var bound = new Dictionary<string, Definition>();
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            it.Reset();
            while (it.MoveNext())
            {
                Definition d = it.Key as Definition;
                if (d != null) bound[d.Name] = d;
            }

            // Arquivo SP em local estável. SEMPRE regravado a partir de SP_CONTEUDO para refletir
            // as definições atuais (evita arquivo "grudado" com tipo antigo, ex.: ZOCUPACAO Texto).
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aegia");
            Directory.CreateDirectory(dir);
            string spPath = Path.Combine(dir, "aegia_parametros.txt");
            File.WriteAllText(spPath, SP_CONTEUDO);

            string oldSp = app.SharedParametersFilename;
            var criados = new List<string>();
            var existiam = new List<string>();
            var retipados = new List<string>();
            var falharam = new List<string>();
            try
            {
                app.SharedParametersFilename = spPath;
                DefinitionFile dfile = app.OpenSharedParameterFile();
                if (dfile == null)
                {
                    File.WriteAllText(spPath, SP_CONTEUDO); // auto-cura
                    dfile = app.OpenSharedParameterFile();
                }
                if (dfile == null)
                    throw new Exception("Não foi possível abrir o arquivo de parâmetros compartilhados:\n" + spPath);

                foreach (var spec in specs)
                {
                    string nome = spec.Key;

                    ExternalDefinition def = null;
                    foreach (DefinitionGroup g in dfile.Groups)
                    {
                        foreach (Definition dd in g.Definitions)
                            if (dd.Name == nome) { def = dd as ExternalDefinition; break; }
                        if (def != null) break;
                    }
                    if (def == null) { falharam.Add(nome + " (ausente no arquivo SP)"); continue; }

                    // Já vinculado? Mantém se o tipo bate; retipa se diverge (ex.: ZOCUPACAO Texto -> Número).
                    bool eraRetipagem = false;
                    if (bound.TryGetValue(nome, out Definition jaDef))
                    {
                        bool mesmoTipo = true;
                        try
                        {
                            ForgeTypeId tA = jaDef.GetDataType();
                            ForgeTypeId tB = def.GetDataType();
                            mesmoTipo = (tA != null && tB != null && tA.TypeId == tB.TypeId);
                        }
                        catch { mesmoTipo = true; }

                        if (mesmoTipo) { existiam.Add(nome); continue; }

                        try { doc.ParameterBindings.Remove(jaDef); eraRetipagem = true; }
                        catch (Exception ex) { falharam.Add(nome + " (não removeu tipo antigo: " + ex.Message + ")"); continue; }
                    }

                    CategorySet cats = app.Create.NewCategorySet();
                    foreach (var bic in spec.Value)
                    {
                        Category cat = Category.GetCategory(doc, bic);
                        if (cat != null) cats.Insert(cat);
                    }
                    if (cats.IsEmpty) { falharam.Add(nome + " (categorias indisponíveis)"); continue; }

                    try
                    {
                        InstanceBinding binding = app.Create.NewInstanceBinding(cats);
                        bool ok = doc.ParameterBindings.Insert(def, binding, GroupTypeId.Electrical);
                        if (!ok) ok = doc.ParameterBindings.ReInsert(def, binding, GroupTypeId.Electrical);
                        if (ok) { if (eraRetipagem) retipados.Add(nome); else criados.Add(nome); }
                        else falharam.Add(nome);
                    }
                    catch (Exception ex) { falharam.Add(nome + " (" + ex.Message + ")"); }
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(oldSp)) app.SharedParametersFilename = oldSp;
            }
            doc.Regenerate();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Arquivo SP: " + spPath);
            sb.AppendLine();
            sb.AppendLine($"Criados ({criados.Count}): " + (criados.Count > 0 ? string.Join(", ", criados) : "—"));
            sb.AppendLine($"Retipados ({retipados.Count}): " + (retipados.Count > 0 ? string.Join(", ", retipados) : "—"));
            sb.AppendLine($"Já existiam ({existiam.Count}): " + (existiam.Count > 0 ? string.Join(", ", existiam) : "—"));
            sb.AppendLine($"Falharam ({falharam.Count}): " + (falharam.Count > 0 ? string.Join(", ", falharam) : "—"));
            return sb.ToString();
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
                        foreach (var tok in bloco.Substring(e + 1).Trim().Split(';'))
                        {
                            string cId = Utils.CidDeToken(tok);
                            if (string.IsNullOrEmpty(cId)) continue;
                            string chave = $"{qId}_{cId}";
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
            scrollWorksets = new WScrollViewer() { Width = 480, Height = 270, Content = dgvWorksets, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
            WCanvas.SetLeft(scrollWorksets, 15); WCanvas.SetTop(scrollWorksets, 145);

            currentWorksetRows = WorksetConfigManager.PreencherWorksets(docAtivo, dgvWorksets, configSalva);

            WCheckBox chkTerraUnico = new WCheckBox() {
                Content = "Cabo terra único por trecho (atribuído ao último circuito)",
                Width = 480, Height = 28,
                VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
                IsChecked = configSalva.ContainsKey("TerraUnicoPorTrecho") && configSalva["TerraUnicoPorTrecho"] == "True"
            };
            WCanvas.SetLeft(chkTerraUnico, 15); WCanvas.SetTop(chkTerraUnico, 425);
            chkTerraUnico.Checked += (s, e) => configSalva["TerraUnicoPorTrecho"] = "True";
            chkTerraUnico.Unchecked += (s, e) => configSalva["TerraUnicoPorTrecho"] = "False";

            // --- Correção de capacidade de condução (NBR 5410): FCT (temperatura + isolação) ---
            WLabel lblTemp = new WLabel() {
                Content = "Temperatura ambiente (°C):", Width = 175, Height = 26,
                VerticalContentAlignment = System.Windows.VerticalAlignment.Center
            };
            WCanvas.SetLeft(lblTemp, 15); WCanvas.SetTop(lblTemp, 455);

            WTextBox txtTemp = new WTextBox() {
                Width = 55, Height = 24,
                Text = (configSalva.ContainsKey("TemperaturaAmbiente") && !string.IsNullOrWhiteSpace(configSalva["TemperaturaAmbiente"])) ? configSalva["TemperaturaAmbiente"] : "30"
            };
            WCanvas.SetLeft(txtTemp, 190); WCanvas.SetTop(txtTemp, 456);

            WCheckBox chkEPR = new WCheckBox() {
                Content = "Isolação EPR/XLPE (90 °C) — desmarcado = PVC (70 °C)",
                Width = 480, Height = 26,
                VerticalContentAlignment = System.Windows.VerticalAlignment.Center,
                IsChecked = configSalva.ContainsKey("IsolacaoTipo") && configSalva["IsolacaoTipo"] != null && configSalva["IsolacaoTipo"].ToUpper().Contains("EPR")
            };
            WCanvas.SetLeft(chkEPR, 15); WCanvas.SetTop(chkEPR, 485);

            WButton btnSalvar = new WButton() {
                Content = "SALVAR REGRAS DE ROTEAMENTO",
                Width = 480, Height = 40,
                Background = new WSolidColorBrush(System.Windows.Media.Color.FromRgb(91, 204, 46)),
                Foreground = new WSolidColorBrush(WColors.White),
                FontWeight = System.Windows.FontWeights.Bold
            };
            WCanvas.SetLeft(btnSalvar, 15); WCanvas.SetTop(btnSalvar, 520);
            btnSalvar.Click += (s, e) => {
                configSalva["TemperaturaAmbiente"] = (txtTemp.Text ?? "30").Trim();
                configSalva["IsolacaoTipo"] = (chkEPR.IsChecked == true) ? "EPR" : "PVC";
                WorksetConfigManager.SalvarConfiguracoes(currentWorksetRows, configSalva, caminhoArquivoJson);
            };

            page.Children.Add(lblInjecao); page.Children.Add(lblInjecaoDesc); page.Children.Add(btnInjetar);
            page.Children.Add(linha);
            page.Children.Add(lblRegras); page.Children.Add(scrollWorksets); page.Children.Add(chkTerraUnico);
            page.Children.Add(lblTemp); page.Children.Add(txtTemp); page.Children.Add(chkEPR);
            page.Children.Add(btnSalvar);
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
        // label do comando -> ElementId do dispositivo de comando (switch). Alimenta o SWID das tags via ZIDS.
        public Dictionary<string, string> CmdDevice { get; set; } = new Dictionary<string, string>();
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

        // Índice geométrico (lazy) de extremidades de infra, para "ponte" entre juntas não
        // conectadas por conector Revit. Tolerância ~5 mm.
        private const double GEO_CELL_FT = 50.0 / 304.8;     // tamanho da célula do índice espacial (~50 mm)
        private const double GEO_PROJ_TOL_FT = 25.0 / 304.8; // distância máx. para considerar duas infra "tocando" (~25 mm)
        private Dictionary<string, List<Element>> geoBody;   // célula -> infra cujo corpo passa por ela

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

        // Sobe na hierarquia de família ANINHADA (SuperComponent) até achar o nível efetivamente
        // ligado à infra por conector. Cobre interruptores/luminárias que são componentes aninhados
        // sem conector próprio — quem carrega a conexão é a família-pai. Retorna null se nenhum nível conecta.
        public Element ElementoConectavel(Element el)
        {
            Element atual = el;
            int guard = 0;
            while (atual != null && atual.IsValidObject && guard++ < 8)
            {
                if (GetNeighbors(atual).Any(IsInfra)) return atual;
                Element pai = (atual as FamilyInstance)?.SuperComponent;
                if (pai == null) break;
                atual = pai;
            }
            return null;
        }

        // Pontos de conexão de uma infra (origens dos conectores; fallback: extremidades da curva).
        private List<XYZ> PontosConectores(Element el)
        {
            var pts = new List<XYZ>();
            try
            {
                ConnectorManager m = null;
                if (el is MEPCurve mc) m = mc.ConnectorManager;
                else if (el is FamilyInstance fi && fi.MEPModel != null) m = fi.MEPModel.ConnectorManager;
                if (m != null) foreach (Connector c in m.Connectors) pts.Add(c.Origin);
            }
            catch { }
            if (pts.Count == 0 && el.Location is LocationCurve lc && lc.Curve != null)
            {
                pts.Add(lc.Curve.GetEndPoint(0));
                pts.Add(lc.Curve.GetEndPoint(1));
            }
            return pts;
        }

        private string GeoKey(double x, double y, double z) =>
            $"{(long)Math.Floor(x / GEO_CELL_FT)}_{(long)Math.Floor(y / GEO_CELL_FT)}_{(long)Math.Floor(z / GEO_CELL_FT)}";

        // Índice espacial do CORPO da infra: cada eletroduto/calha é amostrado ao longo da curva e
        // registrado em todas as células que cruza; fittings entram pela origem dos conectores.
        // Permite achar infra que "toca" um ponto mesmo no meio do trecho (tap sem conexão Revit).
        private void EnsureGeoIndex()
        {
            if (geoBody != null) return;
            geoBody = new Dictionary<string, List<Element>>();

            void Add(Element el, double x, double y, double z)
            {
                string k = GeoKey(x, y, z);
                if (!geoBody.TryGetValue(k, out var lst)) { lst = new List<Element>(); geoBody[k] = lst; }
                if (lst.Count == 0 || lst[lst.Count - 1].Id != el.Id) lst.Add(el);
            }

            foreach (var el in infraFisica)
            {
                if (el == null || !el.IsValidObject) continue;
                if (el.Location is LocationCurve lc && lc.Curve != null)
                {
                    Curve c = lc.Curve;
                    double len = c.ApproximateLength;
                    int n = Math.Max(1, (int)Math.Ceiling(len / GEO_CELL_FT));
                    for (int i = 0; i <= n; i++)
                    {
                        try { XYZ p = c.Evaluate((double)i / n, true); Add(el, p.X, p.Y, p.Z); } catch { }
                    }
                }
                else
                {
                    foreach (var p in PontosConectores(el)) Add(el, p.X, p.Y, p.Z);
                }
            }
        }

        // Vizinhos GEOMÉTRICOS de uma infra: outras infra cujo corpo passa a <= tol de algum ponto de
        // conexão desta (cobre extremidades coincidentes E derivações no meio do trecho), mesmo sem
        // conector Revit. Usa o índice espacial para limitar os candidatos à vizinhança local.
        public List<Element> GetGeoNeighbors(Element el)
        {
            var result = new List<Element>();
            if (el == null || !el.IsValidObject) return result;
            EnsureGeoIndex();

            var seen = new HashSet<ElementId> { el.Id };
            var pts = PontosConectores(el);

            foreach (var p in pts)
            {
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            string k = GeoKey(p.X + dx * GEO_CELL_FT, p.Y + dy * GEO_CELL_FT, p.Z + dz * GEO_CELL_FT);
                            if (!geoBody.TryGetValue(k, out var lst)) continue;

                            foreach (var other in lst)
                            {
                                if (seen.Contains(other.Id)) continue;
                                double dist;
                                if (other.Location is LocationCurve olc && olc.Curve != null)
                                {
                                    IntersectionResult pr = olc.Curve.Project(p);
                                    dist = pr != null ? pr.XYZPoint.DistanceTo(p) : double.MaxValue;
                                }
                                else if (other.Location is LocationPoint olp)
                                    dist = olp.Point.DistanceTo(p);
                                else
                                    dist = double.MaxValue;

                                if (dist <= GEO_PROJ_TOL_FT && seen.Add(other.Id)) result.Add(other);
                            }
                        }
            }
            return result;
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

            // Conectividade real primeiro: se a luminária está fisicamente ligada
            // (conector Revit) a uma infraestrutura permitida, usa-a diretamente.
            {
                XYZ pIns = (luminaria.Location as LocationPoint)?.Point;
                Element melhorCon = null;
                double distCon = double.MaxValue;
                foreach (var n in GetNeighbors(luminaria))
                {
                    if (!IsInfra(n) || !IsWsAllowed(n, tipoCirc)) continue;
                    double d = 0.0;
                    if (pIns != null)
                    {
                        if (n.Location is LocationCurve lc && lc.Curve != null)
                        { var pr = lc.Curve.Project(pIns); if (pr != null) d = pIns.DistanceTo(pr.XYZPoint); }
                        else if (n.Location is LocationPoint lp) d = pIns.DistanceTo(lp.Point);
                    }
                    if (d < distCon) { distCon = d; melhorCon = n; }
                }
                if (melhorCon != null)
                {
                    Utils.WriteParam(luminaria, "ZIDC", melhorCon.Id.ToString());
                    cacheProximidade[cacheKey] = melhorCon;
                    return melhorCon;
                }
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

            // Fallback: nenhuma conexão (fitting) próxima — tenta o eletroduto/eletrocalha mais próximo
            // (GetClosestInfra varre as CURVAS da infra, não só fittings). Cobre luminárias ligadas
            // a um eletroduto sem conexão próxima, que antes ficavam sem rota.
            if (infraOk == null)
                infraOk = GetClosestInfra(luminaria, tipoCirc);

            if (infraOk != null) Utils.WriteParam(luminaria, "ZIDC", infraOk.Id.ToString());

            cacheProximidade[cacheKey] = infraOk;
            return infraOk;
        }

        public List<Element> FindPath(Element startEl, Element endEl, string tipoCirc)
        {
            if (startEl == null || endEl == null) return new List<Element>();

            // Qualquer carga/dispositivo (luminária, INTERRUPTOR, etc.) fisicamente ligado à infra parte
            // DELE MESMO (ou da família-pai, se for componente aninhado) no BFS, para explorar TODOS os
            // eletrodutos/calhas conectados — não fica preso num único toco sem saída escolhido por
            // proximidade. Só sem conexão física cai na âncora por proximidade.
            Element start;
            if (IsInfra(startEl)) start = startEl;
            else
            {
                Element conec = ElementoConectavel(startEl);
                if (conec != null) start = conec;
                else if (startEl.Category?.Id.Value == (long)BuiltInCategory.OST_LightingFixtures) start = GetConLumin(startEl, tipoCirc);
                else start = GetClosestInfra(startEl, tipoCirc);
            }

            Element end;
            if (IsInfra(endEl)) end = endEl;
            else
            {
                Element conec = ElementoConectavel(endEl);
                if (conec != null) end = conec;
                else if (endEl.Category?.Id.Value == (long)BuiltInCategory.OST_LightingFixtures) end = GetConLumin(endEl, tipoCirc);
                else end = GetClosestInfra(endEl, tipoCirc);
            }

            if (start == null || end == null) return new List<Element>();
            if (start.Id == end.Id) return new List<Element> { start };

            HashSet<ElementId> ends = GetPanelEndpoints(endEl);
            if (ends.Count == 0) return new List<Element>();
            if (ends.Contains(start.Id)) return new List<Element> { start };

            // Tier 1 (normal): infra + quadros/equipamentos (OST_ElectricalEquipment, sempre)
            // + luminárias/interruptores que estejam inline na infra (conectados a eletroduto/
            // eletrocalha ou fitting). Carga ligada só a outras cargas não é atravessada.
            Func<Element, bool> tier1 = n =>
            {
                if (IsInfra(n)) return true;
                long? c = n?.Category?.Id.Value;
                if (c == (long)BuiltInCategory.OST_ElectricalEquipment) return true;
                if (c == (long)BuiltInCategory.OST_LightingFixtures || c == (long)BuiltInCategory.OST_LightingDevices)
                    return GetNeighbors(n).Any(IsInfra);
                return false;
            };

            // Tier 2 (fallback): permite atravessar também tomadas (ElectricalFixtures).
            Func<Element, bool> tier2 = n =>
            {
                if (tier1(n)) return true;
                long? c = n?.Category?.Id.Value;
                return c == (long)BuiltInCategory.OST_ElectricalFixtures;
            };

            var path = BfsToEnds(start, ends, tipoCirc, tier1);
            if (path.Count == 0) path = BfsToEnds(start, ends, tipoCirc, tier2);

            // Tier 3 (último recurso): há juntas não conectadas por conector Revit (trechos só
            // "encostados"). Refaz o BFS permitindo ponte geométrica entre infra a cada nó.
            if (path.Count == 0)
                path = BfsToEnds(start, ends, tipoCirc, tier2, usarGeo: true);

            return path;
        }

        // BFS por menos saltos até qualquer endpoint do quadro. 'podeAtravessar' define
        // quais elementos não-endpoint podem ser nós intermediários da rota.
        private List<Element> BfsToEnds(Element start, HashSet<ElementId> ends, string tipoCirc, Func<Element, bool> podeAtravessar, bool usarGeo = false)
        {
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
                if (ends.Contains(node.Id)) { foundEnd = node; break; }

                // Vizinhos por conector Revit + (no modo geo) infra que apenas "encosta" sem conexão.
                var vizinhos = GetNeighbors(node);
                if (usarGeo && IsInfra(node)) vizinhos = vizinhos.Concat(GetGeoNeighbors(node)).ToList();

                foreach (Element neighbor in vizinhos)
                {
                    if (visited.Contains(neighbor.Id)) continue;
                    bool isEnd = ends.Contains(neighbor.Id);
                    if (!isEnd && !podeAtravessar(neighbor)) continue;
                    if (IsInfra(neighbor) && !IsWsAllowed(neighbor, tipoCirc)) continue;

                    visited.Add(neighbor.Id);
                    parentMap[neighbor.Id] = node;
                    queue.Add(neighbor);
                }
            }

            List<Element> path = new List<Element>();
            if (foundEnd != null)
            {
                Element curr = foundEnd;
                while (curr != null) { path.Add(curr); parentMap.TryGetValue(curr.Id, out curr); }
                path.Reverse();
            }
            return path;
        }
    }

    public static class ProcessadorQuadro
    {
        public static void Processar(Document doc, FamilyInstance quadro, NetworkRouter router, Dictionary<ElementId, OcupacaoTrecho> ocupacaoPorTubo = null, bool terraUnicoPorTrecho = false)
        {
            var circuitos = Utils.ObterCircuitosDoQuadro(quadro);
            if (circuitos.Count == 0) return;

            string quadroNome = Utils.LerParametro(quadro, "Panel Name");
            if (string.IsNullOrEmpty(quadroNome)) quadroNome = quadro.Name;
            string quadroIdStr = quadro.Id.ToString();

            var dictInfra = new Dictionary<ElementId, Dictionary<ElementId, FioData>>();
            var compCircuito = new Dictionary<ElementId, CircuitoTotais>();

            foreach (var circ in circuitos) compCircuito[circ.Id] = new CircuitoTotais();

            void RegistrarFio(Element tubo, ElementId circId, string circNum, bool fase = false, bool neutro = false, bool terra = false, string retornoCmd = null, string paraleloCmd = null, Element cmdDevice = null)
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

                // Vincula o label do comando ao ElementId estável do dispositivo de comando (switch).
                string cmdLabel = retornoCmd ?? paraleloCmd;
                if (!string.IsNullOrEmpty(cmdLabel) && cmdDevice != null && cmdDevice.IsValidObject)
                    d.CmdDevice[cmdLabel] = cmdDevice.Id.ToString();
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
                            // Ordena pelos interruptores COM rota ao quadro (exclui os sem rota: Count==0 cairia
                            // em primeiro e tornaria 'intPrincipal' um interruptor sem fase). intPrincipal = o
                            // paralelo roteável mais próximo do quadro; intFinal = o mais distante (lado da luminária).
                            var intsComRota = ints
                                .Select(x => new { sw = x, cnt = router.FindPath(x, quadro, tipoCirc).Count })
                                .Where(a => a.cnt > 0)
                                .OrderBy(a => a.cnt)
                                .Select(a => a.sw)
                                .ToList();
                            if (intsComRota.Count > 0) ints = intsComRota;

                            var intPrincipal = ints.First();
                            var intFinal = ints.Last();

                            foreach (var e in router.FindPath(intPrincipal, quadro, tipoCirc))
                                if (router.IsInfra(e)) RegistrarFio(e, circ.Id, nomeCirc, fase: true);

                            if (ints.Count > 1) {
                                for (int k = 0; k < ints.Count - 1; k++) {
                                    foreach (var e in router.FindPath(ints[k], ints[k + 1], tipoCirc))
                                        if (router.IsInfra(e)) RegistrarFio(e, circ.Id, nomeCirc, paraleloCmd: cmd, cmdDevice: intPrincipal);
                                }
                            }

                            foreach (var lum in lums) {
                                foreach (var e in router.FindPath(lum, intFinal, tipoCirc))
                                    if (router.IsInfra(e)) RegistrarFio(e, circ.Id, nomeCirc, retornoCmd: cmd, cmdDevice: intPrincipal);
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

                // Cabo terra único por trecho: o terra fica só no último circuito (maior número) que o pediu.
                ElementId terraHolderCid = null;
                if (terraUnicoPorTrecho)
                    terraHolderCid = sortedCids.Where(k => trechoData[k].Terra)
                                               .OrderBy(k => Utils.ExtrairNumero(trechoData[k].Num))
                                               .LastOrDefault();

                foreach (var cid in sortedCids)
                {
                    var d = trechoData[cid];

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
                    if (terraUnicoPorTrecho) qtdT = (terraHolderCid != null && cid == terraHolderCid) ? 1 : 0;

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
                            partesFio.Add($"{totalRCmd}R({cmd})");  // ZFIACAO humano: só a letra
                            fiosRTotais += totalRCmd;
                        }
                    }

                    // ZIDS (máquina): token rico cid:número=label~switchId,label~switchId
                    var swParts = new List<string>();
                    foreach (string cmd in sortedCmds) {
                        if (d.CmdDevice.TryGetValue(cmd, out string swid) && !string.IsNullOrEmpty(swid))
                            swParts.Add($"{Utils.SanitizarToken(cmd)}~{swid}");
                    }
                    string richTok = $"{cid}:{Utils.SanitizarToken(d.Num)}";
                    if (swParts.Count > 0) richTok += "=" + string.Join(",", swParts);
                    cIdsTrecho.Add(richTok);

                    if (partesFio.Count > 0) tagPartsTrecho.Add($"{d.Num}: {string.Join(", ", partesFio)}");

                    compCircuito[cid].F += L * qtdF;
                    compCircuito[cid].N += L * qtdN;
                    compCircuito[cid].T += L * qtdT;
                    compCircuito[cid].R += L * fiosRTotais;

                    // Acúmulo de ocupação: área externa dos condutores deste circuito neste trecho
                    if (ocupacaoPorTubo != null)
                    {
                        OcupacaoTrecho acc;
                        if (!ocupacaoPorTubo.TryGetValue(tuboId, out acc))
                        {
                            acc = new OcupacaoTrecho();
                            ocupacaoPorTubo[tuboId] = acc;
                        }

                        // Conta o circuito no trecho para o FCA (agrupamento), independente da bitola.
                        acc.Circuitos.Add(cid);

                        double bitola = NormaNBR.ParseBitola(circObj);
                        if (bitola > 0)
                        {
                            double areaFaseNeutroRet = (qtdF + qtdN + fiosRTotais) * NormaNBR.AreaExternaCaboPVC(bitola);
                            double areaTerra = qtdT * NormaNBR.AreaExternaCaboPVC(NormaNBR.BitolaTerra(bitola));
                            int nCondCirc = qtdF + qtdN + qtdT + fiosRTotais;

                            acc.AreaCabosMM2 += areaFaseNeutroRet + areaTerra;
                            acc.NumCondutores += nCondCirc;
                        }
                    }
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

        // Remove os delimitadores da gramática do ZIDS (| ; : = , ~ [ ]) de um campo livre
        // (número de circuito ou label de comando), para não corromper o parser.
        public static string SanitizarToken(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            foreach (char c in new[] { '|', ';', ':', '=', ',', '~', '[', ']' }) s = s.Replace(c.ToString(), "");
            return s.Trim();
        }

        // Parte de identidade (ElementId do circuito) de um token de ZIDS, rico ou legado.
        // "9876543:12=a~55" -> "9876543";  "9876543" -> "9876543".
        public static string CidDeToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return "";
            string t = token.Trim();
            int i = t.IndexOf(':');
            return (i >= 0 ? t.Substring(0, i) : t).Trim();
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

        // Grava um valor numérico cru (sem conversão de unidade) — para parâmetros tipo Número
        // como ZOCUPACAO. Se o parâmetro ainda for Texto, grava a representação sem unidade.
        public static void WriteParamNumber(Element el, string paramName, double value)
        {
            try {
                if (el == null || !el.IsValidObject) return;
                Parameter param = el.LookupParameter(paramName);
                if (param == null || param.IsReadOnly) return;
                if (param.StorageType == StorageType.Double) param.Set(value);
                else if (param.StorageType == StorageType.Integer) param.Set((int)Math.Round(value));
                else if (param.StorageType == StorageType.String) param.Set(value.ToString("F1"));
            } catch (Exception ex) {  }
        }

        // Limpa o valor de um parâmetro (Texto -> ""; numérico -> sem valor).
        public static void ClearParam(Element el, string paramName)
        {
            try {
                if (el == null || !el.IsValidObject) return;
                Parameter param = el.LookupParameter(paramName);
                if (param == null || param.IsReadOnly) return;
                if (param.StorageType == StorageType.String) param.Set("");
                else param.ClearValue();
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
            if (!dict.ContainsKey(qId)) dict[qId] = new Dictionary<string, string>();
            // Não sobrescreve um token rico (cid:número=...) já existente com um cid puro.
            if (!dict[qId].ContainsKey(cIdStr)) dict[qId][cIdStr] = cIdStr;
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

        // qId -> (cid -> token completo). A chave é sempre o cid (identidade estável);
        // o valor preserva o token rico "cid:número=label~swid,..." quando presente.
        private static Dictionary<string, Dictionary<string, string>> ParseZids(string zids)
        {
            var map = new Dictionary<string, Dictionary<string, string>>();
            if (string.IsNullOrWhiteSpace(zids)) return map;

            var blocks = zids.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var block in blocks)
            {
                int s = block.IndexOf('['); int e = block.IndexOf(']');
                if (s >= 0 && e > s)
                {
                    string q = block.Substring(s + 1, e - s - 1).Trim();
                    string circsRaw = block.Substring(e + 1);
                    var tokens = circsRaw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                                         .Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x));

                    if (!map.ContainsKey(q)) map[q] = new Dictionary<string, string>();
                    foreach (var tok in tokens)
                    {
                        string cid = CidDeToken(tok);
                        if (string.IsNullOrEmpty(cid)) continue;
                        // Mantém o token mais informativo (rico vence puro) ao deduplicar por cid.
                        if (!map[q].ContainsKey(cid) || tok.Length > map[q][cid].Length) map[q][cid] = tok;
                    }
                }
            }
            return map;
        }

        private static string BuildZids(Dictionary<string, Dictionary<string, string>> map)
        {
            List<string> blocks = new List<string>();
            foreach (var kvp in map)
            {
                if (kvp.Value.Count > 0)
                {
                    var sortedToks = kvp.Value.Values.OrderBy(t => ExtrairNumero(CidDeToken(t))).ToList();
                    blocks.Add($"[{kvp.Key}] {string.Join(";", sortedToks)}");
                }
            }
            return string.Join(" | ", blocks);
        }
    }

    // ==========================================
    // OCUPAÇÃO DE CONDUTOS (NBR 5410)
    // ==========================================
    public class OcupacaoTrecho
    {
        public double AreaCabosMM2 { get; set; } = 0.0;  // soma da área externa de todos os condutores
        public int NumCondutores { get; set; } = 0;      // total de condutores físicos no trecho
        public HashSet<ElementId> Circuitos { get; set; } = new HashSet<ElementId>(); // circuitos distintos no trecho (FCA)
    }

    public static class NormaNBR
    {
        // Limites de ocupação (fração da área interna)
        public const double LIM_1COND = 0.53;   // 1 condutor
        public const double LIM_2COND = 0.31;   // 2 condutores
        public const double LIM_3MAIS = 0.40;   // 3 ou mais condutores
        public const double LIM_ELETROCALHA = 0.50; // eletrocalha (por área), configurável

        // Tabela bitola (mm²) -> área externa do condutor (mm²) para cabo PVC 450/750V.
        // Valores de referência NBR 5410 / catálogo de fabricante (área = π/4 · Dext²).
        private static readonly double[] BitolasTab =
            { 1.5, 2.5,  4.0,  6.0, 10.0, 16.0, 25.0, 35.0,  50.0,  70.0,  95.0, 120.0, 150.0, 185.0, 240.0 };
        private static readonly double[] AreaExtTab =
            { 7.5, 10.2, 13.9, 17.3, 32.2, 41.9, 66.5, 81.7, 109.4, 149.6, 198.6, 243.3, 298.6, 363.1, 471.4 };

        public static double LimiteEletroduto(int numCondutores)
        {
            if (numCondutores <= 1) return LIM_1COND;
            if (numCondutores == 2) return LIM_2COND;
            return LIM_3MAIS;
        }

        // FCA - Fator de Correção de Agrupamento (NBR 5410, Tabela 42),
        // arranjo de circuitos agrupados em eletroduto/canaleta (feixe).
        public static double FatorAgrupamento(int nCircuitos)
        {
            if (nCircuitos <= 1) return 1.00;
            if (nCircuitos == 2) return 0.80;
            if (nCircuitos == 3) return 0.70;
            if (nCircuitos == 4) return 0.65;
            if (nCircuitos == 5) return 0.60;
            if (nCircuitos == 6) return 0.57;
            if (nCircuitos == 7) return 0.54;
            if (nCircuitos == 8) return 0.52;
            if (nCircuitos <= 11) return 0.50;   // 9 a 11
            if (nCircuitos <= 15) return 0.45;   // 12 a 15
            if (nCircuitos <= 19) return 0.41;   // 16 a 19
            return 0.38;                          // 20 ou mais
        }

        // FCT - Fator de Correção de Temperatura (NBR 5410, Tabela 40), temperatura ambiente
        // de referência 30 °C. Interpolação linear entre os pontos tabelados; isolação PVC (70 °C)
        // ou EPR/XLPE (90 °C). Fora da faixa, satura no extremo.
        private static readonly double[] TempTab = { 10, 15, 20, 25, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80 };
        private static readonly double[] FctPVC  = { 1.22, 1.17, 1.12, 1.06, 1.00, 0.94, 0.87, 0.79, 0.71, 0.61, 0.50, 0.0, 0.0, 0.0, 0.0 };
        private static readonly double[] FctEPR  = { 1.15, 1.12, 1.08, 1.04, 1.00, 0.96, 0.91, 0.87, 0.82, 0.76, 0.71, 0.65, 0.58, 0.50, 0.41 };

        public static double FatorTemperatura(double tempAmbienteC, bool isEPR)
        {
            double[] f = isEPR ? FctEPR : FctPVC;

            // Determina a faixa útil (PVC tem dados só até 60 °C).
            int maxIdx = f.Length - 1;
            while (maxIdx > 0 && f[maxIdx] <= 0.0) maxIdx--;

            if (tempAmbienteC <= TempTab[0]) return f[0];
            if (tempAmbienteC >= TempTab[maxIdx]) return f[maxIdx];

            for (int i = 0; i < maxIdx; i++)
            {
                if (tempAmbienteC >= TempTab[i] && tempAmbienteC <= TempTab[i + 1])
                {
                    double t = (tempAmbienteC - TempTab[i]) / (TempTab[i + 1] - TempTab[i]);
                    return f[i] + t * (f[i + 1] - f[i]);
                }
            }
            return 1.00;
        }

        // Área externa (mm²) do cabo a partir da bitola: match exato, senão menor faixa >= bitola, senão última.
        public static double AreaExternaCaboPVC(double bitolaMM2)
        {
            if (bitolaMM2 <= 0) return 0.0;
            for (int i = 0; i < BitolasTab.Length; i++)
            {
                if (Math.Abs(BitolasTab[i] - bitolaMM2) < 0.001) return AreaExtTab[i];
                if (BitolasTab[i] >= bitolaMM2) return AreaExtTab[i];
            }
            return AreaExtTab[AreaExtTab.Length - 1];
        }

        // Seção do condutor de proteção (terra) conforme NBR 5410 (Tabela 6.5):
        // S<=16 -> S; 16<S<=35 -> 16; S>35 -> S/2.
        public static double BitolaTerra(double bitolaFase)
        {
            if (bitolaFase <= 16.0) return bitolaFase;
            if (bitolaFase <= 35.0) return 16.0;
            return bitolaFase / 2.0;
        }

        // Lê o parâmetro "Bitola" do circuito e extrai a seção em mm² (aceita "2,5", "3#2,5mm²", "1x4").
        public static double ParseBitola(Element circuito)
        {
            // Bitola como Número (mm², sem unidade): lê o valor direto.
            Parameter pB = circuito?.LookupParameter("Bitola");
            if (pB != null && pB.HasValue && pB.StorageType == StorageType.Double)
                return pB.AsDouble();

            // Fallback texto (ex.: "3#2,5mm²", "1x4").
            string raw = Utils.LerParametro(circuito, "Bitola");
            if (string.IsNullOrWhiteSpace(raw)) return 0.0;

            // Descarta multiplicadores antes de '#' ou 'x' (ex.: "3#2,5" -> "2,5")
            int corte = Math.Max(raw.LastIndexOf('#'), Math.Max(raw.LastIndexOf('x'), raw.LastIndexOf('X')));
            string s = corte >= 0 ? raw.Substring(corte + 1) : raw;

            var sb = new System.Text.StringBuilder();
            foreach (char c in s)
            {
                if (char.IsDigit(c)) sb.Append(c);
                else if ((c == '.' || c == ',') && sb.Length > 0) sb.Append('.');
                else if (sb.Length > 0) break;
            }

            double val;
            if (double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out val))
                return val;
            return 0.0;
        }

        // Área interna real do eletroduto (mm²): usa o diâmetro interno do tipo se disponível,
        // senão o diâmetro nominal com fator 0,85 (estimativa para eletroduto rígido).
        public static double AreaInternaEletroduto(Element conduit)
        {
            double dInternoMM = LerDiametroMM(conduit, BuiltInParameter.RBS_CONDUIT_INNER_DIAM_PARAM);
            if (dInternoMM <= 0)
            {
                double dNominalMM = LerDiametroMM(conduit, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                if (dNominalMM <= 0) return 0.0;
                dInternoMM = dNominalMM * 0.85;
            }
            return Math.PI * dInternoMM * dInternoMM / 4.0;
        }

        // Área interna da eletrocalha (mm²) = largura × altura.
        public static double AreaInternaEletrocalha(Element tray)
        {
            double largMM = LerDiametroMM(tray, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
            double altMM = LerDiametroMM(tray, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
            if (largMM <= 0 || altMM <= 0) return 0.0;
            return largMM * altMM;
        }

        private static double LerDiametroMM(Element el, BuiltInParameter bip)
        {
            try
            {
                Parameter p = el.get_Parameter(bip);
                if (p != null && p.HasValue && p.StorageType == StorageType.Double)
                    return p.AsDouble() * 304.8; // pés -> mm
            }
            catch { }
            return 0.0;
        }
    }
}