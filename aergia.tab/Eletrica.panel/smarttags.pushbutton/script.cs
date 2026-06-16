using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

using View = Autodesk.Revit.DB.View;
using WWindow = System.Windows.Window;
using WButton = System.Windows.Controls.Button;
using WCheckBox = System.Windows.Controls.CheckBox;
using WComboBox = System.Windows.Controls.ComboBox;
using WLabel = System.Windows.Controls.Label;
using WTabControl = System.Windows.Controls.TabControl;
using WTabItem = System.Windows.Controls.TabItem;
using WDataGrid = System.Windows.Controls.DataGrid;
using WDataGridTextColumn = System.Windows.Controls.DataGridTextColumn;
using WDataGridComboBoxColumn = System.Windows.Controls.DataGridComboBoxColumn;
using WDataGridCheckBoxColumn = System.Windows.Controls.DataGridCheckBoxColumn;
using WGrid = System.Windows.Controls.Grid;
using WCanvas = System.Windows.Controls.Canvas;
using WTextBox = System.Windows.Controls.TextBox;
using WThickness = System.Windows.Thickness;

namespace Aegia_Automations
{
    // ==========================================================================================
    // HANDLER PARA EXECUÇÃO SEGURA DE EVENTOS DE INTERFACE MODELESS
    // ==========================================================================================
    public class SalvarConfigHandler : IExternalEventHandler
    {
        public AegiaConfigForm FormReference { get; set; }

        public void Execute(UIApplication app)
        {
            if (FormReference != null)
            {
                FormReference.ExecutarSalvarRevitContext(app.ActiveUIDocument.Document);
            }
        }

        public string GetName() => "Aegia Salvar Configuracoes Handler";
    }

    // Handler para o botão "Atualizar Tags" do formulário modeless: precisa de contexto Revit válido.
    public class AtualizarTagsHandler : IExternalEventHandler
    {
        public void Execute(UIApplication app)
        {
            Document doc = app.ActiveUIDocument?.Document;
            if (doc != null) new SmartTagsCommand().ExecutarAtualizarTags(doc);
        }

        public string GetName() => "Aegia Atualizar Tags Handler";
    }

    [Transaction(TransactionMode.Manual)]
    public class SmartTagsCommand : IExternalCommand
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
        private static extern short GetKeyState(int keyCode);

        private const int VK_SHIFT = 0x10;
        private const int VK_CAPITAL = 0x14;

        
        private List<string> ExtractNumbers(string s) {
            List<string> nums = new List<string>();
            if (string.IsNullOrEmpty(s)) return nums;
            string current = "";
            foreach (char c in s) {
                if (char.IsDigit(c)) current += c;
                else if (current.Length > 0) { nums.Add(current); current = ""; }
            }
            if (current.Length > 0) nums.Add(current);
            return nums;
        }

        private string ReplaceBrackets(string s, char r) {
            if (string.IsNullOrEmpty(s)) return s;
            string res = "";
            bool inBrack = false;
            foreach (char c in s) {
                if (c == '[') inBrack = true;
                else if (c == ']') { inBrack = false; res += r; }
                else if (!inBrack) res += c;
            }
            return res;
        }

        private string KeepNumbersAndPunctuation(string s) {
            if (string.IsNullOrEmpty(s)) return s;
            string res = "";
            foreach (char c in s) {
                if (char.IsDigit(c) || c == ',' || c == '.' || c == '-') res += c;
            }
            return res;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            short initialShiftState = GetAsyncKeyState(VK_SHIFT);
            bool isShiftInvoked = (initialShiftState & 0x8000) != 0;

            if (isShiftInvoked)
            {
                SalvarConfigHandler handler = new SalvarConfigHandler();
                ExternalEvent exEvent = ExternalEvent.Create(handler);
                
                AegiaConfigForm form = new AegiaConfigForm(doc, handler, exEvent);
                handler.FormReference = form;
                
                form.Show(); // Arquitetura Modeless exigida
                return Result.Succeeded;
            }

            string rawProjectName = string.IsNullOrWhiteSpace(doc.ProjectInformation.Name) || doc.ProjectInformation.Name == "Project Name" 
                                    ? doc.Title : doc.ProjectInformation.Name;
            string safeProjectName = string.Join("_", rawProjectName.Split(Path.GetInvalidFileNameChars()));
            
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string bimExtDir = Path.Combine(appData, "pyRevit", "Extensions", "BIM.extension", "lib");
            if (!Directory.Exists(bimExtDir)) try { Directory.CreateDirectory(bimExtDir); } catch { }
            
            string configPath = Path.Combine(bimExtDir, $"aegialt {safeProjectName}.json");
            string logFilePath = Path.Combine(bimExtDir, $"aegialt_memoria_{safeProjectName}.txt");
            
            if (!File.Exists(configPath))
            {
                string oldPath = Path.Combine(appData, "AegiaLT.json");
                if (File.Exists(oldPath)) configPath = oldPath;
                else
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia", "Configurações não encontradas. Segure SHIFT e clique neste botão para configurar.");
                    return Result.Failed;
                }
            }

            var config = ParseJsonSimple(File.ReadAllText(configPath));

