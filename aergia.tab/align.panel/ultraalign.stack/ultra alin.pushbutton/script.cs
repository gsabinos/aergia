using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

// Alias para evitar ambiguidade (Sem 'using System.Windows' globais)
using RView = Autodesk.Revit.DB.View;
using WWindow = System.Windows.Window;
using WCanvas = System.Windows.Controls.Canvas;
using WComboBox = System.Windows.Controls.ComboBox;
using WTextBox = System.Windows.Controls.TextBox;
using WCheckBox = System.Windows.Controls.CheckBox;
using WLabel = System.Windows.Controls.Label;
using WButton = System.Windows.Controls.Button;
using WGroupBox = System.Windows.Controls.GroupBox;
using WBrush = System.Windows.Media.Brush;
using WBrushes = System.Windows.Media.Brushes;
using WFontWeights = System.Windows.FontWeights;

namespace Aegia_Tools
{
    public enum GridDir { Up, Down, Left, Right }

    [Transaction(TransactionMode.Manual)]
    public class SuperAlignSmartCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            RView activeView = doc.ActiveView;

            // 1. Escudo de Memória (Filtro base)
            List<Element> selecionados = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Where(el => el != null && el.IsValidObject && el.get_BoundingBox(activeView) != null)
                .ToList();

            if (selecionados.Count < 2)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Smart Grid", "Selecione ao menos 2 elementos válidos.");
                return Result.Cancelled;
            }

            // 2. Interceptador de Teclado (Shift + Click) - Atualizado para WPF ModifierKeys
            bool isShiftPressed = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);
            
            GridMemory memoria = CarregarMemoria();

            // 3. Roteador de Ação
            if (!isShiftPressed && memoria.Valido)
            {
                // PRE-FLIGHT CHECK: Valida se o parâmetro existe nos elementos, incluindo a regra VIP de Textos
                if (memoria.UsarOrdenacao)
                {
                    bool parametroExiste = selecionados.Any(el => 
                        (memoria.ParametroOrdenacao == "<Conteúdo do Texto>" && el is TextNote) || 
                        el.LookupParameter(memoria.ParametroOrdenacao) != null
                    );

                    if (!parametroExiste)
                    {
                        Autodesk.Revit.UI.TaskDialog.Show("Aviso de Memória", 
                            $"Os elementos selecionados não possuem o parâmetro '{memoria.ParametroOrdenacao}' salvo na última execução.\n\n" +
                            "Por favor, segure SHIFT e CLIQUE no botão para abrir a interface e reconfigurar.");
                        return Result.Cancelled;
                    }
                }

                // MODO ONE-CLICK EXECUTE (Sem interface)
                using (Transaction t = new Transaction(doc, "Smart Grid (Execute)"))
                {
                    t.Start();
                    try { ExecutarMotorGrid(doc, activeView, selecionados, memoria); t.Commit(); }
                    catch (Exception ex) { t.RollBack(); Autodesk.Revit.UI.TaskDialog.Show("Erro Crítico", ex.Message); }
                }
            }
            else
            {
                // MODO CONFIGURAÇÃO (Shift pressionado ou primeira vez)
                List<string> paramsComuns = GetCommonParameters(selecionados);

                SmartGridForm form = new SmartGridForm(paramsComuns);
                if (form.ShowDialog() != true) return Result.Cancelled;

                GridMemory novaConfig = new GridMemory {
                    UsarOrdenacao = form.UsarOrdenacao,
                    ParametroOrdenacao = form.ParametroOrdenacao,
                    UsarSeparador = form.UsarSeparador,
                    ParametroSeparador = form.ParametroSeparador,
                    ElementosPorLinha = form.ElementosPorLinha,
                    Direcao = form.Direcao,
                    Valido = true
                };

                using (Transaction t = new Transaction(doc, "Smart Grid (Config)"))
                {
                    t.Start();
                    try { ExecutarMotorGrid(doc, activeView, selecionados, novaConfig); t.Commit(); }
                    catch (Exception ex) { t.RollBack(); Autodesk.Revit.UI.TaskDialog.Show("Erro Crítico", ex.Message); }
                }
            }

            return Result.Succeeded;
        }

        // --- MÉTODOS AUXILIARES ---
        private List<string> GetCommonParameters(List<Element> elementos)
        {
            var firstParams = elementos.First().Parameters.Cast<Parameter>()
                .Select(p => p.Definition.Name).Distinct().ToList();

            foreach (var el in elementos.Skip(1))
            {
                var currentParams = el.Parameters.Cast<Parameter>().Select(p => p.Definition.Name);
                firstParams = firstParams.Intersect(currentParams).ToList();
            }
            
            List<string> listaFinal = firstParams.OrderBy(s => s).ToList();

            // TRATAMENTO VIP PARA TEXTOS
            if (elementos.Any(el => el is TextNote))
            {
                listaFinal.Insert(0, "<Conteúdo do Texto>");
            }

            return listaFinal;
        }

        private string ObterValorParametro(Element el, string paramNome)
        {
            // LEITURA VIP DE TEXTOS
            if (paramNome == "<Conteúdo do Texto>" && el is TextNote notaDeTexto)
            {
                return notaDeTexto.Text; 
            }

            Parameter p = el.LookupParameter(paramNome);
            if (p == null) return "";
            string val = p.AsString();
            if (string.IsNullOrEmpty(val)) val = p.AsValueString();
            if (string.IsNullOrEmpty(val)) val = p.AsDouble().ToString();
            return val ?? "";
        }

        private string ExtrairStringJson(string json, string chave)
        {
            string marcador = "\"" + chave + "\": \"";
            int start = json.IndexOf(marcador);
            if (start == -1) return null;
            start += marcador.Length;
            int end = json.IndexOf("\"", start);
            if (end == -1) return null;
            return json.Substring(start, end - start);
        }

        private int ExtrairIntJson(string json, string chave)
        {
            string marcador = "\"" + chave + "\": ";
            int start = json.IndexOf(marcador);
            if (start == -1) return -1;
            start += marcador.Length;
            int end = json.IndexOf(",", start);
            if (end == -1) end = json.IndexOf("}", start);
            if (end == -1) return -1;
            string val = json.Substring(start, end - start).Trim();
            if (int.TryParse(val, out int res)) return res;
            return -1;
        }

        private GridMemory CarregarMemoria()
        {
            GridMemory mem = new GridMemory { Valido = false };
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aegia", "GridMemory.json");
            try {
                if (File.Exists(path)) {
                    string json = File.ReadAllText(path);
                    mem.UsarOrdenacao = json.Contains("\"UseSort\": true");
                    mem.UsarSeparador = json.Contains("\"UseSep\": true");

                    string sSort = ExtrairStringJson(json, "SortP");
                    if (!string.IsNullOrEmpty(sSort)) mem.ParametroOrdenacao = sSort;

                    string sSep = ExtrairStringJson(json, "SepP");
                    if (!string.IsNullOrEmpty(sSep)) mem.ParametroSeparador = sSep;

                    int nWrap = ExtrairIntJson(json, "N");
                    if (nWrap >= 0) mem.ElementosPorLinha = nWrap;

                    int nDir = ExtrairIntJson(json, "Dir");
                    if (nDir >= 0) mem.Direcao = (GridDir)nDir;
                    else mem.Direcao = GridDir.Right;

                    mem.Valido = true;
                }
            } catch { }
            return mem;
        }

        // --- O MOTOR MATEMÁTICO UNIVERSAL ---
        private void ExecutarMotorGrid(Document doc, RView view, List<Element> elementos, GridMemory configs)
        {
            Dictionary<ElementId, BoundingBoxXYZ> caixas = new Dictionary<ElementId, BoundingBoxXYZ>();

            using (SubTransaction sub = new SubTransaction(doc))
            {
                sub.Start();
                bool mudouLeader = false;
                foreach (var el in elementos)
                {
                    if (el is IndependentTag tag && tag.HasLeader) { tag.HasLeader = false; mudouLeader = true; }
                    else if (el is SpatialElementTag spatTag && spatTag.HasLeader) { spatTag.HasLeader = false; mudouLeader = true; }
                    else if (el is TextNote textNote && textNote.LeaderCount > 0) { textNote.RemoveLeaders(); mudouLeader = true; }
                }
                
                if (mudouLeader) doc.Regenerate();

                foreach (var el in elementos)
                {
                    caixas[el.Id] = el.get_BoundingBox(view);
                }

                sub.RollBack(); // Restaura as leaders com suas posições originais perfeitamente
            }

            var elementosValidos = elementos.Where(el => caixas.ContainsKey(el.Id) && caixas[el.Id] != null).ToList();
            if (elementosValidos.Count == 0) return;

            double minX = elementosValidos.Min(el => caixas[el.Id].Min.X);
            double maxX = elementosValidos.Max(el => caixas[el.Id].Max.X);
            double minY = elementosValidos.Min(el => caixas[el.Id].Min.Y);
            double maxY = elementosValidos.Max(el => caixas[el.Id].Max.Y);

            if (configs.UsarOrdenacao) {
                var ordenador = new AlphanumComparatorFast();
                elementosValidos = elementosValidos.OrderBy(el => ObterValorParametro(el, configs.ParametroOrdenacao), ordenador).ToList();
            } else {
                elementosValidos = (configs.Direcao == GridDir.Right || configs.Direcao == GridDir.Left) 
                    ? elementosValidos.OrderBy(el => caixas[el.Id].Min.X).ToList() 
                    : elementosValidos.OrderBy(el => caixas[el.Id].Min.Y).ToList();
            }

            if (configs.Direcao == GridDir.Left || configs.Direcao == GridDir.Up) elementosValidos.Reverse();

            double margem = 2.0 / 304.8;
            double cursorPrincipal = 0, cursorSecundario = 0, maxDimLinhaAtual = 0;

            if (configs.Direcao == GridDir.Right) { cursorPrincipal = minX; cursorSecundario = maxY; }
            else if (configs.Direcao == GridDir.Left) { cursorPrincipal = maxX; cursorSecundario = maxY; }
            else if (configs.Direcao == GridDir.Down) { cursorPrincipal = maxY; cursorSecundario = minX; }
            else if (configs.Direcao == GridDir.Up) { cursorPrincipal = minY; cursorSecundario = minX; }

            int itensNaLinhaAtual = 0;
            string valorSeparadorAnterior = null;

            for (int i = 0; i < elementosValidos.Count; i++)
            {
                Element atu = doc.GetElement(elementosValidos[i].Id);
                if (atu.Pinned) continue;

                BoundingBoxXYZ box = caixas[atu.Id];
                double width = box.Max.X - box.Min.X;
                double height = box.Max.Y - box.Min.Y;

                bool quebraPorSeparador = false;
                if (configs.UsarSeparador) {
                    string valorAtual = ObterValorParametro(atu, configs.ParametroSeparador);
                    if (i > 0 && valorAtual != valorSeparadorAnterior) quebraPorSeparador = true;
                    valorSeparadorAnterior = valorAtual;
                }

                if (quebraPorSeparador || (configs.ElementosPorLinha > 0 && itensNaLinhaAtual >= configs.ElementosPorLinha)) {
                    if (configs.Direcao == GridDir.Right || configs.Direcao == GridDir.Left) {
                        cursorPrincipal = (configs.Direcao == GridDir.Right) ? minX : maxX;
                        cursorSecundario -= (maxDimLinhaAtual + margem);
                    } else {
                        cursorPrincipal = (configs.Direcao == GridDir.Up) ? minY : maxY;
                        cursorSecundario += (maxDimLinhaAtual + margem);
                    }
                    maxDimLinhaAtual = 0;
                    itensNaLinhaAtual = 0;
                }

                double dx = 0, dy = 0;
                if (configs.Direcao == GridDir.Right) {
                    dx = cursorPrincipal - box.Min.X; dy = cursorSecundario - box.Max.Y;
                    cursorPrincipal += width + margem; maxDimLinhaAtual = Math.Max(maxDimLinhaAtual, height);
                } else if (configs.Direcao == GridDir.Left) {
                    dx = cursorPrincipal - box.Max.X; dy = cursorSecundario - box.Max.Y;
                    cursorPrincipal -= (width + margem); maxDimLinhaAtual = Math.Max(maxDimLinhaAtual, height);
                } else if (configs.Direcao == GridDir.Down) {
                    dy = cursorPrincipal - box.Max.Y; dx = cursorSecundario - box.Min.X;
                    cursorPrincipal -= (height + margem); maxDimLinhaAtual = Math.Max(maxDimLinhaAtual, width);
                } else if (configs.Direcao == GridDir.Up) {
                    dy = cursorPrincipal - box.Min.Y; dx = cursorSecundario - box.Min.X;
                    cursorPrincipal += height + margem; maxDimLinhaAtual = Math.Max(maxDimLinhaAtual, width);
                }

                ElementTransformUtils.MoveElement(doc, atu.Id, new XYZ(dx, dy, 0));
                itensNaLinhaAtual++;
            }
        }
    }

    // --- ESTRUTURA E INTERFACE ---
    public struct GridMemory
    {
        public bool Valido;
        public bool UsarOrdenacao;
        public string ParametroOrdenacao;
        public bool UsarSeparador;
        public string ParametroSeparador;
        public int ElementosPorLinha;
        public GridDir Direcao;
    }

    public class SmartGridForm : WWindow
    {
        public bool UsarOrdenacao => chkSort.IsChecked == true;
        public string ParametroOrdenacao => cbSort.SelectedItem as string;
        public bool UsarSeparador => chkSep.IsChecked == true;
        public string ParametroSeparador => cbSep.SelectedItem as string;
        public int ElementosPorLinha => int.TryParse(numWrap.Text, out int v) ? v : 0;
        public GridDir Direcao { get; private set; }

        private WCheckBox chkSort, chkSep;
        private WComboBox cbSort, cbSep;
        private WTextBox numWrap;
        private string configPath;

        public SmartGridForm(List<string> parametros)
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Directory.CreateDirectory(Path.Combine(appData, "Aegia"));
            configPath = Path.Combine(appData, "Aegia", "GridMemory.json");

            this.Title = "Aegia | Smart Engine";
            this.Width = 320;
            this.Height = 420;
            this.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            this.Topmost = true;
            this.ResizeMode = System.Windows.ResizeMode.NoResize;

            WCanvas canvas = new WCanvas();
            this.Content = canvas;

            chkSort = new WCheckBox { Content = "Ativar Ordenação por Parâmetro", IsChecked = true };
            WCanvas.SetLeft(chkSort, 15); WCanvas.SetTop(chkSort, 15);
            canvas.Children.Add(chkSort);

            cbSort = new WComboBox { Width = 260 };
            foreach(var p in parametros) cbSort.Items.Add(p);
            WCanvas.SetLeft(cbSort, 15); WCanvas.SetTop(cbSort, 40);
            canvas.Children.Add(cbSort);

            chkSort.Checked += (s, e) => cbSort.IsEnabled = true;
            chkSort.Unchecked += (s, e) => cbSort.IsEnabled = false;

            chkSep = new WCheckBox { Content = "Ativar Quebra por Separador", IsChecked = false };
            WCanvas.SetLeft(chkSep, 15); WCanvas.SetTop(chkSep, 80);
            canvas.Children.Add(chkSep);

            cbSep = new WComboBox { Width = 260, IsEnabled = false };
            foreach(var p in parametros) cbSep.Items.Add(p);
            WCanvas.SetLeft(cbSep, 15); WCanvas.SetTop(cbSep, 105);
            canvas.Children.Add(cbSep);

            chkSep.Checked += (s, e) => cbSep.IsEnabled = true;
            chkSep.Unchecked += (s, e) => cbSep.IsEnabled = false;

            WLabel lblWrap = new WLabel { Content = "Nº Limite na Linha/Coluna (0 = Infinito):" };
            WCanvas.SetLeft(lblWrap, 10); WCanvas.SetTop(lblWrap, 145);
            canvas.Children.Add(lblWrap);

            numWrap = new WTextBox { Width = 260, Text = "0" };
            WCanvas.SetLeft(numWrap, 15); WCanvas.SetTop(numWrap, 175);
            canvas.Children.Add(numWrap);

            WGroupBox gb = new WGroupBox { Header = "Defina o Ponto de Âncora (Executar)", Width = 260, Height = 140 };
            WCanvas.SetLeft(gb, 15); WCanvas.SetTop(gb, 220);
            
            WCanvas gbCanvas = new WCanvas();
            gb.Content = gbCanvas;
            canvas.Children.Add(gb);

            gbCanvas.Children.Add(CreateArrowBtn("▲", 100, 10, GridDir.Up));
            gbCanvas.Children.Add(CreateArrowBtn("▼", 100, 60, GridDir.Down));
            gbCanvas.Children.Add(CreateArrowBtn("◄", 50, 35, GridDir.Left));
            gbCanvas.Children.Add(CreateArrowBtn("►", 150, 35, GridDir.Right));

            LoadSettings(); 
        }

        private WButton CreateArrowBtn(string texto, int x, int y, GridDir d) {
            WButton b = new WButton { Content = texto, Width = 45, Height = 40, FontWeight = WFontWeights.Bold, FontSize = 16 };
            WCanvas.SetLeft(b, x); WCanvas.SetTop(b, y);
            b.Click += (s, e) => {
                Direcao = d;
                SaveSettings();
                this.DialogResult = true;
                this.Close();
            };
            return b;
        }

        private void SaveSettings() {
            try {
                string json = $"{{\"UseSort\": {(chkSort.IsChecked == true ? "true" : "false")}, \"SortP\": \"{cbSort.Text}\", \"UseSep\": {(chkSep.IsChecked == true ? "true" : "false")}, \"SepP\": \"{cbSep.Text}\", \"N\": {numWrap.Text}, \"Dir\": {(int)Direcao}}}";
                File.WriteAllText(configPath, json);
            } catch { }
        }

        private void LoadSettings() {
            if (cbSort.Items.Count > 0) { cbSort.SelectedIndex = 0; cbSep.SelectedIndex = 0; }
            try {
                if (File.Exists(configPath)) {
                    string json = File.ReadAllText(configPath);
                    WBrush memColor = WBrushes.LightGreen;

                    if (json.Contains("\"UseSort\": true")) { chkSort.IsChecked = true; chkSort.Background = memColor; } else { chkSort.IsChecked = false; }
                    
                    string sSort = ExtrairStringJson(json, "SortP");
                    if (!string.IsNullOrEmpty(sSort) && cbSort.Items.Contains(sSort)) { cbSort.SelectedItem = sSort; cbSort.Background = memColor; }

                    if (json.Contains("\"UseSep\": true")) { chkSep.IsChecked = true; chkSep.Background = memColor; }
                    
                    string sSep = ExtrairStringJson(json, "SepP");
                    if (!string.IsNullOrEmpty(sSep) && cbSep.Items.Contains(sSep)) { cbSep.SelectedItem = sSep; cbSep.Background = memColor; }

                    int nWrap = ExtrairIntJson(json, "N");
                    if (nWrap >= 0) { numWrap.Text = nWrap.ToString(); if (nWrap > 0) numWrap.Background = memColor; }
                }
            } catch { }
        }

        private string ExtrairStringJson(string json, string chave)
        {
            string marcador = "\"" + chave + "\": \"";
            int start = json.IndexOf(marcador);
            if (start == -1) return null;
            start += marcador.Length;
            int end = json.IndexOf("\"", start);
            if (end == -1) return null;
            return json.Substring(start, end - start);
        }

        private int ExtrairIntJson(string json, string chave)
        {
            string marcador = "\"" + chave + "\": ";
            int start = json.IndexOf(marcador);
            if (start == -1) return -1;
            start += marcador.Length;
            int end = json.IndexOf(",", start);
            if (end == -1) end = json.IndexOf("}", start);
            if (end == -1) return -1;
            string val = json.Substring(start, end - start).Trim();
            if (int.TryParse(val, out int res)) return res;
            return -1;
        }
    }

    public class AlphanumComparatorFast : IComparer<string>
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string x, string y);
        public int Compare(string x, string y) => StrCmpLogicalW(x ?? "", y ?? "");
    }
}