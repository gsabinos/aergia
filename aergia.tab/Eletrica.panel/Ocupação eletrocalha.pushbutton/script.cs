using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using WWindow = System.Windows.Window;
using WButton = System.Windows.Controls.Button;
using WLabel = System.Windows.Controls.Label;
using WTextBox = System.Windows.Controls.TextBox;
using WComboBox = System.Windows.Controls.ComboBox;

namespace Aegia_Automations
{
    // =====================================================================================
    // 0. COMANDO PRINCIPAL (MASTER COMMAND) - CONTROLA O SHIFT + CLIQUE
    // =====================================================================================
    [Transaction(TransactionMode.Manual)]
    public class AegiaMasterCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            // Verifica se a tecla SHIFT está pressionada usando WPF
            bool isShiftPressed = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;

            if (isShiftPressed)
            {
                return RunConfig(commandData); // Roda a Tela de Configuração e Criação do JSON
            }
            else
            {
                return RunDrawingLoop(commandData); // Roda o Lançamento Interativo
            }
        }

        // =====================================================================================
        // MÓDULO A: CONFIGURAÇÃO (SHIFT + CLIQUE)
        // =====================================================================================
        private Result RunConfig(ExternalCommandData commandData)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;

            var itensDetalheDisponiveis = new List<ItemDetalheBIM>();
            var symbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_DetailComponents)
                .Cast<FamilySymbol>();

            foreach (var s in symbols)
            {
                Parameter p = s.LookupParameter("TIPOFAM") ?? s.LookupParameter("tipofam");
                if (p != null && p.HasValue && p.AsString() != null && p.AsString().ToLower().Contains("dimen"))
                {
                    itensDetalheDisponiveis.Add(new ItemDetalheBIM 
                    { 
                        RevitID = s.Id.ToString(), 
                        Nome = $"{s.FamilyName} - {s.Name}", 
                        TipoFam = p.AsString() 
                    });
                }
            }

            ProcessamentoHandler handler = new ProcessamentoHandler();
            ExternalEvent exEvent = ExternalEvent.Create(handler);

            MenuPrincipalForm menu = new MenuPrincipalForm(exEvent, handler, itensDetalheDisponiveis);
            menu.MainForm.Show(); 
            
            return Result.Succeeded;
        }

        // =====================================================================================
        // MÓDULO B: LANÇAMENTO INTERATIVO (CLIQUE NORMAL)
        // =====================================================================================
        private Result RunDrawingLoop(ExternalCommandData commandData)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                string nomeProjeto = doc.Title.Replace(".rvt", "");
                var dadosBim = LerBancoDeDadosJson(nomeProjeto);
                
                if (dadosBim == null || dadosBim.Circuitos.Count == 0) 
                    return Result.Cancelled;

                ObjectSnapTypes snaps = ObjectSnapTypes.Endpoints | ObjectSnapTypes.Midpoints | 
                                        ObjectSnapTypes.Intersections | ObjectSnapTypes.Nearest | 
                                        ObjectSnapTypes.Perpendicular | ObjectSnapTypes.Centers;

                while (true)
                {
                    Reference refAnotacao = uidoc.Selection.PickObject(ObjectType.Element, new AnotacaoGenericaFilter(), "Selecione a Anotação (ELID) para desenhar a eletrocalha (Pressione ESC para sair)...");
                    Element anotacao = doc.GetElement(refAnotacao);

                    Parameter pElid = anotacao.LookupParameter("ELID") ?? anotacao.LookupParameter("elid");
                    if (pElid == null || !pElid.HasValue || string.IsNullOrWhiteSpace(pElid.AsString()))
                    {
                        Autodesk.Revit.UI.TaskDialog.Show("Erro", "O parâmetro 'ELID' não foi encontrado ou está vazio na anotação selecionada.");
                        continue; 
                    }
                    string eletrocalhaId = pElid.AsString().Trim();

                    XYZ pontoBase = uidoc.Selection.PickPoint(snaps, "Clique no local de inserção (Snaps Ativados) ou pressione ESC para sair...");

                    ExecutarDesenho(doc, eletrocalhaId, pontoBase, dadosBim);
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Succeeded; // Usuário abortou com ESC (Loop finalizado com sucesso)
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Erro Fatal", $"Ocorreu um erro inesperado:\n{ex.Message}");
                return Result.Failed;
            }
        }

        // --- MÉTODOS INTERNOS DO LANÇAMENTO ---
        private void ExecutarDesenho(Document doc, string eletrocalhaId, XYZ pontoBase, BancoDadosJson dadosBim)
        {
            Element eletrocalha = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .FirstOrDefault(e => e.Id.ToString() == eletrocalhaId);

            if (eletrocalha == null)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Erro", $"Eletrocalha com ID '{eletrocalhaId}' não encontrada no modelo."); return;
            }

            Parameter pZids = eletrocalha.LookupParameter("ZIDS") ?? eletrocalha.LookupParameter("zids") ?? 
                              eletrocalha.LookupParameter("ZIDC") ?? eletrocalha.LookupParameter("zidc");
            
            if (pZids == null || !pZids.HasValue)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aviso", "O parâmetro de rotas (ZIDS ou ZIDC) não existe ou está vazio nesta eletrocalha."); return;
            }

            string zidsRaw = pZids.AsString();
            
            string zidsLimpo = "";
            bool insideBracket = false;
            foreach(char c in zidsRaw) {
                if (c == '[') insideBracket = true;
                else if (c == ']') insideBracket = false;
                else if (!insideBracket) zidsLimpo += c;
            }
            
            string[] arrayIdsLimpos = zidsLimpo.Split(new char[] { ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            // ZIDS pode trazer token rico "cid:número=label~swid,..."; a identidade é o cid antes de ':'.
            List<string> idsDosCircuitos = arrayIdsLimpos
                .Select(tok => tok.Split(':')[0].Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct().ToList();

            if (idsDosCircuitos.Count == 0) return;

            using (Transaction t = new Transaction(doc, "Desenhar Arranjo de Cabos"))
            {
                t.Start();

                FamilySymbol familiaDimen = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(f => f.Id.ToString() == dadosBim.IdFamiliaEscolhida);

                if (familiaDimen == null)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Erro", "A família de detalhe mapeada no JSON não foi encontrada.");
                    t.RollBack(); return;
                }

                if (!familiaDimen.IsActive) familiaDimen.Activate();

                Parameter pWidth = eletrocalha.get_Parameter(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                Parameter pHeight = eletrocalha.get_Parameter(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                
                double trayWidth = pWidth != null ? pWidth.AsDouble() : 300.0 / 304.8;
                double trayHeight = pHeight != null ? pHeight.AsDouble() : 100.0 / 304.8;

                try
                {
                    XYZ p1 = pontoBase;
                    XYZ p2 = pontoBase + new XYZ(trayWidth, 0, 0);
                    XYZ p3 = pontoBase + new XYZ(trayWidth, trayHeight, 0);
                    XYZ p4 = pontoBase + new XYZ(0, trayHeight, 0);

                    doc.Create.NewDetailCurve(doc.ActiveView, Line.CreateBound(p1, p2)); 
                    doc.Create.NewDetailCurve(doc.ActiveView, Line.CreateBound(p2, p3)); 
                    doc.Create.NewDetailCurve(doc.ActiveView, Line.CreateBound(p3, p4)); 
                    doc.Create.NewDetailCurve(doc.ActiveView, Line.CreateBound(p4, p1)); 
                }
                catch { }

                double currentX = 0; 
                double currentY = 0;
                double rowMaxHeight = 0;
                
                double gap = 0.5 / 304.8; 
                double gapEntreCircuitos = 1.0 / 304.8; 

                foreach (string idCabo in idsDosCircuitos)
                {
                    var ocorrencias = dadosBim.Circuitos.Where(c => c.RevitID == idCabo).ToList();
                    if (ocorrencias.Count == 0) continue;

                    List<CircuitoJson> cabosDoCircuito = new List<CircuitoJson>();
                    foreach (var ocorrencia in ocorrencias)
                    {
                        if (ocorrencia.DiametroEncontrado <= 0) continue;
                        int qtd = ExtrairNumero(ocorrencia.QtdCond);
                        for (int i = 0; i < qtd; i++) cabosDoCircuito.Add(ocorrencia);
                    }

                    if (cabosDoCircuito.Count == 0) continue;

                    cabosDoCircuito = cabosDoCircuito.OrderByDescending(c => c.DiametroEncontrado).ToList();

                    int totalCabos = cabosDoCircuito.Count;
                    int baseCount = totalCabos <= 2 ? totalCabos : (int)Math.Ceiling(totalCabos / 2.0);
                    
                    var baseCables = cabosDoCircuito.Take(baseCount).ToList();
                    var topCables = cabosDoCircuito.Skip(baseCount).ToList();

                    double baseWidth = baseCables.Sum(c => c.DiametroEncontrado / 304.8) + (Math.Max(0, baseCables.Count - 1) * gap);
                    double topWidth = topCables.Sum(c => c.DiametroEncontrado / 304.8) + (Math.Max(0, topCables.Count - 1) * gap);
                    double circuitTotalWidth = Math.Max(baseWidth, topWidth);

                    double maxBaseDiam = baseCables.Max(c => c.DiametroEncontrado / 304.8);
                    double maxTopDiam = topCables.Count > 0 ? topCables.Max(c => c.DiametroEncontrado / 304.8) : 0;
                    
                    double nesting = maxBaseDiam * 0.15; 
                    double alturaDoBloco = maxBaseDiam + (topCables.Count > 0 ? gap + maxTopDiam - nesting : 0);

                    int qtdFases = ExtrairNumero(ocorrencias.First().QtdFase);

                    for (int f = 0; f < qtdFases; f++)
                    {
                        if (currentX + circuitTotalWidth > trayWidth && currentX > 0)
                        {
                            currentX = 0;
                            currentY += rowMaxHeight + gapEntreCircuitos;
                            rowMaxHeight = 0;
                        }

                        double cxBase = currentX + (circuitTotalWidth - baseWidth) / 2.0; 
                        foreach (var cabo in baseCables)
                        {
                            double diamPes = cabo.DiametroEncontrado / 304.8;
                            double raioPes = diamPes / 2.0;
                            
                            double globalX = pontoBase.X + cxBase + raioPes;
                            double globalY = pontoBase.Y + currentY + raioPes;
                            XYZ pt = new XYZ(globalX, globalY, pontoBase.Z);
                            
                            DesenharEPreencher(doc, familiaDimen, pt, cabo, diamPes);
                            
                            cxBase += diamPes + gap;
                        }

                        if (topCables.Count > 0)
                        {
                            double cxTop = currentX + (circuitTotalWidth - topWidth) / 2.0; 
                            double yTop = currentY + maxBaseDiam + gap - nesting;

                            foreach (var cabo in topCables)
                            {
                                double diamPes = cabo.DiametroEncontrado / 304.8;
                                double raioPes = diamPes / 2.0;
                                
                                double globalX = pontoBase.X + cxTop + raioPes;
                                double globalY = pontoBase.Y + yTop + raioPes;
                                XYZ pt = new XYZ(globalX, globalY, pontoBase.Z);
                                
                                DesenharEPreencher(doc, familiaDimen, pt, cabo, diamPes);
                                
                                cxTop += diamPes + gap;
                            }
                        }

                        currentX += circuitTotalWidth + gapEntreCircuitos; 
                        rowMaxHeight = Math.Max(rowMaxHeight, alturaDoBloco);
                    }
                }
                t.Commit();
            }
        }

        private void DesenharEPreencher(Document doc, FamilySymbol familia, XYZ ponto, CircuitoJson caboDados, double diametroPes)
        {
            FamilyInstance inst = doc.Create.NewFamilyInstance(ponto, familia, doc.ActiveView);
            doc.Regenerate();

            PreencherParametroAgnostico(inst, new[] { "DIA", "dia", "Dia", "diametro", "Diametro" }, diametroPes, caboDados.DiametroEncontrado.ToString(CultureInfo.InvariantCulture));
            PreencherParametroAgnostico(inst, new[] { "CIRC", "circ", "Circ", "cabo", "Cabo", "circuito", "Circuito" }, 0, caboDados.CaboNome);
            PreencherParametroAgnostico(inst, new[] { "BITOLA", "bitola", "Bitola", "secao", "Secao" }, 0, caboDados.Bitola);
            PreencherParametroAgnostico(inst, new[] { "Func", "func", "FUNC", "funcao", "Funcao" }, 0, caboDados.Funcao);
            PreencherParametroAgnostico(inst, new[] { "Fab", "fab", "FAB", "fabricante", "Fabricante" }, 0, caboDados.Fabricante);
        }

        private int ExtrairNumero(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return 1; 
            string numStr = new string(texto.Where(char.IsDigit).ToArray());
            return string.IsNullOrEmpty(numStr) ? 1 : int.Parse(numStr);
        }

        private bool PreencherParametroAgnostico(FamilyInstance instancia, string[] nomesPossiveis, double valorNumero, string valorTexto)
        {
            foreach (var nome in nomesPossiveis)
            {
                Parameter p = instancia.LookupParameter(nome);
                if (p != null && !p.IsReadOnly)
                {
                    if (p.StorageType == StorageType.Double) { p.Set(valorNumero); return true; }
                    else if (p.StorageType == StorageType.String) { p.Set(valorTexto); return true; }
                }
            }
            return false;
        }

        private BancoDadosJson LerBancoDeDadosJson(string nomeProjeto)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string jsonPath = Path.Combine(appData, "Aegia_BIM", $"aegia.dimens.{nomeProjeto}.json");

            if (!File.Exists(jsonPath))
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aviso", $"O banco de dados não foi encontrado em:\n{jsonPath}");
                return null;
            }

            string json = File.ReadAllText(jsonPath);
            BancoDadosJson banco = new BancoDadosJson();

            banco.IdFamiliaEscolhida = ExtractJsonStringValue(json, "\"ItemDetalheSelecionado\"", "\"RevitID\"");

            int circuitosIndex = json.IndexOf("\"Circuitos\"");
            if (circuitosIndex > 0)
            {
                int arrayStart = json.IndexOf('[', circuitosIndex);
                int arrayEnd = json.LastIndexOf(']');
                if (arrayStart > 0 && arrayEnd > arrayStart)
                {
                    string arrayContent = json.Substring(arrayStart, arrayEnd - arrayStart);
                    string[] blocks = arrayContent.Split(new[] { "{" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach(string block in blocks)
                    {
                        if (!block.Contains("}")) continue;
                        string b = block.Substring(0, block.IndexOf('}'));
                        
                        string id = ExtractJsonValueSimple(b, "\"RevitID\"");
                        string cabo = ExtractJsonValueSimple(b, "\"Cabo\"");
                        string bitola = ExtractJsonValueSimple(b, "\"Bitola\"");
                        string diamStr = ExtractJsonValueSimple(b, "\"DiametroEncontrado\"", true);
                        string fabricante = ExtractJsonValueSimple(b, "\"Fabricante\"");
                        string funcao = ExtractJsonValueSimple(b, "\"Funcao\"");
                        string qtdFase = ExtractJsonValueSimple(b, "\"QtdFase\"");
                        string qtdCond = ExtractJsonValueSimple(b, "\"QtdCond\"");

                        if (!string.IsNullOrEmpty(id) && double.TryParse(diamStr, NumberStyles.Any, CultureInfo.InvariantCulture, out double diam))
                        {
                            banco.Circuitos.Add(new CircuitoJson 
                            { 
                                RevitID = id, CaboNome = cabo, Bitola = bitola, 
                                DiametroEncontrado = diam, Fabricante = fabricante, 
                                Funcao = funcao, QtdFase = qtdFase, QtdCond = qtdCond
                            });
                        }
                    }
                }
            }
            return banco;
        }

        private string ExtractJsonStringValue(string json, string parentKey, string targetKey)
        {
            int pIdx = json.IndexOf(parentKey);
            if (pIdx < 0) return "";
            int tIdx = json.IndexOf(targetKey, pIdx);
            if (tIdx < 0) return "";
            int colonIdx = json.IndexOf(':', tIdx);
            int firstQuote = json.IndexOf('"', colonIdx);
            int secondQuote = json.IndexOf('"', firstQuote + 1);
            if (firstQuote > 0 && secondQuote > firstQuote) return json.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
            return "";
        }

        private string ExtractJsonValueSimple(string jsonBlock, string key, bool isNumber = false)
        {
            int idx = jsonBlock.IndexOf(key);
            if (idx < 0) return "";
            int colonIdx = jsonBlock.IndexOf(':', idx);
            if (colonIdx < 0) return "";
            
            if (isNumber)
            {
                int endIdx = jsonBlock.IndexOf(',', colonIdx);
                if (endIdx < 0) endIdx = jsonBlock.Length;
                return jsonBlock.Substring(colonIdx + 1, endIdx - colonIdx - 1).Trim();
            }
            else
            {
                int firstQuote = jsonBlock.IndexOf('"', colonIdx);
                if (firstQuote < 0) return "";
                int secondQuote = jsonBlock.IndexOf('"', firstQuote + 1);
                if (secondQuote < 0) return "";
                return jsonBlock.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
            }
        }
    }

    // =====================================================================================
    // CLASSES DA INTERFACE DE CONFIGURAÇÃO (UI WPF)
    // =====================================================================================
    public class MenuPrincipalForm
    {
        public WWindow MainForm { get; private set; }
        private WTextBox _txtCaminhoCabos; 
        private WTextBox _txtCaminhoCatalogo;
        private ExternalEvent _exEvent;
        private ProcessamentoHandler _handler;
        private List<ItemDetalheBIM> _itensDetalhe;

        public MenuPrincipalForm(ExternalEvent exEvent, ProcessamentoHandler handler, List<ItemDetalheBIM> itensDetalhe)
        {
            MainForm = new WWindow();
            _exEvent = exEvent; 
            _handler = handler;
            _itensDetalhe = itensDetalhe;
            ConfigurarFormulario(); 
            ConstruirInterface();
        }

        private void ConfigurarFormulario()
        {
            MainForm.Title = "Configuração do Dimensionamento";
            MainForm.Width = 550; 
            MainForm.Height = 250;
            MainForm.Topmost = true; 
            MainForm.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            MainForm.ResizeMode = System.Windows.ResizeMode.NoResize;
        }

        private void ConstruirInterface()
        {
            System.Windows.Controls.Canvas canvas = new System.Windows.Controls.Canvas();

            WLabel lblCabos = new WLabel { Content = "1. Lista de Cabos do Projeto (.csv):", Width = 400 };
            System.Windows.Controls.Canvas.SetLeft(lblCabos, 20);
            System.Windows.Controls.Canvas.SetTop(lblCabos, 20);

            _txtCaminhoCabos = new WTextBox { Width = 380, IsReadOnly = true };
            System.Windows.Controls.Canvas.SetLeft(_txtCaminhoCabos, 20);
            System.Windows.Controls.Canvas.SetTop(_txtCaminhoCabos, 45);

            WButton btnCabos = new WButton { Content = "Procurar...", Width = 100 };
            System.Windows.Controls.Canvas.SetLeft(btnCabos, 410);
            System.Windows.Controls.Canvas.SetTop(btnCabos, 43);
            btnCabos.Click += (s, e) => SelecionarArquivo(_txtCaminhoCabos, "Lista de Cabos");

            WLabel lblCatalogo = new WLabel { Content = "2. Catálogo de Fabricantes (.csv):", Width = 400 };
            System.Windows.Controls.Canvas.SetLeft(lblCatalogo, 20);
            System.Windows.Controls.Canvas.SetTop(lblCatalogo, 80);

            _txtCaminhoCatalogo = new WTextBox { Width = 380, IsReadOnly = true };
            System.Windows.Controls.Canvas.SetLeft(_txtCaminhoCatalogo, 20);
            System.Windows.Controls.Canvas.SetTop(_txtCaminhoCatalogo, 105);

            WButton btnCatalogo = new WButton { Content = "Procurar...", Width = 100 };
            System.Windows.Controls.Canvas.SetLeft(btnCatalogo, 410);
            System.Windows.Controls.Canvas.SetTop(btnCatalogo, 103);
            btnCatalogo.Click += (s, e) => SelecionarArquivo(_txtCaminhoCatalogo, "Catálogo");

            WButton btnAvancar = new WButton
            {
                Content = "Avançar para Mapeamento", 
                Width = 490, 
                Height = 35,
                FontWeight = System.Windows.FontWeights.Bold
            };
            System.Windows.Controls.Canvas.SetLeft(btnAvancar, 20);
            System.Windows.Controls.Canvas.SetTop(btnAvancar, 155);
            btnAvancar.Click += BtnAvancar_Click;

            canvas.Children.Add(lblCabos); 
            canvas.Children.Add(_txtCaminhoCabos); 
            canvas.Children.Add(btnCabos);
            canvas.Children.Add(lblCatalogo); 
            canvas.Children.Add(_txtCaminhoCatalogo); 
            canvas.Children.Add(btnCatalogo);
            canvas.Children.Add(btnAvancar);

            MainForm.Content = canvas;
        }

        private void SelecionarArquivo(WTextBox txt, string titulo)
        {
            Microsoft.Win32.OpenFileDialog ofd = new Microsoft.Win32.OpenFileDialog { Filter = "CSV|*.csv", Title = titulo };
            if (ofd.ShowDialog() == true) txt.Text = ofd.FileName;
        }

        private void BtnAvancar_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_txtCaminhoCabos.Text) || string.IsNullOrWhiteSpace(_txtCaminhoCatalogo.Text))
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aviso", "Selecione os dois arquivos."); 
                return;
            }
            try
            {
                MainForm.Cursor = System.Windows.Input.Cursors.Wait;
                MapeamentoForm tela2 = new MapeamentoForm(_txtCaminhoCabos.Text, _txtCaminhoCatalogo.Text, _exEvent, _handler, _itensDetalhe);
                tela2.MainForm.Show();
                MainForm.Close(); 
            }
            catch (Exception ex) { Autodesk.Revit.UI.TaskDialog.Show("Erro", "Erro: " + ex.Message); }
        }
    }

    public class MapeamentoForm
    {
        public WWindow MainForm { get; private set; }
        private string _camCabos; private string _camCatalogo;
        private ExternalEvent _exEvent; private ProcessamentoHandler _handler;
        private List<ItemDetalheBIM> _itensDetalhe;
        private Dictionary<string, WComboBox> _combosMapeamento = new Dictionary<string, WComboBox>();
        private WComboBox _cmbItemDetalhe;

        public MapeamentoForm(string camCabos, string camCatalogo, ExternalEvent exEvent, ProcessamentoHandler handler, List<ItemDetalheBIM> itensDetalhe)
        {
            MainForm = new WWindow();
            _camCabos = camCabos; _camCatalogo = camCatalogo; _exEvent = exEvent; _handler = handler; _itensDetalhe = itensDetalhe;
            ConfigurarFormulario(); CarregarDadosInteligentes();
        }

        private void ConfigurarFormulario()
        {
            MainForm.Title = "Passo 2: Mapear Fabricantes Pertinentes";
            MainForm.Width = 600; 
            MainForm.Height = 550; 
            MainForm.Topmost = true; 
            MainForm.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen; 
        }

        private void CarregarDadosInteligentes()
        {
            var todosFabricantes = new HashSet<string>();
            var fabricantesPorCat = new Dictionary<string, HashSet<string>>(); 

            string[] linhasCat = File.ReadAllLines(_camCatalogo);
            char sepCat = linhasCat[0].Contains(";") ? ';' : ',';
            
            for (int i = 1; i < linhasCat.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(linhasCat[i])) continue;
                string[] col = MotorDimensionamento.SepararCsvRobusto(linhasCat[i], sepCat);
                
                string fab = MotorDimensionamento.ObterValorSeguro(col, "A").Trim();
                string catColB_Tipo = MotorDimensionamento.ObterValorSeguro(col, "B"); 
                string catColC_Classe = MotorDimensionamento.ObterValorSeguro(col, "C"); 
                
                if (!string.IsNullOrEmpty(fab))
                {
                    todosFabricantes.Add(fab);
                    string chaveCat = $"{MotorDimensionamento.Limpar(catColC_Classe)}_{MotorDimensionamento.Limpar(catColB_Tipo)}";
                    if (!fabricantesPorCat.ContainsKey(chaveCat)) fabricantesPorCat[chaveCat] = new HashSet<string>();
                    fabricantesPorCat[chaveCat].Add(fab);
                }
            }

            var combinacoesProjeto = new Dictionary<string, List<string>>();
            string[] linhasCabos = File.ReadAllLines(_camCabos);
            char sepCab = linhasCabos[0].Contains(";") ? ';' : ',';
            
            string memoriaClasse = ""; string memoriaTipo = "";

            for (int i = 1; i < linhasCabos.Length; i++) 
            {
                if (string.IsNullOrWhiteSpace(linhasCabos[i])) continue;
                string[] col = MotorDimensionamento.SepararCsvRobusto(linhasCabos[i], sepCab);
                
                string caboColC = MotorDimensionamento.ObterValorSeguro(col, "C");
                if (caboColC == "CABO Nº" || caboColC.Contains("QUADRO GERAL")) continue;

                string colE_ClasseProj = MotorDimensionamento.ObterValorSeguro(col, "E"); 
                string colZ_TipoProj = MotorDimensionamento.ObterValorSeguro(col, "Z"); 

                if (!string.IsNullOrWhiteSpace(colE_ClasseProj)) memoriaClasse = colE_ClasseProj; else colE_ClasseProj = memoriaClasse;
                if (!string.IsNullOrWhiteSpace(colZ_TipoProj)) memoriaTipo = colZ_TipoProj; else colZ_TipoProj = memoriaTipo;

                if (string.IsNullOrWhiteSpace(colE_ClasseProj)) continue;

                string exibicao = $"{colE_ClasseProj} | {colZ_TipoProj}";
                string chaveBusca = $"{MotorDimensionamento.Limpar(colE_ClasseProj)}_{MotorDimensionamento.Limpar(colZ_TipoProj)}";
                
                if (!combinacoesProjeto.ContainsKey(exibicao))
                {
                    List<string> fabsPertinentes;
                    if (fabricantesPorCat.ContainsKey(chaveBusca))
                    {
                        fabsPertinentes = fabricantesPorCat[chaveBusca].ToList();
                        fabsPertinentes.Sort(); 
                    }
                    else
                    {
                        exibicao += " [Aviso]"; 
                        fabsPertinentes = todosFabricantes.ToList();
                        fabsPertinentes.Sort();
                    }
                    combinacoesProjeto.Add(exibicao, fabsPertinentes);
                }
            }
            ConstruirInterface(combinacoesProjeto);
        }

        private void ConstruirInterface(Dictionary<string, List<string>> combinacoes)
        {
            System.Windows.Controls.DockPanel dock = new System.Windows.Controls.DockPanel();

            System.Windows.Controls.StackPanel pnlTop = new System.Windows.Controls.StackPanel { Height = 70, Margin = new System.Windows.Thickness(10) };
            System.Windows.Controls.DockPanel.SetDock(pnlTop, System.Windows.Controls.Dock.Top);

            WLabel lblFam = new WLabel { Content = "Selecione a Família de Detalhe para Vínculo (TIPOFAM=dimen):", FontWeight = System.Windows.FontWeights.Bold };
            
            _cmbItemDetalhe = new WComboBox { Width = 550, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            if (_itensDetalhe.Count == 0)
            {
                _cmbItemDetalhe.Items.Add("Nenhuma família encontrada no projeto com TIPOFAM = dimen.");
            }
            else
            {
                foreach (var i in _itensDetalhe) _cmbItemDetalhe.Items.Add(i.Nome);
            }
            _cmbItemDetalhe.SelectedIndex = 0;
            pnlTop.Children.Add(lblFam); 
            pnlTop.Children.Add(_cmbItemDetalhe);

            System.Windows.Controls.StackPanel painel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(10) };
            
            WLabel lblInfo = new WLabel { Content = "Selecione o fabricante compatível (já filtrado pelo catálogo):", FontWeight = System.Windows.FontWeights.Bold };
            painel.Children.Add(lblInfo);

            foreach (var kvp in combinacoes)
            {
                string comboNome = kvp.Key; 
                List<string> fabricantesParaEsteCabo = new List<string>(kvp.Value); 
                fabricantesParaEsteCabo.Insert(0, "Ignorar / Não Dimensionar");

                System.Windows.Controls.StackPanel linha = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new System.Windows.Thickness(0, 5, 0, 5) };
                WLabel lblItem = new WLabel { Content = comboNome, Width = 250 };

                WComboBox cmbFab = new WComboBox { Width = 250 };
                foreach (var f in fabricantesParaEsteCabo) cmbFab.Items.Add(f);
                cmbFab.SelectedIndex = 0; 
                
                linha.Children.Add(lblItem); 
                linha.Children.Add(cmbFab); 
                painel.Children.Add(linha);
                _combosMapeamento.Add(comboNome.Replace(" [Aviso]", ""), cmbFab); 
            }

            System.Windows.Controls.ScrollViewer scroll = new System.Windows.Controls.ScrollViewer { Content = painel, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };

            System.Windows.Controls.StackPanel pnlBaixo = new System.Windows.Controls.StackPanel { Height = 60, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, Margin = new System.Windows.Thickness(10) };
            System.Windows.Controls.DockPanel.SetDock(pnlBaixo, System.Windows.Controls.Dock.Bottom);

            WButton btnExecutar = new WButton
            {
                Content = "PROCESSAR NO REVIT", 
                Width = 200, 
                Height = 40
            };
            btnExecutar.Click += BtnExecutar_Click; 
            pnlBaixo.Children.Add(btnExecutar);

            dock.Children.Add(pnlTop); 
            dock.Children.Add(pnlBaixo);
            dock.Children.Add(scroll);

            MainForm.Content = dock;
        }

        private void BtnExecutar_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var regrasUsuario = new Dictionary<string, string>();
            foreach (var item in _combosMapeamento)
                if (item.Value.SelectedIndex > 0) regrasUsuario.Add(item.Key, item.Value.SelectedItem.ToString());

            ItemDetalheBIM famSelecionada = null;
            if (_itensDetalhe.Count > 0 && _cmbItemDetalhe.SelectedIndex >= 0)
            {
                famSelecionada = _itensDetalhe[_cmbItemDetalhe.SelectedIndex];
            }

            _handler.ConfigurarTarefa(_camCabos, _camCatalogo, regrasUsuario, famSelecionada);
            _exEvent.Raise(); 
            MainForm.Close();
        }
    }

    public class ProcessamentoHandler : IExternalEventHandler
    {
        private string _camCabos; private string _camCatalogo; private Dictionary<string, string> _regrasMapeamento;
        private ItemDetalheBIM _familiaSelecionada;

        public void ConfigurarTarefa(string cabos, string catalogo, Dictionary<string, string> regras, ItemDetalheBIM familiaSelecionada)
        {
            _camCabos = cabos; _camCatalogo = catalogo; _regrasMapeamento = regras; _familiaSelecionada = familiaSelecionada;
        }

        public void Execute(UIApplication app)
        {
            try 
            {
                Document doc = app.ActiveUIDocument.Document;
                MotorDimensionamento motor = new MotorDimensionamento();
                
                var resultados = motor.ProcessarPlanilhas(_camCabos, _camCatalogo, _regrasMapeamento);
                if (resultados.Count == 0) { Autodesk.Revit.UI.TaskDialog.Show("Aviso", "Nenhum cabo validado para processamento."); return; }

                BuscarIdsNoRevit(doc, resultados);
                ExportarParaJsonAppdata(doc, resultados, _familiaSelecionada);

                RelatorioCruzamentoForm relatorio = new RelatorioCruzamentoForm(resultados);
                relatorio.MainForm.Show();
            }
            catch (Exception ex) { Autodesk.Revit.UI.TaskDialog.Show("Erro Fatal", ex.Message); }
        }

        private void BuscarIdsNoRevit(Document doc, List<ResultadoCruzamento> resultados)
        {
            List<BuiltInCategory> categoriasBusca = new List<BuiltInCategory> { BuiltInCategory.OST_ElectricalCircuit, BuiltInCategory.OST_Conduit, BuiltInCategory.OST_ElectricalFixtures };
            ElementMulticategoryFilter filtroCat = new ElementMulticategoryFilter(categoriasBusca);
            var elementosRevit = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(filtroCat).ToList();

            foreach (var res in resultados)
            {
                if (string.IsNullOrWhiteSpace(res.NumeroCaboCsv)) continue;
                foreach (var el in elementosRevit)
                {
                    string mark = el.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? "";
                    string nome = el.Name ?? "";
                    if (mark.Contains(res.NumeroCaboCsv) || nome.Contains(res.NumeroCaboCsv))
                    {
                        res.ElementIdRevit = el.Id.ToString(); break;
                    }
                }
            }
        }

        private void ExportarParaJsonAppdata(Document doc, List<ResultadoCruzamento> resultados, ItemDetalheBIM familiaEscolhida)
        {
            var modelados = resultados.Where(r => r.ElementIdRevit != "Ainda não modelado").ToList();
            if (modelados.Count == 0) return;

            string nomeProj = string.IsNullOrWhiteSpace(doc.Title) ? "Projeto" : doc.Title.Replace(".rvt", "");
            
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string pastaAegia = Path.Combine(appData, "Aegia_BIM");
            if (!Directory.Exists(pastaAegia)) Directory.CreateDirectory(pastaAegia);
            
            string filePath = Path.Combine(pastaAegia, $"aegia.dimens.{nomeProj}.json");

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine($"  \"Projeto\": \"{EscapeJson(nomeProj)}\",");
            
            if (familiaEscolhida != null)
            {
                sb.AppendLine("  \"ItemDetalheSelecionado\": {");
                sb.AppendLine($"    \"RevitID\": \"{familiaEscolhida.RevitID}\",");
                sb.AppendLine($"    \"Nome\": \"{EscapeJson(familiaEscolhida.Nome)}\",");
                sb.AppendLine($"    \"TipoFam\": \"{EscapeJson(familiaEscolhida.TipoFam)}\"");
                sb.AppendLine("  },");
            }
            
            sb.AppendLine("  \"Circuitos\": [");
            for (int i = 0; i < modelados.Count; i++)
            {
                var r = modelados[i];
                sb.AppendLine("    {");
                sb.AppendLine($"      \"RevitID\": \"{r.ElementIdRevit}\",");
                sb.AppendLine($"      \"Cabo\": \"{EscapeJson(r.NumeroCaboCsv)}\",");
                sb.AppendLine($"      \"Fabricante\": \"{EscapeJson(r.FabricanteUsado)}\",");
                sb.AppendLine($"      \"QtdFase\": \"{EscapeJson(r.QtdeFase)}\",");
                sb.AppendLine($"      \"QtdCond\": \"{EscapeJson(r.QtdeCond)}\",");
                sb.AppendLine($"      \"Bitola\": \"{EscapeJson(r.Bitola)}\",");
                sb.AppendLine($"      \"Funcao\": \"{EscapeJson(r.Funcoes)}\",");
                sb.AppendLine($"      \"DiametroEncontrado\": {r.Diametro.ToString(System.Globalization.CultureInfo.InvariantCulture)},");
                sb.AppendLine($"      \"AreaCaboUnitaria\": {r.AreaUnitaria.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                sb.Append("    }");
                if (i < modelados.Count - 1) sb.AppendLine(","); else sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            Autodesk.Revit.UI.TaskDialog.Show("Sucesso", $"O banco de dados foi salvo silenciosamente em:\n\n{filePath}");
        }

        private string EscapeJson(string str) => str == null ? "" : str.Replace("\\", "\\\\").Replace("\"", "\\\"");
        
        public string GetName() => "ProcessamentoDimensionamento";
    }

    public class RelatorioCruzamentoForm
    {
        public WWindow MainForm { get; private set; }
        public RelatorioCruzamentoForm(List<ResultadoCruzamento> resultados)
        {
            MainForm = new WWindow();
            MainForm.Title = "Relatório de Dimensionamento e Vínculo BIM";
            MainForm.Width = 1200; 
            MainForm.Height = 550; 
            MainForm.Topmost = true; 
            
            System.Windows.Controls.DataGrid dgv = new System.Windows.Controls.DataGrid { IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = System.Windows.Controls.DataGridSelectionMode.Extended };
            
            dgv.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header = "Revit ElementID", Binding = new System.Windows.Data.Binding("ElementIdRevit") });
            dgv.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header = "CABO Nº", Binding = new System.Windows.Data.Binding("NumeroCaboCsv") });
            dgv.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header = "Função (Col M)", Binding = new System.Windows.Data.Binding("Funcoes") });
            dgv.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header = "Ø Encontrado (mm)", Binding = new System.Windows.Data.Binding("DiametroDisplay") });
            dgv.Columns.Add(new System.Windows.Controls.DataGridTextColumn { Header = "Área Unit. (mm²)", Binding = new System.Windows.Data.Binding("AreaUnitariaDisplay") });

            var list = new List<RelatorioItem>();
            foreach (var r in resultados)
            {
                list.Add(new RelatorioItem
                {
                    ElementIdRevit = r.ElementIdRevit,
                    NumeroCaboCsv = r.NumeroCaboCsv,
                    Funcoes = r.Funcoes,
                    DiametroDisplay = r.Diametro > 0 ? r.Diametro.ToString("F2") : "-",
                    AreaUnitariaDisplay = r.AreaUnitaria > 0 ? r.AreaUnitaria.ToString("F2") : "-"
                });
            }
            dgv.ItemsSource = list;

            MainForm.Content = dgv;
        }
    }

    public class RelatorioItem
    {
        public string ElementIdRevit { get; set; }
        public string NumeroCaboCsv { get; set; }
        public string Funcoes { get; set; }
        public string DiametroDisplay { get; set; }
        public string AreaUnitariaDisplay { get; set; }
    }

    public class ItemDetalheBIM
    {
        public string RevitID { get; set; }
        public string Nome { get; set; }
        public string TipoFam { get; set; }
    }

    public class ResultadoCruzamento
    {
        public string NumeroCaboCsv { get; set; }  
        public string ElementIdRevit { get; set; } 
        public string ClasseIsolacao { get; set; }
        public string FabricanteUsado { get; set; }
        public string QtdeFase { get; set; }
        public string QtdeCond { get; set; }
        public string Bitola { get; set; }
        public string Funcoes { get; set; }
        public double Diametro { get; set; }
        public double AreaUnitaria { get; set; }
    }

    public class MotorDimensionamento
    {
        public static int Col(string letra) { int s = 0; foreach (char c in letra.ToUpper()) s = s * 26 + (c - 'A' + 1); return s - 1; }
        public static string ObterValorSeguro(string[] dados, string letra) => Col(letra) < dados.Length ? dados[Col(letra)].Trim() : "";
        public static string Limpar(string val) => string.IsNullOrWhiteSpace(val) ? "" : val.Replace("\"", "").Replace(",", ".").Replace(" ", "").ToUpper();

        public List<ResultadoCruzamento> ProcessarPlanilhas(string camCabos, string camCatalogo, Dictionary<string, string> regrasMapeamento)
        {
            var resultados = new List<ResultadoCruzamento>();
            var catalogo = new Dictionary<string, double>();
            
            string[] linhasCat = File.ReadAllLines(camCatalogo);
            char sepCat = linhasCat[0].Contains(";") ? ';' : ',';
            for (int i = 1; i < linhasCat.Length; i++)
            {
                string[] c = SepararCsvRobusto(linhasCat[i], sepCat);
                string chave = $"{Limpar(ObterValorSeguro(c,"A"))}_{Limpar(ObterValorSeguro(c,"C"))}_{Limpar(ObterValorSeguro(c,"B"))}_{Limpar(ObterValorSeguro(c,"D"))}";
                double.TryParse(ObterValorSeguro(c, "F").Replace(".", ","), out double val);
                if (!catalogo.ContainsKey(chave)) catalogo.Add(chave, val);
            }

            string[] linhasCab = File.ReadAllLines(camCabos);
            char sepCab = linhasCab[0].Contains(";") ? ';' : ',';
            
            string memoriaCaboId = ""; string memoriaClasse = ""; string memoriaTipo = "";
            string memoriaQtdFase = ""; string memoriaQtdCond = ""; string memoriaFuncoes = "";

            for (int i = 1; i < linhasCab.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(linhasCab[i])) continue;
                string[] c = SepararCsvRobusto(linhasCab[i], sepCab);
                
                string caboColC = ObterValorSeguro(c, "C");
                if (caboColC == "CABO Nº" || caboColC.Contains("QUADRO GERAL")) continue;

                string colE_Classe = ObterValorSeguro(c, "E");
                string colZ_Tipo = ObterValorSeguro(c, "Z");
                string colK_Bitola = ObterValorSeguro(c, "K");
                string colI_QtdFase = ObterValorSeguro(c, "I"); 
                string colJ_QtdCond = ObterValorSeguro(c, "J");
                string colM_Funcoes = ObterValorSeguro(c, "M");

                if (!string.IsNullOrWhiteSpace(caboColC)) memoriaCaboId = caboColC; else caboColC = memoriaCaboId;
                if (!string.IsNullOrWhiteSpace(colE_Classe)) memoriaClasse = colE_Classe; else colE_Classe = memoriaClasse;
                if (!string.IsNullOrWhiteSpace(colZ_Tipo)) memoriaTipo = colZ_Tipo; else colZ_Tipo = memoriaTipo;
                if (!string.IsNullOrWhiteSpace(colI_QtdFase)) memoriaQtdFase = colI_QtdFase; else colI_QtdFase = memoriaQtdFase;
                if (!string.IsNullOrWhiteSpace(colJ_QtdCond)) memoriaQtdCond = colJ_QtdCond; else colJ_QtdCond = memoriaQtdCond;
                if (!string.IsNullOrWhiteSpace(colM_Funcoes)) memoriaFuncoes = colM_Funcoes; else colM_Funcoes = memoriaFuncoes;

                if (string.IsNullOrWhiteSpace(colK_Bitola) || string.IsNullOrWhiteSpace(caboColC)) continue;

                string combTextual = $"{colE_Classe} | {colZ_Tipo}";
                string fabricanteDefinido = regrasMapeamento.ContainsKey(combTextual) ? regrasMapeamento[combTextual] : "";

                double diametroEncontrado = 0;
                if (!string.IsNullOrEmpty(fabricanteDefinido))
                {
                    string chaveBusca = $"{Limpar(fabricanteDefinido)}_{Limpar(colE_Classe)}_{Limpar(colZ_Tipo)}_{Limpar(colK_Bitola)}";
                    if (catalogo.ContainsKey(chaveBusca)) diametroEncontrado = catalogo[chaveBusca];
                }

                double areaUnit = diametroEncontrado > 0 ? Math.PI * Math.Pow(diametroEncontrado / 2.0, 2) : 0;

                resultados.Add(new ResultadoCruzamento
                {
                    NumeroCaboCsv = caboColC,
                    ElementIdRevit = "Ainda não modelado", 
                    ClasseIsolacao = colE_Classe,
                    FabricanteUsado = string.IsNullOrEmpty(fabricanteDefinido) ? "Ignorado" : fabricanteDefinido,
                    QtdeFase = colI_QtdFase,
                    QtdeCond = colJ_QtdCond,
                    Bitola = colK_Bitola,
                    Funcoes = colM_Funcoes,
                    Diametro = diametroEncontrado,
                    AreaUnitaria = areaUnit
                });
            }
            return resultados;
        }

        public static string[] SepararCsvRobusto(string linha, char separador)
        {
            List<string> result = new List<string>();
            bool inQuotes = false;
            string current = "";
            foreach (char c in linha)
            {
                if (c == '\"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == separador && !inQuotes)
                {
                    result.Add(current);
                    current = "";
                }
                else
                {
                    current += c;
                }
            }
            result.Add(current);
            return result.ToArray();
        }
    }

    // =====================================================================================
    // CLASSES DE APOIO DO RELEVIT (FILTROS E DADOS)
    // =====================================================================================
    public class AnotacaoGenericaFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            if (elem.Category != null && elem.Category.Id.Equals(new ElementId((long)BuiltInCategory.OST_GenericAnnotation)))
                return true;
            return false;
        }
        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    public class BancoDadosJson
    {
        public string IdFamiliaEscolhida { get; set; } = "";
        public List<CircuitoJson> Circuitos { get; set; } = new List<CircuitoJson>();
    }

    public class CircuitoJson
    {
        public string RevitID { get; set; }
        public string CaboNome { get; set; }
        public string Bitola { get; set; }
        public double DiametroEncontrado { get; set; } 
        public string Fabricante { get; set; }
        public string Funcao { get; set; }
        public string QtdFase { get; set; }
        public string QtdCond { get; set; }
    }
}