            if (activeView is ViewSheet sheet)
            {
                return ExecutarModoDescarregar(uidoc, doc, sheet, config, logFilePath);
            }
            else
            {
                return ExecutarModoLancar(uidoc, doc, activeView, config, logFilePath);
            }
        }

        // ==========================================================================================
        // MODO: DESCARREGAR TAGS (DRAFTING VIEW)
        // ==========================================================================================
        private Result ExecutarModoDescarregar(UIDocument uidoc, Document doc, ViewSheet sheet, Dictionary<string, string> config, string logFilePath)
        {
            if (!File.Exists(logFilePath))
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aegia", "Arquivo de memória não encontrado. Lembre-se de lançar as Tags com CapsLock ativado.");
                return Result.Cancelled;
            }

            var memoria = ExtrairELimparMemoria(doc, logFilePath);
            if (memoria.Count == 0)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aegia", "A memória está vazia ou todas as Tags armazenadas foram deletadas do projeto.");
                return Result.Cancelled;
            }

            HashSet<ElementId> vistasNaFolha = new HashSet<ElementId>();
            List<int> escalasVistas = new List<int>();

            foreach (ElementId vpId in sheet.GetAllViewports())
            {
                Viewport vp = doc.GetElement(vpId) as Viewport;
                if (vp != null && vp.IsValidObject) 
                {
                    vistasNaFolha.Add(vp.ViewId);
                    View v = doc.GetElement(vp.ViewId) as View;
                    if (v != null && v.IsValidObject) escalasVistas.Add(v.Scale);
                }
            }

            foreach (var kvp in memoria) kvp.Value.RemoveAll(c => !vistasNaFolha.Contains(c.ViewId));
            var chavesVazias = memoria.Where(kvp => kvp.Value.Count == 0).Select(kvp => kvp.Key).ToList();
            foreach (var k in chavesVazias) memoria.Remove(k);

            if (memoria.Count == 0)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aviso", "Nenhum tubo registrado na memória pertence às plantas presentes nesta Folha.");
                return Result.Cancelled;
            }

            var cacheSimbolos = new Dictionary<string, FamilySymbol>();
            var colSimbolos = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).WhereElementIsElementType(); 
            foreach (FamilySymbol s in colSimbolos)
            {
                if (s == null || !s.IsValidObject || s.Category == null) continue;
                
                long catId = s.Category.Id.Value;
                if (catId == (long)BuiltInCategory.OST_GenericAnnotation || catId == (long)BuiltInCategory.OST_MultiCategoryTags)
                {
                    cacheSimbolos[$"{s.Family.Name} - {s.Name}"] = s;
                }
            }

            config.TryGetValue("CHAMADA_GEN", out string chamadaGenKey);
            FamilySymbol symChamadaGen = null;
            if (!string.IsNullOrEmpty(chamadaGenKey)) cacheSimbolos.TryGetValue(chamadaGenKey, out symChamadaGen);

            if (symChamadaGen == null)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Erro", "Família de 'Chamada Externa (Genérica)' não configurada.");
                return Result.Failed;
            }

            List<string> filtrosAtivos = (config.ContainsKey("FILTROS_ATIVOS") ? config["FILTROS_ATIVOS"] : "").Split('|').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            var naturalComparer = new NaturalStringComparer();

            var memoriaOrdenada = memoria
                .Select(kvp => new KeyValuePair<ElementId, List<CircuitoLog>>(
                    kvp.Key,
                    kvp.Value
                        .Where(c => filtrosAtivos.Count == 0 || filtrosAtivos.Any(f => c.TipoCircuito.ToUpper().Contains(f)))
                        .OrderBy(c => FormatarParaOrdemAlfabetica(c.Numero))
                        .ToList()
                ))
                .Where(kvp => kvp.Value.Count > 0)
                .OrderBy(kvp => {
                    Element c = doc.GetElement(kvp.Key);
                    if (c == null || !c.IsValidObject || c.Category == null) return "";
                    return GetParamStringOrValue(c, BuiltInParameter.ALL_MODEL_MARK);
                }, naturalComparer)
                .ToList();

            ViewFamilyType draftingType = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.Drafting);

            if (draftingType == null) return Result.Failed;

            ViewDrafting novaVista = null;
            int escalaDominante = escalasVistas.Count > 0 ? escalasVistas.Min() : 100;

            using (Transaction t1 = new Transaction(doc, "Criar Vista Aegia"))
            {
                t1.Start();
                novaVista = ViewDrafting.Create(doc, draftingType.Id);
                novaVista.Scale = escalaDominante;
                
                string baseName = $"Fiação - {sheet.SheetNumber} - {sheet.Name}";
                string finalName = baseName;
                int countName = 1;
                while (new FilteredElementCollector(doc).OfClass(typeof(View)).Any(v => v.Name == finalName))
                {
                    finalName = $"{baseName} ({countName++})";
                }
                novaVista.Name = finalName;
                t1.Commit();
            }

            uidoc.ActiveView = novaVista;

            using (Transaction t2 = new Transaction(doc, "Gerar Legenda de Fiação Aegia"))
            {
                t2.Start();

                if (!symChamadaGen.IsActive) symChamadaGen.Activate();
                foreach (var sym in cacheSimbolos.Values) if (!sym.IsActive) sym.Activate();

                double viewScale = novaVista.Scale; 
                double currentY = 0.0; 
                int gruposCriados = 0;

                double margemX = (1.0 / 304.8) * viewScale; 
                double margemY = (2.0 / 304.8) * viewScale; 
                double espacoGrupos = (6.0 / 304.8) * viewScale; 

                foreach (var kvp in memoriaOrdenada)
                {
                    ElementId condutoId = kvp.Key;
                    List<CircuitoLog> circuitos = kvp.Value;
                    
                    Element conduto = doc.GetElement(condutoId);
                    if (conduto == null || !conduto.IsValidObject || conduto.Category == null) continue;

                    XYZ posChamada = new XYZ(0, currentY, 0);
                    FamilyInstance instChamada = doc.Create.NewFamilyInstance(posChamada, symChamadaGen, novaVista);
                    
                    try
                    {
                        SetParamRobusto(instChamada, new[] { "ELID" }, condutoId.ToString());
                        
                        string valA = GetParamStringOrValueCustom(conduto, "ZZ.ELNIV");
                        string valB = GetParamStringOrValue(conduto, BuiltInParameter.RBS_OFFSET_PARAM);
                        if (string.IsNullOrEmpty(valB)) valB = GetParamStringOrValueCustom(conduto, "Bottom Elevation");
                        if (string.IsNullOrEmpty(valB)) valB = GetParamStringOrValueCustom(conduto, "Elevação inferior");
                        string elevacao = (valA + valB).Trim();

                        string mark = GetParamStringOrValue(conduto, BuiltInParameter.ALL_MODEL_MARK);

                        string size = GetParamStringOrValue(conduto, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                        if (string.IsNullOrEmpty(size)) size = GetParamStringOrValueCustom(conduto, "Tamanho");

                        if (!string.IsNullOrEmpty(size))
                        {
                            size = size.Replace("ø", "").Replace("Ø", "").Trim();
                            if (conduto.Category.Id.Value == (long)BuiltInCategory.OST_Conduit)
                            {
                                size = "Ø" + size;
                            }
                        }

                        SetParamRobusto(instChamada, new[] { "tam" }, size ?? "");
                        SetParamRobusto(instChamada, new[] { "mk" }, mark ?? "");
                        SetParamRobusto(instChamada, new[] { "el" }, elevacao ?? "");
                    }
                    catch { }

                    doc.Regenerate();
                    BoundingBoxXYZ boxChamada = null;
                    try { boxChamada = instChamada.get_BoundingBox(novaVista); } catch { }
                    
                    double alturaChamada = 0;

                    if (boxChamada != null)
                    {
                        alturaChamada = boxChamada.Max.Y - boxChamada.Min.Y;
                        double dxChamada = 0 - boxChamada.Min.X;
                        if (Math.Abs(dxChamada) > 0.0001)
                        {
                            ElementTransformUtils.MoveElement(doc, instChamada.Id, new XYZ(dxChamada, 0, 0));
                        }
                    }

                    double recuoX = (15.0 / 304.8) * viewScale; 
                    double cursorX = recuoX;
                    int countColuna = 0;
                    double maxAltDaLinha = 0; 

                    foreach (CircuitoLog circ in circuitos)
                    {
                        string tipoCirc = circ.TipoCircuito;
                        string famKey = ObterChaveFamilia(config, tipoCirc);
                        if (string.IsNullOrWhiteSpace(famKey) || !cacheSimbolos.TryGetValue(famKey, out FamilySymbol symCirc)) continue;

                        int maxLinha = ParseIntSafe(config, $"MAX_LINHA_{ObterBaseFiltro(tipoCirc)}", 0);

                        if (maxLinha > 0 && countColuna >= maxLinha)
                        {
                            countColuna = 0;
                            cursorX = recuoX; 
                            currentY -= (maxAltDaLinha + margemY); 
                            maxAltDaLinha = 0;
                        }

                        XYZ posTagCircuito = new XYZ(cursorX, currentY, 0);
                        FamilyInstance instCirc = doc.Create.NewFamilyInstance(posTagCircuito, symCirc, novaVista);
                        PreencherViaLog(instCirc, circ);

                        doc.Regenerate();
                        BoundingBoxXYZ box = null;
                        try { box = instCirc.get_BoundingBox(novaVista); } catch { }

                        if (box != null)
                        {
                            double width = box.Max.X - box.Min.X;
                            double height = box.Max.Y - box.Min.Y;
                            maxAltDaLinha = Math.Max(maxAltDaLinha, height);

                            double dx = cursorX - box.Min.X;
                            if (Math.Abs(dx) > 0.0001) 
                            {
                                ElementTransformUtils.MoveElement(doc, instCirc.Id, new XYZ(dx, 0, 0));
                            }

                            cursorX += (width + margemX);
                        }

                        countColuna++;
                    }

                    double saltoY = Math.Max(maxAltDaLinha, alturaChamada);
                    currentY -= (saltoY + espacoGrupos); 
                    gruposCriados++;
                }

                t2.Commit();

                if (gruposCriados > 0)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia", $"Vista de desenho criada com sucesso!\nForam listados {gruposCriados} agrupamentos reais na escala 1:{escalaDominante}.");
                }
            }

            return Result.Succeeded;
        }

        // ==========================================================================================
        // MODO: ATUALIZAR TAGS (PROJETO INTEIRO, VIA ÂNCORAS CIRCID/ELID)
        // ==========================================================================================
        // Relê a identidade VIVA das anotações e reescreve seu conteúdo, mantendo posições.
        // - Tag de circuito (tem CIRCID): relê número/tipo/bitola do circuito e fiação do ZFIACAO do conduto.
        // - Tag de chamada (só ELID): atualiza tam/mk/el do conduto (comportamento legado).
        public Result ExecutarAtualizarTags(Document doc)
        {
            var annotations = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_GenericAnnotation)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .ToList();

            int circuitos = 0, chamadas = 0, orfas = 0;

            using (Transaction t = new Transaction(doc, "Atualizar Tags Aegia"))
            {
                t.Start();

                foreach (var anno in annotations)
                {
                    if (anno == null || !anno.IsValidObject || anno.Category == null) continue;

                    Parameter pCirc = anno.LookupParameter("CIRCID");
                    string circIdStr = pCirc?.AsString();

                    if (!string.IsNullOrWhiteSpace(circIdStr))
                    {
                        // --- Tag de circuito (âncora estável CIRCID) ---
                        if (!long.TryParse(circIdStr, out long cidVal)) continue;
                        Element circ = doc.GetElement(new ElementId(cidVal));
                        if (circ == null || !circ.IsValidObject) { orfas++; continue; }

                        try
                        {
                            string numCirc = GetParamStringOrValue(circ, BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER);
                            if (string.IsNullOrEmpty(numCirc)) numCirc = "S/N";
                            string tipoCirc = GetParamStringOrValueCustom(circ, "Tipo Circuito");

                            bool isTomIlu = tipoCirc.ToUpper().Contains("TOM") || tipoCirc.ToUpper().Contains("ILU");
                            var dadosFio = (0, 0, 0, 0, "");
                            double bitola = 0.0;

                            // Fiação e SWID vêm do conduto referenciado por ELID.
                            string elidStr = anno.LookupParameter("ELID")?.AsString();
                            if (long.TryParse(elidStr, out long condVal))
                            {
                                Element conduto = doc.GetElement(new ElementId(condVal));
                                if (conduto != null && conduto.IsValidObject)
                                {
                                    if (isTomIlu)
                                    {
                                        var mapaFios = ParseFiacao(GetParamStringOrValueCustom(conduto, "ZFIACAO"));
                                        dadosFio = ExtrairDadosFio(mapaFios.ContainsKey(numCirc) ? mapaFios[numCirc] : "");
                                        bitola = ObterBitola(circ);
                                    }

                                    var zidsMap = ParseZids(GetParamStringOrValueCustom(conduto, "ZIDS"));
                                    SetParamRobusto(anno, new[] { "SWID" }, zidsMap.ContainsKey(circIdStr) ? string.Join(",", zidsMap[circIdStr]) : "");
                                }
                            }

                            PreencherViaLog(anno, new CircuitoLog { Numero = numCirc, TipoCircuito = tipoCirc, F = dadosFio.Item1, N = dadosFio.Item2, T = dadosFio.Item3, R = dadosFio.Item4, TextoRet = dadosFio.Item5, Bitola = bitola });
                            circuitos++;
                        }
                        catch { }
                    }
                    else
                    {
                        // --- Tag de chamada (só ELID, comportamento legado) ---
                        string elidStr = anno.LookupParameter("ELID")?.AsString();
                        if (string.IsNullOrWhiteSpace(elidStr) || !long.TryParse(elidStr, out long idVal)) continue;
                        Element elem = doc.GetElement(new ElementId(idVal));
                        if (elem == null || !elem.IsValidObject || elem.Category == null) { orfas++; continue; }
                        try { AtualizarChamada(anno, elem); chamadas++; } catch { }
                    }
                }

                t.Commit();
            }

            Autodesk.Revit.UI.TaskDialog.Show("Aegia | SmartTags",
                $"Atualização concluída.\n\nTags de circuito atualizadas: {circuitos}\nChamadas atualizadas: {chamadas}\nÓrfãs (circuito/elemento ausente): {orfas}");

            return Result.Succeeded;
        }

        // Atualiza tam/mk/el de uma tag de chamada a partir do conduto/elemento referenciado.
        private void AtualizarChamada(FamilyInstance anno, Element elem)
        {
            string valA = GetParamStringOrValueCustom(elem, "ZZ.ELNIV");
            string valB = GetParamStringOrValue(elem, BuiltInParameter.RBS_OFFSET_PARAM);
            if (string.IsNullOrEmpty(valB)) valB = GetParamStringOrValueCustom(elem, "Bottom Elevation");
            if (string.IsNullOrEmpty(valB)) valB = GetParamStringOrValueCustom(elem, "Elevação inferior");
            string elevacao = (valA + valB).Trim();

            string mark = GetParamStringOrValue(elem, BuiltInParameter.ALL_MODEL_MARK);

            string size = GetParamStringOrValue(elem, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
            if (string.IsNullOrEmpty(size)) size = GetParamStringOrValueCustom(elem, "Tamanho");

            if (!string.IsNullOrEmpty(size))
            {
                size = size.Replace("ø", "").Replace("Ø", "").Trim();
                if (elem.Category != null && elem.Category.Id.Value == (long)BuiltInCategory.OST_Conduit)
                    size = "Ø" + size;
            }

            SetParamRobusto(anno, new[] { "tam" }, size ?? "");
            SetParamRobusto(anno, new[] { "mk" }, mark ?? "");
            SetParamRobusto(anno, new[] { "el" }, elevacao ?? "");
        }

        private string GetParamStringOrValue(Element elem, BuiltInParameter paramId)
        {
            try
            {
                Parameter p = elem.get_Parameter(paramId);
                if (p == null) return "";
                if (p.StorageType == StorageType.String) return p.AsString() ?? "";
                return p.AsValueString() ?? p.AsDouble().ToString("0.00", CultureInfo.InvariantCulture);
            }
            catch { return ""; }
        }

        private string GetParamStringOrValueCustom(Element elem, string paramName)
        {
            try
            {
                Parameter p = elem.LookupParameter(paramName);
                if (p == null) return "";
                if (p.StorageType == StorageType.String) return p.AsString() ?? "";
                return p.AsValueString() ?? p.AsDouble().ToString("0.00", CultureInfo.InvariantCulture);
            }
            catch { return ""; }
        }

        // ==========================================================================================
        // MODO: LANÇAR TAGS (OMNIDIRECIONAL COM CORREÇÃO DE BOUNDINGBOX)
        // ==========================================================================================
        private Result ExecutarModoLancar(UIDocument uidoc, Document doc, View activeView, Dictionary<string, string> config, string logFilePath)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string bimExtDir = Path.Combine(appData, "pyRevit", "Extensions", "BIM.extension", "lib");

            var cacheSimbolos = new Dictionary<string, FamilySymbol>();
            var colSimbolos = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).WhereElementIsElementType();

            foreach (FamilySymbol s in colSimbolos)
            {
                if (s == null || !s.IsValidObject || s.Category == null) continue;
                long cId = s.Category.Id.Value;
                if (cId == (long)BuiltInCategory.OST_GenericAnnotation || 
                    cId == (long)BuiltInCategory.OST_MultiCategoryTags ||
                    cId == (long)BuiltInCategory.OST_ConduitTags ||
                    cId == (long)BuiltInCategory.OST_CableTrayTags)
                {
                    cacheSimbolos[$"{s.Family.Name} - {s.Name}"] = s;
                }
            }

            using (Transaction tAct = new Transaction(doc, "Pré-ativar Símbolos"))
            {
                tAct.Start();
                foreach (var sym in cacheSimbolos.Values) if (!sym.IsActive) sym.Activate();
                tAct.Commit();
            }

            List<string> filtrosAtivos = (config.ContainsKey("FILTROS_ATIVOS") ? config["FILTROS_ATIVOS"] : "").Split('|').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            string diamPadrão = config.ContainsKey("DIAMETRO_PADRAO") ? config["DIAMETRO_PADRAO"] : "";
            string normDiamPadrao = NormalizeString(diamPadrão);

            config.TryGetValue("CHAMADA_MULTI", out string multiKey);
            FamilySymbol symMulti = null;
            if (!string.IsNullOrEmpty(multiKey)) cacheSimbolos.TryGetValue(multiKey, out symMulti);

            config.TryGetValue("TAG_ELETRODUTO", out string conduitTagKey);
            FamilySymbol symConduit = null;
            if (!string.IsNullOrEmpty(conduitTagKey)) cacheSimbolos.TryGetValue(conduitTagKey, out symConduit);

            config.TryGetValue("TAG_ELETROCALHA", out string trayTagKey);
            FamilySymbol symTray = null;
            if (!string.IsNullOrEmpty(trayTagKey)) cacheSimbolos.TryGetValue(trayTagKey, out symTray);

            while (true)
            {
                try
                {
                    GetAsyncKeyState(VK_SHIFT); 
                    Reference refConduit = uidoc.Selection.PickObject(ObjectType.Element, new RotaSelectionFilter(), "Selecione o eletroduto/calha (ESC para sair)");
                    XYZ ptCliqueRef = refConduit.GlobalPoint;
                    XYZ ptBase = uidoc.Selection.PickPoint("Segure 'SHIFT' (Opcional) | CapsLock Ativo para Log");

                    short shiftState = GetAsyncKeyState(VK_SHIFT);
                    bool isModifierPressed = (shiftState & 0x8000) != 0;
                    bool isCapsOn = (GetKeyState(VK_CAPITAL) & 0x0001) != 0;

                    Element conduit = doc.GetElement(refConduit);
                    if (conduit == null || !conduit.IsValidObject || conduit.Category == null) continue;

                    long catIdVal = conduit.Category.Id.Value;
                    bool isCableTray = catIdVal == (long)BuiltInCategory.OST_CableTray;
                    bool isConduit = catIdVal == (long)BuiltInCategory.OST_Conduit;

                    if (isCableTray)
                    {
                        isModifierPressed = true;
                    }
                    else if (isConduit && !string.IsNullOrEmpty(normDiamPadrao))
                    {
                        string dStr = GetParamStringOrValue(conduit, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                        if (string.IsNullOrEmpty(dStr)) dStr = GetParamStringOrValueCustom(conduit, "Tamanho") ?? GetParamStringOrValueCustom(conduit, "Size");
                        
                        if (NormalizeString(dStr) == normDiamPadrao)
                            isModifierPressed = false; 
                        else
                            isModifierPressed = true;  
                    }

                    string zids = GetParamStringOrValueCustom(conduit, "ZIDS");
                    string zfiacao = GetParamStringOrValueCustom(conduit, "ZFIACAO");

                    var zidsMap = ParseZids(zids);            // cid -> lista de switchIds
                    var idsCircs = zidsMap.Keys.ToList();
                    var mapaFios = ParseFiacao(zfiacao);

                    XYZ rightDir = activeView.RightDirection;
                    XYZ upDir = activeView.UpDirection;
                    XYZ viewDir = activeView.ViewDirection;

                    Transform viewTransform = Transform.Identity;
                    viewTransform.Origin = activeView.Origin;
                    viewTransform.BasisX = rightDir;
                    viewTransform.BasisY = upDir;
                    viewTransform.BasisZ = viewDir;

                    bool alinharDireita = (ptBase - ptCliqueRef).DotProduct(rightDir) > 0;

                    var circuitosMap = new Dictionary<long, Element>();
                    foreach (string cidStr in idsCircs)
                    {
                        if (long.TryParse(cidStr, out long cidVal) && !circuitosMap.ContainsKey(cidVal))
                        {
                            Element c = doc.GetElement(new ElementId(cidVal));
                            if (c != null && c.IsValidObject && c.Category != null) circuitosMap[cidVal] = c;
                        }
                    }

                    using (Transaction t = new Transaction(doc, "Lançar Tags Smart"))
                    {
                        t.Start();

                        XYZ cursor3D = ptBase;
                        bool isFirstTagOverall = true;

                        if (isModifierPressed)
                        {
                            try
                            {
                                FamilySymbol symUsar = isCableTray ? symTray : symConduit;
                                IndependentTag tagConduite = IndependentTag.Create(doc, activeView.Id, refConduit, false, TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, cursor3D);
                                if (symUsar != null) tagConduite.ChangeTypeId(symUsar.Id);

                                doc.Regenerate();
                                BoundingBoxXYZ boxTag = null;
                                try { boxTag = tagConduite.get_BoundingBox(activeView); } catch { }

                                if (boxTag != null)
                                {
                                    XYZ cursorView = viewTransform.Inverse.OfPoint(cursor3D);
                                    double dxView = alinharDireita ? (cursorView.X - boxTag.Min.X) : (cursorView.X - boxTag.Max.X);
                                    ElementTransformUtils.MoveElement(doc, tagConduite.Id, rightDir * dxView);

                                    double widthTag = boxTag.Max.X - boxTag.Min.X;
                                    double offsetTagMm = isCableTray ? 2.0 : 1.5;
                                    double padding = (offsetTagMm / 304.8) * activeView.Scale;

                                    double step = widthTag + padding;
                                    cursor3D += rightDir * (alinharDireita ? step : -step);
                                }

                                if (isFirstTagOverall)
                                {
                                    doc.Regenerate();
                                    XYZ headPos = tagConduite.TagHeadPosition;
                                    tagConduite.HasLeader = true;
                                    tagConduite.LeaderEndCondition = LeaderEndCondition.Free;
                                    
                                    doc.Regenerate();
                                    XYZ delta = headPos - tagConduite.TagHeadPosition;
                                    if (delta.GetLength() > 0.0001) ElementTransformUtils.MoveElement(doc, tagConduite.Id, delta);

                                    tagConduite.SetLeaderEnd(refConduit, ptCliqueRef);
                                    try
                                    {
                                        double tElbow = (ptCliqueRef - headPos).DotProduct(rightDir) / 2.0;
                                        XYZ elbow = headPos + rightDir * tElbow;
                                        tagConduite.SetLeaderElbow(refConduit, elbow);
                                    }
                                    catch { }

                                    doc.Regenerate();
                                    delta = headPos - tagConduite.TagHeadPosition;
                                    if (delta.GetLength() > 0.0001) 
                                    {
                                        ElementTransformUtils.MoveElement(doc, tagConduite.Id, delta);
                                        tagConduite.SetLeaderEnd(refConduit, ptCliqueRef);
                                    }

                                    isFirstTagOverall = false;
                                }
                            }
                            catch (Exception ex) { File.AppendAllText(Path.Combine(bimExtDir, "erros_tags.txt"), "\nErro Tag Conduite: " + ex.Message); }
                        }
                        
                        if (isCapsOn)
                        {
                            List<string> logLines = new List<string>();
                            string timeStamp = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
                            string tagIdStr = "N/A";

                            if (symMulti != null)
                            {
                                try
                                {
                                    IndependentTag multiTag = IndependentTag.Create(doc, activeView.Id, refConduit, false, TagMode.TM_ADDBY_MULTICATEGORY, TagOrientation.Horizontal, cursor3D);
                                    multiTag.ChangeTypeId(symMulti.Id);

                                    doc.Regenerate();
                                    BoundingBoxXYZ boxTag = null;
                                    try { boxTag = multiTag.get_BoundingBox(activeView); } catch { }

                                    if (boxTag != null)
                                    {
                                        XYZ cursorView = viewTransform.Inverse.OfPoint(cursor3D);
                                        double dxView = alinharDireita ? (cursorView.X - boxTag.Min.X) : (cursorView.X - boxTag.Max.X);
                                        ElementTransformUtils.MoveElement(doc, multiTag.Id, rightDir * dxView);

                                        double widthTag = boxTag.Max.X - boxTag.Min.X;
                                        
                                        Parameter pDist = multiTag.LookupParameter("AEDIST") ?? doc.GetElement(multiTag.GetTypeId())?.LookupParameter("AEDIST");
                                        config.TryGetValue("CHAMADA_MULTI_AEDIST", out string distStrMulti);
                                        double aeDistMulti = CalcularEspaçamento(doc, activeView, pDist, distStrMulti);
                                        
                                        double step = aeDistMulti > 0 ? aeDistMulti : (widthTag + ((1.5 / 304.8) * activeView.Scale));
                                        cursor3D += rightDir * (alinharDireita ? step : -step);
                                    }

                                    if (isFirstTagOverall)
                                    {
                                        doc.Regenerate();
                                        XYZ headPos = multiTag.TagHeadPosition;
                                        multiTag.HasLeader = true;
                                        multiTag.LeaderEndCondition = LeaderEndCondition.Free;
                                        
                                        doc.Regenerate();
                                        XYZ delta = headPos - multiTag.TagHeadPosition;
                                        if (delta.GetLength() > 0.0001) ElementTransformUtils.MoveElement(doc, multiTag.Id, delta);

                                        multiTag.SetLeaderEnd(refConduit, ptCliqueRef);
                                        try
                                        {
                                            double tElbow = (ptCliqueRef - headPos).DotProduct(rightDir) / 2.0;
                                            XYZ elbow = headPos + rightDir * tElbow;
                                            multiTag.SetLeaderElbow(refConduit, elbow);
                                        }                                        
                                        catch { }

                                        doc.Regenerate();
                                        delta = headPos - multiTag.TagHeadPosition;
                                        if (delta.GetLength() > 0.0001) 
                                        {
                                            ElementTransformUtils.MoveElement(doc, multiTag.Id, delta);
                                            multiTag.SetLeaderEnd(refConduit, ptCliqueRef);
                                        }

                                        isFirstTagOverall = false;
                                    }

                                    tagIdStr = multiTag.Id.ToString();
                                }
                                catch (Exception ex) { File.AppendAllText(Path.Combine(bimExtDir, "erros_tags.txt"), "\nErro MultiTag: " + ex.Message); }
                            }

                            foreach (var kvp in circuitosMap)
                            {
                                Element circ = kvp.Value;
                                string numCirc = GetParamStringOrValue(circ, BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER);
                                if (string.IsNullOrEmpty(numCirc)) numCirc = "S/N";
                                string tipoCirc = GetParamStringOrValueCustom(circ, "Tipo Circuito");

                                bool isTomIlu = tipoCirc.ToUpper().Contains("TOM") || tipoCirc.ToUpper().Contains("ILU");
                                var dFio = isTomIlu ? ExtrairDadosFio(mapaFios.ContainsKey(numCirc) ? mapaFios[numCirc] : "") : (0, 0, 0, 0, "");
                                double bitola = isTomIlu ? ObterBitola(circ) : 0.0;

                                // ID_Circ e SWID são acrescentados ao FINAL (parts[7], parts[8]) para não deslocar
                                // os índices que os parsers de memória já esperam (parts[0..6]).
                                string swidLog = zidsMap.ContainsKey(kvp.Key.ToString()) ? string.Join(",", zidsMap[kvp.Key.ToString()]) : "";
                                string linhaLog = $"{timeStamp} | ID_Vista: {activeView.Id} | ID_Conduto: {conduit.Id} | ID_Tag: {tagIdStr} | Circuito: {numCirc} ({tipoCirc}) | F:{dFio.Item1} N:{dFio.Item2} T:{dFio.Item3} R:{dFio.Item4} Ret:[{dFio.Item5}] | Bitola:{bitola.ToString(CultureInfo.InvariantCulture)} | ID_Circ: {kvp.Key} | SWID: {swidLog}";
                                logLines.Add(linhaLog);
                            }

                            if (logLines.Count > 0)
                            {
                                try { File.AppendAllLines(logFilePath, logLines); } catch { }
                            }
                        }
                        else
                        {
                            var circuitosProcessados = new List<Tuple<Element, string, string, string>>();
                            
                            foreach (var kvp in circuitosMap)
                            {
                                string cid = kvp.Key.ToString();
                                Element circ = kvp.Value;
                                string tipoCirc = GetParamStringOrValueCustom(circ, "Tipo Circuito");
                                
                                string tipoUpper = tipoCirc.ToUpper();
                                bool passaFiltro = filtrosAtivos.Count == 0 || filtrosAtivos.Any(f => tipoUpper.Contains(f));
                                
                                if (!passaFiltro) continue;

                                string numCirc = GetParamStringOrValue(circ, BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER);
                                if (string.IsNullOrEmpty(numCirc)) numCirc = "S/N";
                                circuitosProcessados.Add(new Tuple<Element, string, string, string>(circ, numCirc, tipoCirc, cid));
                            }

                            var ordenador = new NaturalStringComparer();
                            circuitosProcessados = circuitosProcessados.OrderBy(c => c.Item2, ordenador).ToList();

                            var tagsParaGrid = new List<Element>();
                            int maxLinhaGrupo = 0;

                            List<List<Element>> listasDeTags = new List<List<Element>>();
                            List<int> limites = new List<int>();

                            foreach (var tupla in circuitosProcessados)
                            {
                                Element circ = tupla.Item1;
                                string numCirc = tupla.Item2;
                                string tipoCirc = tupla.Item3;

                                string famKey = ObterChaveFamilia(config, tipoCirc);
                                if (string.IsNullOrWhiteSpace(famKey) || !cacheSimbolos.TryGetValue(famKey, out FamilySymbol sym)) continue;

                                int maxLinha = ParseIntSafe(config, $"MAX_LINHA_{ObterBaseFiltro(tipoCirc)}", 0);

                                bool isTomIlu = tipoCirc.ToUpper().Contains("TOM") || tipoCirc.ToUpper().Contains("ILU");
                                var dadosFio = isTomIlu ? ExtrairDadosFio(mapaFios.ContainsKey(numCirc) ? mapaFios[numCirc] : "") : (0, 0, 0, 0, "");
                                double bitola = isTomIlu ? ObterBitola(circ) : 0.0;

                                FamilyInstance inst = doc.Create.NewFamilyInstance(cursor3D, sym, activeView);
                                PreencherViaLog(inst, new CircuitoLog { Numero = numCirc, TipoCircuito = tipoCirc, F = dadosFio.Item1, N = dadosFio.Item2, T = dadosFio.Item3, R = dadosFio.Item4, TextoRet = dadosFio.Item5, Bitola = bitola });

                                // Âncoras estáveis para o modo "Atualizar": id do conduto, id do circuito e id(s) do(s) comando(s).
                                string cidAnc = tupla.Item4;
                                SetParamRobusto(inst, new[] { "ELID" }, conduit.Id.ToString());
                                SetParamRobusto(inst, new[] { "CIRCID" }, cidAnc);
                                SetParamRobusto(inst, new[] { "SWID" }, zidsMap.ContainsKey(cidAnc) ? string.Join(",", zidsMap[cidAnc]) : "");

                                if (tagsParaGrid.Count == 0 || maxLinhaGrupo == maxLinha)
                                {
                                    maxLinhaGrupo = maxLinha;
                                    tagsParaGrid.Add(inst);
                                }
                                else
                                {
                                    listasDeTags.Add(tagsParaGrid);
                                    limites.Add(maxLinhaGrupo);
                                    tagsParaGrid = new List<Element>() { inst };
                                    maxLinhaGrupo = maxLinha;
                                }
                            }
                            
                            if (tagsParaGrid.Count > 0)
                            {
                                listasDeTags.Add(tagsParaGrid);
                                limites.Add(maxLinhaGrupo);
                            }

                            doc.Regenerate();

                            int direcaoChave = alinharDireita ? 39 : 37;
                            double currentYOffsetView = -(0.19 / 304.8) * activeView.Scale; 
                            double margemY = 1.5 / 304.8;
                            
                            AnnotationSymbol instanciaLider = null;
                            double menorDist = double.MaxValue;

                            for (int i = 0; i < listasDeTags.Count; i++)
                            {
                                XYZ pontoAncora = cursor3D + upDir * currentYOffsetView;
                                double alturaGrupo = ExecutarMotorGridSmartTags(doc, activeView, listasDeTags[i], limites[i], direcaoChave, pontoAncora, ref isFirstTagOverall);
                                
                                if (!isModifierPressed)
                                {
                                    foreach (Element elem in listasDeTags[i])
                                    {
                                        double dist = (elem.Location as LocationPoint)?.Point.DistanceTo(ptBase) ?? double.MaxValue;
                                        if (dist < menorDist) { menorDist = dist; instanciaLider = elem as AnnotationSymbol; }
                                    }
                                }

                                currentYOffsetView -= (alturaGrupo + margemY);
                            }

                            if (!isModifierPressed && instanciaLider != null)
                            {
                                try 
                                {
                                    instanciaLider.addLeader();
                                    foreach (Leader l in instanciaLider.GetLeaders()) l.End = ptCliqueRef;
                                } 
                                catch { }
                            }

                            if (isFirstTagOverall) { isFirstTagOverall = false; }
                        }

                        t.Commit();
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { break; }
                catch (Exception ex) { Autodesk.Revit.UI.TaskDialog.Show("Aegia", "Erro inesperado: " + ex.Message); break; }
            }

            return Result.Succeeded;
        }

        // ==========================================================================================
        // MOTOR ULTRA ALIGN INCORPORADO PARA AS SMARTTAGS
        // ==========================================================================================
        private double ExecutarMotorGridSmartTags(Document doc, View view, List<Element> elementos, int elementosPorLinha, int direcaoChave, XYZ pontoAncora, ref bool isFirstTagOverall)
        {
            if (elementos.Count == 0) return 0;

            double margem = 0.0;
            double margemY = 1.5 / 304.8;
            
            XYZ rightDir = view.RightDirection;
            XYZ upDir = view.UpDirection;
            XYZ viewDir = view.ViewDirection;

            Transform viewTransform = Transform.Identity;
            viewTransform.Origin = view.Origin;
            viewTransform.BasisX = rightDir;
            viewTransform.BasisY = upDir;
            viewTransform.BasisZ = viewDir;

            XYZ ptView = viewTransform.Inverse.OfPoint(pontoAncora);

            double cursorPrincipal = ptView.X;
            double cursorSecundario = ptView.Y;
            double maxDimLinhaAtual = 0;
            double alturaTotalDoGrid = 0;
            
            bool isRight = direcaoChave == 39; 

            int itensNaLinhaAtual = 0;

            foreach (Element atu in elementos)
            {
                double width = GetVisualWidth(atu, view);
                
                BoundingBoxXYZ box = null;
                try { box = atu.get_BoundingBox(view); } catch { }
                if (box == null) continue;
                double height = box.Max.Y - box.Min.Y;

                Parameter pDist = atu.LookupParameter("AEDIST");
                if (pDist == null && atu is FamilyInstance fi) pDist = fi.Symbol?.LookupParameter("AEDIST");
                
                double aeDist = CalcularEspaçamento(doc, view, pDist, null);
                if (aeDist > 0) width = aeDist;

                if (elementosPorLinha > 0 && itensNaLinhaAtual >= elementosPorLinha) 
                {
                    cursorPrincipal = ptView.X; 
                    cursorSecundario -= (maxDimLinhaAtual + margemY); 
                    
                    alturaTotalDoGrid += (maxDimLinhaAtual + margemY); 
                    maxDimLinhaAtual = 0;
                    itensNaLinhaAtual = 0;
                }

                if (isFirstTagOverall)
                {
                    isFirstTagOverall = false;
                    
                    double nextX = isRight ? box.Max.X : box.Min.X;
                    if (aeDist > 0)
                    {
                        nextX = isRight ? box.Min.X + aeDist : box.Max.X - aeDist;
                    }
                    
                    cursorPrincipal = nextX;
                    maxDimLinhaAtual = Math.Max(maxDimLinhaAtual, height);
                }
                else
                {
                    double dx = 0;
                    double dy = cursorSecundario - ptView.Y;

                    if (isRight) {
                        dx = cursorPrincipal - box.Min.X; 
                        cursorPrincipal += width + margem; 
                        maxDimLinhaAtual = Math.Max(maxDimLinhaAtual, height);
                    } else { 
                        dx = cursorPrincipal - box.Max.X; 
                        cursorPrincipal -= (width + margem); 
                        maxDimLinhaAtual = Math.Max(maxDimLinhaAtual, height);
                    }

                    XYZ vetorMovimento = rightDir * dx + upDir * dy;
                    ElementTransformUtils.MoveElement(doc, atu.Id, vetorMovimento);
                }
                
                doc.Regenerate();
                itensNaLinhaAtual++;
            }
            
            alturaTotalDoGrid += maxDimLinhaAtual; 
            return alturaTotalDoGrid;
        }

        // ==========================================================================================
        // MÉTODOS COMPARTILHADOS
        // ==========================================================================================
        private double CalcularEspaçamento(Document doc, View view, Parameter pDist, string valConfig)
        {
            double aeDistRaw = 0;
            bool found = false;

            if (!string.IsNullOrEmpty(valConfig) && double.TryParse(valConfig.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out double resConfig))
            {
                aeDistRaw = resConfig;
                found = true;
            }
            else if (pDist != null && pDist.HasValue)
            {
                try {
                    aeDistRaw = pDist.AsDouble();
                    found = true;
                } catch { }
                if (aeDistRaw > 0 && aeDistRaw < 0.5) return aeDistRaw * view.Scale;
            }

            if (!found || aeDistRaw <= 0) return 0;

            return (aeDistRaw / 304.8) * view.Scale;
        }

        private void CorrigirPosicaoTag(IndependentTag tag, XYZ alvo, Document doc)
        {
            doc.Regenerate();
            if (tag.TagHeadPosition.DistanceTo(alvo) > 0.0001)
            {
                try 
                { 
                    tag.TagHeadPosition = alvo; 
                }
                catch 
                { 
                    XYZ delta = alvo - tag.TagHeadPosition;
                    ElementTransformUtils.MoveElement(doc, tag.Id, delta); 
                }
            }
            doc.Regenerate();
        }

        private double GetVisualWidth(Element elem, View view)
        {
            if (elem == null || view == null) return 0;
            BoundingBoxXYZ bbox = null;
            try { bbox = elem.get_BoundingBox(view); } catch { }
            if (bbox == null || !bbox.Enabled) return 0;

            XYZ viewRight = view.RightDirection;

            XYZ[] corners = new XYZ[8];
            corners[0] = bbox.Min;
            corners[1] = new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Min.Z);
            corners[2] = new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Min.Z);
            corners[3] = new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Min.Z);
            corners[4] = new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Max.Z);
            corners[5] = new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Max.Z);
            corners[6] = bbox.Max;
            corners[7] = new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Max.Z);

            double minProj = double.MaxValue;
            double maxProj = double.MinValue;

            foreach (XYZ pt in corners)
            {
                double dot = pt.DotProduct(viewRight);
                minProj = Math.Min(minProj, dot);
                maxProj = Math.Max(maxProj, dot);
            }
            
            return maxProj - minProj;
        }

        private string NormalizeString(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            return raw.Replace("\"", "").Replace("'", "").Replace(" ", "").Replace("in", "").Replace("mm", "").Replace("ø", "").Replace("Ø", "").ToLowerInvariant();
        }

        private Dictionary<ElementId, List<CircuitoLog>> ExtrairELimparMemoria(Document doc, string path)
        {
            var dict = new Dictionary<ElementId, List<CircuitoLog>>();
            string[] linhas = File.ReadAllLines(path);
            List<string> linhasValidas = new List<string>();
            bool precisaSalvar = false;

            foreach (string linha in linhas)
            {
                if (string.IsNullOrWhiteSpace(linha)) continue;

                string[] parts = linha.Split('|');
                if (parts.Length >= 6)
                {
                    string p1 = parts[1].Replace("ID_Vista:", "").Trim();
                    string p2 = parts[2].Replace("ID_Conduto:", "").Trim();
                    string p3 = parts[3].Replace("ID_Tag:", "").Trim();
                    
                    string p4 = parts[4].Trim();
                    int openP = p4.IndexOf('(');
                    string numCirc = openP > 0 ? p4.Substring(0, openP).Replace("Circuito:", "").Trim() : "";
                    string tipoCirc = openP > 0 ? p4.Substring(openP + 1).Replace(")", "").Trim() : "";

                    string p5 = parts[5].Trim();
                    int fVal = ParseSubstringVal(p5, "F:");
                    int nVal = ParseSubstringVal(p5, "N:");
                    int tVal = ParseSubstringVal(p5, "T:");
                    int rVal = ParseSubstringVal(p5, "R:");
                    
                    string textoRet = "";
                    int retStart = p5.IndexOf("Ret:[");
                    if (retStart != -1) {
                        int retEnd = p5.IndexOf("]", retStart);
                        if (retEnd != -1) textoRet = p5.Substring(retStart + 5, retEnd - (retStart + 5));
                    }

                    string p6 = parts.Length >= 7 ? parts[6].Replace("Bitola:", "").Trim() : "0";

                    long.TryParse(p1, out long valViewId);
                    long.TryParse(p2, out long valCondId);

                    ElementId viewId = new ElementId(valViewId);
                    ElementId condId = new ElementId(valCondId);
                    string tagIdStr = p3;

                    Element conduto = doc.GetElement(condId);
                    if (conduto == null || !conduto.IsValidObject || conduto.Category == null) 
                    {
                        precisaSalvar = true; 
                        continue; 
                    }

                    if (!string.IsNullOrEmpty(tagIdStr) && tagIdStr != "N/A" && long.TryParse(tagIdStr, out long tId))
                    {
                        Element tag = doc.GetElement(new ElementId(tId));
                        if (tag == null || !tag.IsValidObject)
                        {
                            precisaSalvar = true; 
                            continue; 
                        }
                    }

                    // Se a linha registra o ID estável do circuito (ID_Circ), relê número/tipo VIVOS:
                    // cobre renumeração de circuitos feita após o lançamento, sem reimportar a memória.
                    int idcIdx = linha.IndexOf("ID_Circ:");
                    if (idcIdx != -1)
                    {
                        string idcRaw = linha.Substring(idcIdx + "ID_Circ:".Length).Split('|')[0].Trim();
                        if (long.TryParse(idcRaw, out long circIdVal))
                        {
                            Element circEl = doc.GetElement(new ElementId(circIdVal));
                            if (circEl != null && circEl.IsValidObject)
                            {
                                string numVivo = GetParamStringOrValue(circEl, BuiltInParameter.RBS_ELEC_CIRCUIT_NUMBER);
                                if (!string.IsNullOrEmpty(numVivo)) numCirc = numVivo;
                                string tipoVivo = GetParamStringOrValueCustom(circEl, "Tipo Circuito");
                                if (!string.IsNullOrEmpty(tipoVivo)) tipoCirc = tipoVivo;
                            }
                        }
                    }

                    linhasValidas.Add(linha);

                    if (!dict.ContainsKey(condId)) dict[condId] = new List<CircuitoLog>();

                    if (!dict[condId].Any(c => c.Numero == numCirc && c.ViewId == viewId))
                    {
                        double.TryParse(p6.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out double bitolaVal);

                        dict[condId].Add(new CircuitoLog
                        {
                            ViewId = viewId,
                            Numero = numCirc,
                            TipoCircuito = tipoCirc,
                            F = fVal,
                            N = nVal,
                            T = tVal,
                            R = rVal,
                            TextoRet = textoRet,
                            Bitola = bitolaVal
                        });
                    }
                }
                else if (!string.IsNullOrWhiteSpace(linha))
                {
                     linhasValidas.Add(linha);
                }
            }

            if (precisaSalvar)
            {
                try { File.WriteAllLines(path, linhasValidas); } catch { }
            }

            return dict;
        }

        private int ParseSubstringVal(string src, string key) {
            int idx = src.IndexOf(key);
            if (idx == -1) return 0;
            idx += key.Length;
            string numStr = "";
            while (idx < src.Length && char.IsDigit(src[idx])) {
                numStr += src[idx];
                idx++;
            }
            int.TryParse(numStr, out int res);
            return res;
        }

        private string FormatarParaOrdemAlfabetica(string texto)
        {
            if (string.IsNullOrEmpty(texto)) return "";
            string result = "";
            string currentNum = "";
            foreach (char c in texto) {
                if (char.IsDigit(c)) currentNum += c;
                else {
                    if (currentNum.Length > 0) { result += currentNum.PadLeft(5, '0'); currentNum = ""; }
                    result += c;
                }
            }
            if (currentNum.Length > 0) result += currentNum.PadLeft(5, '0');
            return result;
        }

        private void PreencherViaLog(FamilyInstance i, CircuitoLog circ)
        {
            SetParamRobusto(i, new[] { "Circuito" }, circ.Numero);
            SetParamRobusto(i, new[] { "ncaracteres" }, circ.Numero.Length);

            bool isTomIlu = circ.TipoCircuito.ToUpper().Contains("TOM") || circ.TipoCircuito.ToUpper().Contains("ILU");
            if (isTomIlu)
            {
                SetParamRobusto(i, new[] { "Zfase", "FASE", "Fases", "Fase", "F" }, circ.F);
                SetParamRobusto(i, new[] { "NEUTRO", "Neutro", "N" }, circ.N);
                SetParamRobusto(i, new[] { "TERRA", "Terra", "T" }, circ.T);
                SetParamRobusto(i, new[] { "Retorno", "RETORNO", "Retornos" }, circ.R);
                SetParamRobusto(i, new[] { "R" }, circ.TextoRet);
                SetParamRobusto(i, new[] { "Bitola", "mm²" }, circ.Bitola);
            }
        }

        private void SetParamRobusto(FamilyInstance inst, string[] nomes, object valor)
        {
            if (valor == null) return;
            foreach (string nome in nomes)
            {
                try
                {
                    Parameter p = inst.LookupParameter(nome);
                    if (p != null && !p.IsReadOnly)
                    {
                        if (p.StorageType == StorageType.String) p.Set(valor.ToString());
                        else if (p.StorageType == StorageType.Integer) p.Set(Convert.ToInt32(valor));
                        else if (p.StorageType == StorageType.Double) p.Set(Convert.ToDouble(valor));
                        return; 
                    }
                }
                catch { continue; }
            }
        }

        private double ObterBitola(Element c)
        {
            try
            {
                Parameter p = c.LookupParameter("Bitola") ?? c.LookupParameter("mm²");
                if (p == null || !p.HasValue) return 0.0;
                if (p.StorageType == StorageType.Double) return p.AsDouble();
                string s = KeepNumbersAndPunctuation(p.AsValueString() ?? p.AsString() ?? "0").Replace(",", ".");
                return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double res) ? res : 0.0;
            }
            catch { return 0.0; }
        }

        private string ObterBaseFiltro(string t)
        {
            string u = t.ToUpper();
            if (u.Contains("TOM")) return "TOM";
            if (u.Contains("ILU")) return "ILU";
            if (u.Contains("FOR")) return "FOR";
            if (u.Contains("DADOS")) return "DADOS";
            return "OUTROS";
        }

        private string ObterChaveFamilia(Dictionary<string, string> c, string t) {
            string u = t.ToUpper();
            if (u.Contains("TOM") || u.Contains("ILU")) return c.ContainsKey("TOMADAS (TOM)") ? c["TOMADAS (TOM)"] : "";
            if (u.Contains("FOR")) return c.ContainsKey("FORÇA (FOR)") ? c["FORÇA (FOR)"] : "";
            return c.ContainsKey("DADOS") ? c["DADOS"] : "";
        }

        private Dictionary<string, string> ParseJsonSimple(string j) {
            var d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(j)) return d;
            int index = 0;
            while (index < j.Length && (index = j.IndexOf("\"", index)) != -1) {
                int startKey = index + 1;
                int endKey = j.IndexOf("\"", startKey);
                if (endKey == -1) break;
                string key = j.Substring(startKey, endKey - startKey);
                index = j.IndexOf(":", endKey);
                if (index == -1) break;
                index = j.IndexOf("\"", index);
                if (index == -1) break;
                int startVal = index + 1;
                int endVal = j.IndexOf("\"", startVal);
                if (endVal == -1) break;
                string val = j.Substring(startVal, endVal - startVal);
                d[key] = val;
                index = endVal + 1;
            }
            return d;
        }

        // Parser estruturado do ZIDS (parâmetro de máquina).
        // Token rico: "cid:número=label~switchId,label~switchId". Legado: "cid".
        // Retorna cid -> lista de switchIds (ElementIds dos dispositivos de comando).
        private Dictionary<string, List<string>> ParseZids(string zids)
        {
            var map = new Dictionary<string, List<string>>();
            if (string.IsNullOrEmpty(zids)) return map;

            foreach (var bloco in zids.Split('|'))
            {
                int e = bloco.IndexOf(']');
                string corpo = e >= 0 ? bloco.Substring(e + 1) : bloco;

                foreach (var tokRaw in corpo.Split(';'))
                {
                    string tok = tokRaw.Trim();
                    if (tok.Length == 0) continue;

                    string cid = tok.Split(':')[0].Trim();
                    if (cid.Length == 0) continue;

                    if (!map.ContainsKey(cid)) map[cid] = new List<string>();

                    int eq = tok.IndexOf('=');
                    if (eq >= 0)
                    {
                        foreach (var cmd in tok.Substring(eq + 1).Split(','))
                        {
                            int tilde = cmd.IndexOf('~');
                            string swid = tilde >= 0 ? cmd.Substring(tilde + 1).Trim() : "";
                            if (swid.Length > 0 && !map[cid].Contains(swid)) map[cid].Add(swid);
                        }
                    }
                }
            }
            return map;
        }

        private Dictionary<string, string> ParseFiacao(string s)
        {
            var d = new Dictionary<string, string>();
            foreach (var b in ReplaceBrackets(s, ';').Split(';'))
            {
                if (b.Contains(":")) { var p = b.Split(new[] { ':' }, 2); d[p[0].Trim()] = p[1].Trim(); }
            }
            return d;
        }

        private (int, int, int, int, string) ExtrairDadosFio(string s)
        {
            int f = 0, r = 0;
            string textoRet = "";

            foreach (var part in s.Split(','))
            {
                string p = part.Trim();
                if (p.EndsWith("F"))
                {
                    var nums = ExtractNumbers(p);
                    f += nums.Count > 0 && int.TryParse(nums[0], out int fv) ? fv : 1;
                }
                else if (p.Contains("R("))
                {
                    int rIdx = p.IndexOf("R(");
                    var nums = ExtractNumbers(p.Substring(0, rIdx));
                    r += nums.Count > 0 && int.TryParse(nums[0], out int rv) ? rv : 1;
                    int closeP = p.IndexOf(')', rIdx);
                    if (closeP > rIdx + 1) textoRet = p.Substring(rIdx + 2, closeP - rIdx - 2);
                }
                else if (p.Contains("R"))
                {
                    var nums = ExtractNumbers(p);
                    r += nums.Count > 0 && int.TryParse(nums[0], out int rv2) ? rv2 : 1;
                }
            }

            return (f, s.Contains("N") ? 1 : 0, s.Contains("T") ? 1 : 0, r, textoRet);
        }
        
        private int ParseIntSafe(Dictionary<string, string> d, string k, int def) => d.TryGetValue(k, out string v) && int.TryParse(v, out int r) ? r : def;
    }

    public class CircuitoLog
    {
        public ElementId ViewId { get; set; } 
        public string Numero { get; set; }
        public string TipoCircuito { get; set; }
        public int F { get; set; }
        public int N { get; set; }
        public int T { get; set; }
        public int R { get; set; }
        public string TextoRet { get; set; }
        public double Bitola { get; set; }
    }

    public class RotaSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) => e.Category != null && (e.Category.Id.Value == (long)BuiltInCategory.OST_Conduit || e.Category.Id.Value == (long)BuiltInCategory.OST_CableTray);
        public bool AllowReference(Reference r, XYZ p) => true;
    }

    public class NaturalStringComparer : IComparer<string>
    {
        public int Compare(string x, string y)
        {
            if (x == y) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            var mx = SplitAlphaNum(x);
            var my = SplitAlphaNum(y);

            for (int i = 0; i < Math.Min(mx.Count, my.Count); i++)
            {
                string vx = mx[i];
                string vy = my[i];

                bool isNumX = int.TryParse(vx, out int numX);
                bool isNumY = int.TryParse(vy, out int numY);

                if (isNumX && isNumY)
                {
                    int cmp = numX.CompareTo(numY);
                    if (cmp != 0) return cmp;
                }
                else
                {
                    int cmp = string.Compare(vx, vy, StringComparison.OrdinalIgnoreCase);
                    if (cmp != 0) return cmp;
                }
            }
            return mx.Count.CompareTo(my.Count);
        }
        
        private List<string> SplitAlphaNum(string s) {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            string current = s[0].ToString();
            bool isDigit = char.IsDigit(s[0]);
            for (int i = 1; i < s.Length; i++) {
                if (char.IsDigit(s[i]) == isDigit) { current += s[i]; }
                else { list.Add(current); current = s[i].ToString(); isDigit = char.IsDigit(s[i]); }
            }
            list.Add(current);
            return list;
        }
    }

    // ============================================================================
    // CLASSES DA INTERFACE DE CONFIGURAÇÃO (WINFORMS)
    // ============================================================================
    public class TagRowItem {
        public string ColDesc { get; set; }
        public string ColChave { get; set; }
        public string ColFam { get; set; }
    }

    public class MemoriaRowItem {
        public bool ColManter { get; set; }
        public string ColVista { get; set; }
        public string ColConduto { get; set; }
        public int ColQtd { get; set; }
        public string ColKey { get; set; }
    }

    public class AegiaConfigForm
    {
        public WWindow MainForm { get; private set; }
        private Document doc;
        private SalvarConfigHandler salvarHandler;
        private ExternalEvent salvarEvent;

        private string safeProjectName;
        private string logFilePath;
        private string jsonConfigPath;
        private Dictionary<string, string> configCompleta = new Dictionary<string, string>();
        private Dictionary<string, List<string>> memoriaBrutaPorGrupo = new Dictionary<string, List<string>>();

        private WTabControl tabControl;
        private WTabItem tabTagsFiltros;
        private WTabItem tabDiametro;
        private WTabItem tabMemoria;

        private List<WCheckBox> chkFiltros = new List<WCheckBox>();
        private List<WTextBox> numFiltros = new List<WTextBox>();
        
        private List<TagRowItem> tagsCollection = new List<TagRowItem>();
        private List<MemoriaRowItem> memoriaCollection = new List<MemoriaRowItem>();
        private WDataGrid dgvTags;
        private WDataGrid dgvMemoria;
        private WComboBox cbDiametro;
        private WButton btnSalvar;
        private WButton btnAtualizar;

        private AtualizarTagsHandler atualizarHandler;
        private ExternalEvent atualizarEvent;

        public AegiaConfigForm(Document document, SalvarConfigHandler handler, ExternalEvent exEvent)
        {
            this.doc = document;
            this.salvarHandler = handler;
            this.salvarEvent = exEvent;

            // Evento externo próprio para o botão "Atualizar Tags" (UI modeless exige contexto Revit).
            this.atualizarHandler = new AtualizarTagsHandler();
            this.atualizarEvent = ExternalEvent.Create(this.atualizarHandler);

            MainForm = new WWindow();
            MainForm.Title = "Aegia | Configurador de Tags e Memória";
            MainForm.Width = 650; 
            MainForm.Height = 580; 
            MainForm.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            MainForm.Topmost = true; 
            MainForm.ResizeMode = System.Windows.ResizeMode.NoResize;
            
            DefinirCaminhosDiretorios();
            CarregarConfigJsonCompleto(); 
            
            InitializeComponents();
            
            PreencherInterfaceFiltrosTags();
            PreencherInterfaceDiametros();
            CarregarMemoriaAgrupada();

            // List<T> não notifica o DataGrid via INotifyCollectionChanged (System.ObjectModel
            // não é referenciado em pyRevit .NET 8/10). Atribuímos ItemsSource só depois do load.
            dgvTags.ItemsSource = tagsCollection;
            dgvMemoria.ItemsSource = memoriaCollection;
        }

        public void Show() { MainForm.Show(); }

        private void DefinirCaminhosDiretorios()
        {
            string rawProjectName = string.IsNullOrWhiteSpace(doc.ProjectInformation.Name) || doc.ProjectInformation.Name == "Project Name" 
                                    ? doc.Title : doc.ProjectInformation.Name;
            safeProjectName = string.Join("_", rawProjectName.Split(Path.GetInvalidFileNameChars()));

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string bimExtDir = Path.Combine(appData, "pyRevit", "Extensions", "BIM.extension", "lib");
            try { if (!Directory.Exists(bimExtDir)) Directory.CreateDirectory(bimExtDir); } catch { }

            logFilePath = Path.Combine(bimExtDir, $"aegialt_memoria_{safeProjectName}.txt");
            jsonConfigPath = Path.Combine(bimExtDir, $"aegialt {safeProjectName}.json");
        }

        private void CarregarConfigJsonCompleto()
        {
            string pathParaLer = jsonConfigPath;
            string oldPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AegiaLT.json");

            if (!File.Exists(pathParaLer) && File.Exists(oldPath)) pathParaLer = oldPath;

            if (File.Exists(pathParaLer))
            {
                try
                {
                    string json = File.ReadAllText(pathParaLer);
                    var parsed = ParseJsonSimple(json);
                    foreach (var kvp in parsed) configCompleta[kvp.Key] = kvp.Value;
                }
                catch { }
            }
        }
        
        private Dictionary<string, string> ParseJsonSimple(string j) {
            var d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(j)) return d;
            int index = 0;
            while (index < j.Length && (index = j.IndexOf("\"", index)) != -1) {
                int startKey = index + 1;
                int endKey = j.IndexOf("\"", startKey);
                if (endKey == -1) break;
                string key = j.Substring(startKey, endKey - startKey);
                index = j.IndexOf(":", endKey);
                if (index == -1) break;
                index = j.IndexOf("\"", index);
                if (index == -1) break;
                int startVal = index + 1;
                int endVal = j.IndexOf("\"", startVal);
                if (endVal == -1) break;
                string val = j.Substring(startVal, endVal - startVal);
                d[key] = val;
                index = endVal + 1;
            }
            return d;
        }

        private void InitializeComponents()
        {
            WGrid mainGrid = new WGrid();
            MainForm.Content = mainGrid;

            tabControl = new WTabControl() { Margin = new WThickness(10, 10, 10, 60) };

            // ABA 1: FILTROS E TAGS
            tabTagsFiltros = new WTabItem() { Header = "Filtros e Famílias" };
            WCanvas canvasTagsFiltros = new WCanvas() { Background = System.Windows.Media.Brushes.White };
            tabTagsFiltros.Content = canvasTagsFiltros;

            System.Windows.Controls.GroupBox gbFiltros = new System.Windows.Controls.GroupBox() { 
                Header = "Filtros de Disciplina Ativos & Limite de Quebra de Linha", 
                Width = 585, Height = 145 
            };
            WCanvas.SetLeft(gbFiltros, 10);
            WCanvas.SetTop(gbFiltros, 10);
            WCanvas innerGbFiltros = new WCanvas();
            gbFiltros.Content = innerGbFiltros;
            
            string[] codigosFiltro = { "TOM", "ILU", "FOR", "DADOS" };
            string[] labelsFiltro = { "Tomadas (TOM)", "Iluminação (ILU)", "Força (FOR)", "Dados (DADOS)" };

            for (int i = 0; i < codigosFiltro.Length; i++)
            {
                WCheckBox chk = new WCheckBox() { 
                    Content = labelsFiltro[i], Tag = codigosFiltro[i], 
                    Width = 150 
                };
                WCanvas.SetLeft(chk, 10); WCanvas.SetTop(chk, 10 + (i * 25));
                
                WLabel lblNum = new WLabel() { 
                    Content = "Máx por Linha:", 
                    Width = 90, Padding = new WThickness(0)
                };
                WCanvas.SetLeft(lblNum, 190); WCanvas.SetTop(lblNum, 10 + (i * 25));
                
                WTextBox num = new WTextBox() { 
                    Tag = "MAX_LINHA_" + codigosFiltro[i], 
                    Width = 60, Text = "0"
                };
                WCanvas.SetLeft(num, 285); WCanvas.SetTop(num, 10 + (i * 25));
                
                WLabel lblZero = new WLabel() { 
                    Content = "(0 = Infinito)", 
                    Width = 100, Padding = new WThickness(0)
                };
                WCanvas.SetLeft(lblZero, 350); WCanvas.SetTop(lblZero, 10 + (i * 25));

                innerGbFiltros.Children.Add(chk);
                innerGbFiltros.Children.Add(lblNum);
                innerGbFiltros.Children.Add(num);
                innerGbFiltros.Children.Add(lblZero);
                
                chkFiltros.Add(chk);
                numFiltros.Add(num);
            }
            canvasTagsFiltros.Children.Add(gbFiltros);

            System.Windows.Controls.GroupBox gbTags = new System.Windows.Controls.GroupBox() { 
                Header = "Mapeamento de Famílias (Tags)", 
                Width = 585, Height = 245 
            };
            WCanvas.SetLeft(gbTags, 10);
            WCanvas.SetTop(gbTags, 165);
            WCanvas innerGbTags = new WCanvas();
            gbTags.Content = innerGbTags;

            dgvTags = new WDataGrid() {
                Width = 565, Height = 210,
                AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
                HeadersVisibility = System.Windows.Controls.DataGridHeadersVisibility.Column,
                SelectionMode = System.Windows.Controls.DataGridSelectionMode.Single,
                SelectionUnit = System.Windows.Controls.DataGridSelectionUnit.FullRow,
                Background = System.Windows.Media.Brushes.WhiteSmoke
            };
            WCanvas.SetLeft(dgvTags, 0); WCanvas.SetTop(dgvTags, 5);

            dgvTags.Columns.Add(new WDataGridTextColumn() { Header = "Finalidade / Elemento", Binding = new System.Windows.Data.Binding("ColDesc"), IsReadOnly = true, Width = new System.Windows.Controls.DataGridLength(40, System.Windows.Controls.DataGridLengthUnitType.Star) });

            List<string> listaFamiliasRevit = new List<string>() { "" };
            Guid guidFiltro = new Guid("7c29e2a5-4a32-4a8b-863d-4d922633591c");

            var colSimbolos = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).WhereElementIsElementType();
            foreach (FamilySymbol sym in colSimbolos)
            {
                if (sym == null || !sym.IsValidObject || sym.Category == null) continue;
                long catId = sym.Category.Id.Value;
                
                bool catValida = catId == (long)BuiltInCategory.OST_ConduitTags || 
                                 catId == (long)BuiltInCategory.OST_CableTrayTags || 
                                 catId == (long)BuiltInCategory.OST_MultiCategoryTags || 
                                 catId == (long)BuiltInCategory.OST_GenericAnnotation;

                if (catValida)
                {
                    bool filtroTipoFam = false;
                    Parameter pFiltro = sym.get_Parameter(guidFiltro) ?? sym.LookupParameter("TIPOFAM");
                    if (pFiltro != null && pFiltro.HasValue)
                    {
                        string val = pFiltro.AsString() ?? "";
                        if (val.ToUpper().Contains("TAGS")) filtroTipoFam = true;
                    }

                    if (filtroTipoFam || catId != (long)BuiltInCategory.OST_GenericAnnotation)
                    {
                        listaFamiliasRevit.Add($"{sym.Family.Name} - {sym.Name}");
                    }
                }
            }
            listaFamiliasRevit = listaFamiliasRevit.Distinct().OrderBy(s => s).ToList();

            WDataGridComboBoxColumn cbCol = new WDataGridComboBoxColumn() { 
                Header = "Selecione o Símbolo", 
                SelectedValueBinding = new System.Windows.Data.Binding("ColFam"),
                Width = new System.Windows.Controls.DataGridLength(60, System.Windows.Controls.DataGridLengthUnitType.Star),
                ItemsSource = listaFamiliasRevit
            };
            dgvTags.Columns.Add(cbCol);

            innerGbTags.Children.Add(dgvTags);
            canvasTagsFiltros.Children.Add(gbTags);

            // ABA 2: DIÂMETRO PADRÃO
            tabDiametro = new WTabItem() { Header = "Diâmetro Padrão" };
            WCanvas canvasDiametro = new WCanvas() { Background = System.Windows.Media.Brushes.White };
            tabDiametro.Content = canvasDiametro;
            
            WLabel lblDiametroInfo = new WLabel() { 
                Content = "Selecione o diâmetro predominante. Eletrodutos com este diâmetro suprimirão a tag principal automaticamente.", 
                Width = 585, Height = 35 
            };
            WCanvas.SetLeft(lblDiametroInfo, 10); WCanvas.SetTop(lblDiametroInfo, 10);
            
            WLabel lblCbDiametro = new WLabel() { 
                Content = "Diâmetro Padrão Predominante:", 
                Width = 200, Height = 30, FontWeight = System.Windows.FontWeights.Bold 
            };
            WCanvas.SetLeft(lblCbDiametro, 10); WCanvas.SetTop(lblCbDiametro, 55);

            cbDiametro = new WComboBox() { 
                Width = 200, IsEditable = true 
            };
            WCanvas.SetLeft(cbDiametro, 210); WCanvas.SetTop(cbDiametro, 52);

            canvasDiametro.Children.Add(lblDiametroInfo);
            canvasDiametro.Children.Add(lblCbDiametro);
            canvasDiametro.Children.Add(cbDiametro);

            // ABA 3: MEMÓRIA AGRUPADA
            tabMemoria = new WTabItem() { Header = "Memória Agrupada" };
            WCanvas canvasMemoria = new WCanvas() { Background = System.Windows.Media.Brushes.White };
            tabMemoria.Content = canvasMemoria;

            WLabel lblMemoriaInfo = new WLabel() { 
                Content = "Desmarque um conjunto (Vista + Conduto) para excluir de uma vez todos os circuitos atrelados a ele no descarregamento:", 
                Width = 585, Height = 35, FontWeight = System.Windows.FontWeights.Bold 
            };
            WCanvas.SetLeft(lblMemoriaInfo, 10); WCanvas.SetTop(lblMemoriaInfo, 10);

            dgvMemoria = new WDataGrid() {
                Width = 585, Height = 360,
                AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false,
                HeadersVisibility = System.Windows.Controls.DataGridHeadersVisibility.Column,
                SelectionMode = System.Windows.Controls.DataGridSelectionMode.Single,
                SelectionUnit = System.Windows.Controls.DataGridSelectionUnit.FullRow,
                Background = System.Windows.Media.Brushes.WhiteSmoke
            };
            WCanvas.SetLeft(dgvMemoria, 10); WCanvas.SetTop(dgvMemoria, 50);

            dgvMemoria.Columns.Add(new WDataGridCheckBoxColumn() { Header = "Manter", Binding = new System.Windows.Data.Binding("ColManter"), Width = new System.Windows.Controls.DataGridLength(15, System.Windows.Controls.DataGridLengthUnitType.Star) });
            dgvMemoria.Columns.Add(new WDataGridTextColumn() { Header = "Vista Hospedeira", Binding = new System.Windows.Data.Binding("ColVista"), IsReadOnly = true, Width = new System.Windows.Controls.DataGridLength(40, System.Windows.Controls.DataGridLengthUnitType.Star) });
            dgvMemoria.Columns.Add(new WDataGridTextColumn() { Header = "ID do Conduto", Binding = new System.Windows.Data.Binding("ColConduto"), IsReadOnly = true, Width = new System.Windows.Controls.DataGridLength(25, System.Windows.Controls.DataGridLengthUnitType.Star) });
            dgvMemoria.Columns.Add(new WDataGridTextColumn() { Header = "Qtd. Circuitos", Binding = new System.Windows.Data.Binding("ColQtd"), IsReadOnly = true, Width = new System.Windows.Controls.DataGridLength(20, System.Windows.Controls.DataGridLengthUnitType.Star) });

            canvasMemoria.Children.Add(lblMemoriaInfo);
            canvasMemoria.Children.Add(dgvMemoria);

            btnSalvar = new WButton() {
                Content = "SALVAR CONFIGURAÇÕES",
                Width = 360, Height = 45,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(91, 204, 46)),
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = System.Windows.FontWeights.Bold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                Margin = new WThickness(0, 0, 10, 10)
            };
            btnSalvar.Click += BtnSalvar_Click;

            btnAtualizar = new WButton() {
                Content = "ATUALIZAR TAGS DO PROJETO",
                Width = 250, Height = 45,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(33, 118, 188)),
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = System.Windows.FontWeights.Bold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
                Margin = new WThickness(10, 0, 0, 10)
            };
            btnAtualizar.Click += BtnAtualizar_Click;

            tabControl.Items.Add(tabTagsFiltros);
            tabControl.Items.Add(tabDiametro);
            tabControl.Items.Add(tabMemoria);

            mainGrid.Children.Add(tabControl);
            mainGrid.Children.Add(btnSalvar);
            mainGrid.Children.Add(btnAtualizar);
        }

        private void PreencherInterfaceFiltrosTags()
        {
            string filtrosAtivos = configCompleta.ContainsKey("FILTROS_ATIVOS") ? configCompleta["FILTROS_ATIVOS"] : "TOM|ILU|FOR";
            List<string> listaFiltros = filtrosAtivos.Split('|').ToList();

            foreach (var chk in chkFiltros) chk.IsChecked = listaFiltros.Contains(chk.Tag.ToString());

            foreach (var num in numFiltros)
            {
                string key = num.Tag.ToString();
                if (configCompleta.ContainsKey(key) && int.TryParse(configCompleta[key], out int val)) num.Text = val.ToString();
            }

            void AdicionarTagLinha(string desc, string chaveJson)
            {
                string valorSalvo = configCompleta.ContainsKey(chaveJson) ? configCompleta[chaveJson] : "";
                tagsCollection.Add(new TagRowItem { ColDesc = desc, ColChave = chaveJson, ColFam = valorSalvo });
            }

            AdicionarTagLinha("CHAMADA EXTERNA", "CHAMADA_GEN");
            AdicionarTagLinha("CHAMADA EXTERNA VISTA", "CHAMADA_MULTI");
            AdicionarTagLinha("TAG ELETRODUTO", "TAG_ELETRODUTO");
            AdicionarTagLinha("TAG ELETROCALHA", "TAG_ELETROCALHA");
            AdicionarTagLinha("Circuitos de Tomadas", "TOMADAS (TOM)");
            AdicionarTagLinha("Circuitos de Iluminação", "ILUMINAÇÃO (ILU)");
            AdicionarTagLinha("Circuitos de Força", "FORÇA (FOR)");
            AdicionarTagLinha("Infraestrutura de Dados", "DADOS");
        }

        private void PreencherInterfaceDiametros()
        {
            HashSet<string> ds = new HashSet<string>();
            var conduits = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Conduit).WhereElementIsNotElementType();
            
            foreach (Element c in conduits)
            {
                try
                {
                    if (c == null || !c.IsValidObject || c.Category == null) continue;
                    Parameter p = c.get_Parameter(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                    if (p != null)
                    {
                        string v = p.AsValueString();
                        if (!string.IsNullOrEmpty(v)) ds.Add(v);
                    }
                }
                catch { }
            }

            var sortedDs = ds.ToList();
            sortedDs.Sort(new NaturalStringComparer());
            cbDiametro.ItemsSource = sortedDs;

            if (configCompleta.ContainsKey("DIAMETRO_PADRAO"))
            {
                string diamAtual = configCompleta["DIAMETRO_PADRAO"];
                cbDiametro.Text = diamAtual;
            }
        }

        private void CarregarMemoriaAgrupada()
        {
            if (!File.Exists(logFilePath)) return;

            try
            {
                string[] linhas = File.ReadAllLines(logFilePath);
                var grupos = new Dictionary<string, (string NomeVista, string IdConduto, int Qtd, List<string> Linhas)>();

                foreach (string linha in linhas)
                {
                    if (string.IsNullOrWhiteSpace(linha)) continue;

                    string[] parts = linha.Split('|');
                    if (parts.Length >= 6)
                    {
                        string idVistaTxt = parts[1].Replace("ID_Vista:", "").Trim();
                        string idCondutoTxt = parts[2].Replace("ID_Conduto:", "").Trim();
                        string key = idVistaTxt + "_" + idCondutoTxt;

                        if (!grupos.ContainsKey(key))
                        {
                            string nomeVista = idVistaTxt; 
                            if (long.TryParse(idVistaTxt, out long idVal))
                            {
                                try
                                {
                                    Element vElem = doc.GetElement(new ElementId(idVal));
                                    if (vElem is View v) nomeVista = v.Name;
                                }
                                catch { } 
                            }
                            grupos[key] = (nomeVista, idCondutoTxt, 0, new List<string>());
                        }

                        var groupData = grupos[key];
                        groupData.Qtd++;
                        groupData.Linhas.Add(linha);
                        grupos[key] = groupData;
                    }
                }

                foreach (var kvp in grupos)
                {
                    memoriaCollection.Add(new MemoriaRowItem { ColManter = true, ColVista = kvp.Value.NomeVista, ColConduto = kvp.Value.IdConduto, ColQtd = kvp.Value.Qtd, ColKey = kvp.Key });
                    memoriaBrutaPorGrupo[kvp.Key] = kvp.Value.Linhas;
                }
            }
            catch { }
        }

        private void BtnSalvar_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            salvarEvent.Raise();
        }

        private void BtnAtualizar_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            atualizarEvent.Raise();
        }

        public void ExecutarSalvarRevitContext(Document documentContext)
        {
            try 
            {
                configCompleta["PROJETO_ATIVO"] = safeProjectName;

                List<string> marcados = new List<string>();
                foreach (var chk in chkFiltros) if (chk.IsChecked == true) marcados.Add(chk.Tag.ToString());
                configCompleta["FILTROS_ATIVOS"] = string.Join("|", marcados);

                foreach (var num in numFiltros) configCompleta[num.Tag.ToString()] = num.Text;

                var colSimbolos = new FilteredElementCollector(documentContext).OfClass(typeof(FamilySymbol)).WhereElementIsElementType();
                var dictSimbolos = new Dictionary<string, FamilySymbol>();
                foreach (FamilySymbol s in colSimbolos)
                {
                    if (s == null || !s.IsValidObject) continue;
                    dictSimbolos[$"{s.Family.Name} - {s.Name}"] = s;
                }

                foreach (var row in tagsCollection)
                {
                    string key = row.ColChave;
                    string val = row.ColFam ?? "";
                    configCompleta[key] = val;

                    if (!string.IsNullOrEmpty(val) && dictSimbolos.TryGetValue(val, out FamilySymbol sym))
                    {
                        try
                        {
                            Parameter pDist = sym.LookupParameter("AEDIST");
                            if (pDist != null && pDist.HasValue)
                            {
                                configCompleta[key + "_AEDIST"] = pDist.AsDouble().ToString(CultureInfo.InvariantCulture);
                            }
                        }
                        catch { }
                    }
                }

                configCompleta["DIAMETRO_PADRAO"] = cbDiametro.Text?.Trim() ?? "";

                List<string> jsonLines = new List<string>();
                foreach (var kvp in configCompleta)
                {
                    string safeVal = (kvp.Value ?? "").Replace("\"", "'").Replace("\r", "").Replace("\n", "");
                    jsonLines.Add($"  \"{kvp.Key}\": \"{safeVal}\"");
                }
                string jsonOut = "{\n" + string.Join(",\n", jsonLines) + "\n}";
                File.WriteAllText(jsonConfigPath, jsonOut);

                List<string> linhasParaManter = new List<string>();
                foreach (var row in memoriaCollection)
                {
                    if (row.ColManter)
                    {
                        string groupKey = row.ColKey;
                        if (memoriaBrutaPorGrupo.ContainsKey(groupKey))
                            linhasParaManter.AddRange(memoriaBrutaPorGrupo[groupKey]);
                    }
                }

                if (linhasParaManter.Count > 0) File.WriteAllLines(logFilePath, linhasParaManter);
                else if (File.Exists(logFilePath)) File.Delete(logFilePath);

                Autodesk.Revit.UI.TaskDialog.Show("Aegia", "Configurações atualizadas com sucesso!");
                MainForm.Close();
            } 
            catch (Exception ex) 
            { 
                Autodesk.Revit.UI.TaskDialog.Show("Erro", "Erro crítico ao salvar as configurações: " + ex.Message); 
            }
        }
    }
}