using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

// Resolvendo ambiguidades com Aliases para WPF
using WWindow = System.Windows.Window;
using WButton = System.Windows.Controls.Button;
using WLabel = System.Windows.Controls.Label;
using WComboBox = System.Windows.Controls.ComboBox;
using WTabControl = System.Windows.Controls.TabControl;
using WTabItem = System.Windows.Controls.TabItem;
using WCheckBox = System.Windows.Controls.CheckBox;
using WGrid = System.Windows.Controls.Grid;
using WStackPanel = System.Windows.Controls.StackPanel;
using WScrollViewer = System.Windows.Controls.ScrollViewer;
using WDataGrid = System.Windows.Controls.DataGrid;
using WDataGridTextColumn = System.Windows.Controls.DataGridTextColumn;
using WDataGridCheckBoxColumn = System.Windows.Controls.DataGridCheckBoxColumn;
using WThickness = System.Windows.Thickness;
using WBrushes = System.Windows.Media.Brushes;
using WColor = System.Windows.Media.Color;
using WTextBox = System.Windows.Controls.TextBox;
using WHorizontalAlignment = System.Windows.HorizontalAlignment;
using WVerticalAlignment = System.Windows.VerticalAlignment;
using WTextAlignment = System.Windows.TextAlignment;

namespace Aegia_Configuracao
{
    [Transaction(TransactionMode.ReadOnly)]
    public class ConfigCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;

            Guid guidFiltro = new Guid("7c29e2a5-4a32-4a8b-863d-4d922633591c");
            
            // Separação das listas para comportar as duas categorias
            List<string> familiasGen = new List<string> { "" }; 
            List<string> familiasMulti = new List<string> { "" }; 
            Dictionary<string, TagData> parametrosExtraidos = new Dictionary<string, TagData>();
            
            var collector = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol));
            foreach (FamilySymbol sym in collector)
            {
                try
                {
                    if (!sym.IsValidObject || sym.Category == null) continue;
                    
                    long catId = sym.Category.Id.Value;
                    
                    // Expansão do filtro para capturar Generic Annotations E Multi-Category Tags
                    if (catId == (long)BuiltInCategory.OST_GenericAnnotation || catId == (long)BuiltInCategory.OST_MultiCategoryTags)
                    {
                        Parameter pFiltro = sym.get_Parameter(guidFiltro) ?? sym.LookupParameter("TIPOFAM");

                        if (pFiltro != null && pFiltro.HasValue && pFiltro.StorageType == StorageType.String)
                        {
                            string valorFiltro = pFiltro.AsString();
                            if (!string.IsNullOrEmpty(valorFiltro) && valorFiltro.ToLower().Contains("tags"))
                            {
                                string nomeCompleto = $"{sym.Family.Name} - {sym.Name}";
                                
                                // Distribuição lógica
                                if (catId == (long)BuiltInCategory.OST_GenericAnnotation) familiasGen.Add(nomeCompleto);
                                else if (catId == (long)BuiltInCategory.OST_MultiCategoryTags) familiasMulti.Add(nomeCompleto);

                                TagData data = new TagData();
                                Parameter pForm = sym.LookupParameter("Zform");
                                Parameter pLarg = sym.LookupParameter("ZlargNT");
                                Parameter pAlt = sym.LookupParameter("Zalt");
                                Parameter pZaf = sym.LookupParameter("Zaf");

                                if (pForm != null && pForm.HasValue) data.Zform = pForm.AsString();
                                if (pLarg != null && pLarg.HasValue) data.ZlargNT = pLarg.AsDouble() * 304.8;
                                if (pAlt != null && pAlt.HasValue) data.Zalt = pAlt.AsDouble() * 304.8;
                                if (pZaf != null && pZaf.HasValue) data.Zaf = pZaf.AsDouble() * 304.8;

                                parametrosExtraidos[nomeCompleto] = data;
                            }
                        }
                    }
                }
                catch { } 
            }
            
            familiasGen = familiasGen.Distinct().OrderBy(x => x).ToList();
            familiasMulti = familiasMulti.Distinct().OrderBy(x => x).ToList();

            if (familiasGen.Count <= 1 && familiasMulti.Count <= 1)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aviso", "Nenhuma tag válida encontrada. Verifique o parâmetro TIPOFAM.");
                return Result.Cancelled;
            }

            HashSet<string> tiposCircuito = new HashSet<string>();
            var circCollector = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ElectricalCircuit).WhereElementIsNotElementType();
            foreach (Element circ in circCollector)
            {
                try {
                    Parameter p = circ.LookupParameter("Tipo Circuito");
                    if (p != null && p.HasValue) tiposCircuito.Add(p.AsString());
                } catch { }
            }

            ConfigForm form = new ConfigForm(doc, familiasGen, familiasMulti, tiposCircuito.ToList(), parametrosExtraidos);
            form.MainForm.ShowDialog(); 
            
            return Result.Succeeded;
        }
    }

    public class TagData
    {
        public string Zform = "10";
        public double ZlargNT = 0.0; public double Zalt = 0.0; public double Zaf = 0.0;
    }

    public class FiltroRow
    {
        public string TipoCircuito { get; set; }
        public WCheckBox ChkAtivo { get; set; }
        public WTextBox TxtMaxLinha { get; set; }
    }

    public class WorksetItem
    {
        public string Workset { get; set; }
        public bool Dados { get; set; }
        public bool Tomadas { get; set; }
        public bool Iluminacao { get; set; }
        public bool Forca { get; set; }
    }

    public class ConfigForm
    {
        public WWindow MainForm { get; private set; }
        private WComboBox cbEletrica, cbFor, cbComunicacao, cbChamadaGen, cbChamadaMulti;
        private WStackPanel pnlFiltros;
        private WButton btnSalvar;
        private WTabControl tabControl;
        private WDataGrid dgvWorksets;
        private Dictionary<string, string> configSalva = new Dictionary<string, string>();
        private Dictionary<string, TagData> parametrosExtraidos;
        private List<FiltroRow> linhasFiltro = new List<FiltroRow>();
        private Document doc;
        private System.Collections.ObjectModel.ObservableCollection<WorksetItem> worksetItems = new System.Collections.ObjectModel.ObservableCollection<WorksetItem>();

        public ConfigForm(Document document, List<string> familiasGen, List<string> familiasMulti, List<string> tiposCircuito, Dictionary<string, TagData> paramExtraidos)
        {
            MainForm = new WWindow();
            this.doc = document;
            this.parametrosExtraidos = paramExtraidos;
            MainForm.Title = "Configuração de TAGS e Roteamento";
            
            MainForm.Width = 550; MainForm.Height = 620; 
            MainForm.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            MainForm.Topmost = true; 
            MainForm.ResizeMode = System.Windows.ResizeMode.NoResize;
            
            CarregarConfiguracoesAntigas();
            InitializeComponents(familiasGen, familiasMulti, tiposCircuito);
            PreencherWorksets();
        }

        private void InitializeComponents(List<string> familiasGen, List<string> familiasMulti, List<string> tiposCircuito)
        {
            WGrid mainGrid = new WGrid();
            mainGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition() { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            mainGrid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition() { Height = System.Windows.GridLength.Auto });
            
            tabControl = new WTabControl() { Margin = new WThickness(12, 5, 12, 5) };
            
            // ABA 1: FILTROS
            WTabItem tabFiltros = new WTabItem() { Header = "Filtros", Background = WBrushes.White };
            WGrid gridFiltros = new WGrid();
            gridFiltros.RowDefinitions.Add(new System.Windows.Controls.RowDefinition() { Height = System.Windows.GridLength.Auto });
            gridFiltros.RowDefinitions.Add(new System.Windows.Controls.RowDefinition() { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            
            WLabel lblFiltro = new WLabel() { Content = "Circuitos a serem tagueados:", FontWeight = System.Windows.FontWeights.Bold, Margin = new WThickness(10, 10, 0, 0) };
            System.Windows.Controls.Grid.SetRow(lblFiltro, 0);
            
            WScrollViewer scrollFiltros = new WScrollViewer() { Margin = new WThickness(10), BorderBrush = WBrushes.Gray, BorderThickness = new WThickness(1) };
            System.Windows.Controls.Grid.SetRow(scrollFiltros, 1);
            pnlFiltros = new WStackPanel() { Margin = new WThickness(5) };
            
            List<string> filtrosSalvos = configSalva.ContainsKey("FILTROS_ATIVOS") ? configSalva["FILTROS_ATIVOS"].Split('|').ToList() : new List<string>();
            bool temConfig = configSalva.ContainsKey("FILTROS_ATIVOS");

            foreach (var tc in tiposCircuito.OrderBy(x => x))
            {
                bool isChecked = temConfig ? filtrosSalvos.Contains(tc) : false;
                int maxLinha = 0;
                if (configSalva.ContainsKey("MAX_LINHA_" + tc)) int.TryParse(configSalva["MAX_LINHA_" + tc], out maxLinha);

                WGrid rowGrid = new WGrid() { Margin = new WThickness(0, 2, 0, 2) };
                rowGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition() { Width = new System.Windows.GridLength(200) });
                rowGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition() { Width = new System.Windows.GridLength(100) });
                rowGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition() { Width = new System.Windows.GridLength(60) });

                WCheckBox chk = new WCheckBox() { Content = tc, IsChecked = isChecked, VerticalAlignment = WVerticalAlignment.Center };
                WLabel lblMax = new WLabel() { Content = "Máx em linha:", FontSize = 10, VerticalAlignment = WVerticalAlignment.Center };
                WTextBox txt = new WTextBox() { Text = (maxLinha >= 0 ? maxLinha : 0).ToString(), VerticalAlignment = WVerticalAlignment.Center, TextAlignment = WTextAlignment.Center };

                System.Windows.Controls.Grid.SetColumn(chk, 0);
                System.Windows.Controls.Grid.SetColumn(lblMax, 1);
                System.Windows.Controls.Grid.SetColumn(txt, 2);

                rowGrid.Children.Add(chk);
                rowGrid.Children.Add(lblMax);
                rowGrid.Children.Add(txt);

                pnlFiltros.Children.Add(rowGrid);
                linhasFiltro.Add(new FiltroRow { TipoCircuito = tc, ChkAtivo = chk, TxtMaxLinha = txt });
            }
            scrollFiltros.Content = pnlFiltros;
            gridFiltros.Children.Add(lblFiltro);
            gridFiltros.Children.Add(scrollFiltros);
            tabFiltros.Content = gridFiltros;

            // ABA 2: FAMÍLIAS
            WTabItem tabFamilias = new WTabItem() { Header = "TAGS", Background = WBrushes.White };
            WScrollViewer scrollFamilias = new WScrollViewer() { VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
            WStackPanel pnlFamilias = new WStackPanel() { Margin = new WThickness(10) };
            
            cbEletrica = CriarComboUnificado(pnlFamilias, "Tomadas:", familiasGen, "TOMADAS (TOM)");
            cbFor = CriarComboUnificado(pnlFamilias, "Força:", familiasGen, "FORÇA (FOR)");
            cbComunicacao = CriarComboUnificado(pnlFamilias, "Dados e CFTV:", familiasGen, "DADOS");
            
            cbChamadaGen = CriarComboUnificado(pnlFamilias, "Chamada Externa (Anotação Genérica):", familiasGen, "CHAMADA_GEN");
            cbChamadaMulti = CriarComboUnificado(pnlFamilias, "Chamada Externa (Multi Category Tag):", familiasMulti, "CHAMADA_MULTI");
            
            scrollFamilias.Content = pnlFamilias;
            tabFamilias.Content = scrollFamilias;

            // ABA 3: REGRAS DE ROTEAMENTO
            WTabItem tabRegras = new WTabItem() { Header = "Regras (Worksets)", Background = WBrushes.White };
            WGrid gridRegras = new WGrid();
            gridRegras.RowDefinitions.Add(new System.Windows.Controls.RowDefinition() { Height = System.Windows.GridLength.Auto });
            gridRegras.RowDefinitions.Add(new System.Windows.Controls.RowDefinition() { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            
            WLabel lblRegras = new WLabel() { 
                Content = "Defina quais tipos de circuitos podem trafegar em cada Workset de infraestrutura:", 
                FontWeight = System.Windows.FontWeights.Bold, Margin = new WThickness(10, 10, 0, 10) 
            };
            System.Windows.Controls.Grid.SetRow(lblRegras, 0);

            dgvWorksets = new WDataGrid() {
                Margin = new WThickness(10),
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = System.Windows.Controls.DataGridHeadersVisibility.Column,
                Background = WBrushes.White,
                SelectionMode = System.Windows.Controls.DataGridSelectionMode.Single,
                ItemsSource = worksetItems
            };

            dgvWorksets.Columns.Add(new WDataGridTextColumn() { Header = "Workset", Binding = new System.Windows.Data.Binding("Workset"), IsReadOnly = true, Width = new System.Windows.Controls.DataGridLength(1, System.Windows.Controls.DataGridLengthUnitType.Star) });
            dgvWorksets.Columns.Add(new WDataGridCheckBoxColumn() { Header = "Dados", Binding = new System.Windows.Data.Binding("Dados") });
            dgvWorksets.Columns.Add(new WDataGridCheckBoxColumn() { Header = "Tomadas", Binding = new System.Windows.Data.Binding("Tomadas") });
            dgvWorksets.Columns.Add(new WDataGridCheckBoxColumn() { Header = "Iluminação", Binding = new System.Windows.Data.Binding("Iluminacao") });
            dgvWorksets.Columns.Add(new WDataGridCheckBoxColumn() { Header = "Força", Binding = new System.Windows.Data.Binding("Forca") });

            System.Windows.Controls.Grid.SetRow(dgvWorksets, 1);
            gridRegras.Children.Add(lblRegras);
            gridRegras.Children.Add(dgvWorksets);
            tabRegras.Content = gridRegras;

            tabControl.Items.Add(tabFiltros);
            tabControl.Items.Add(tabRegras); 
            tabControl.Items.Add(tabFamilias);
            
            System.Windows.Controls.Grid.SetRow(tabControl, 0);
            mainGrid.Children.Add(tabControl);

            btnSalvar = new WButton() { 
                Content = "SALVAR CONFIGURAÇÕES", Margin = new WThickness(15, 5, 15, 10), Height = 45, 
                Background = new System.Windows.Media.SolidColorBrush(WColor.FromRgb(91, 204, 46)), 
                Foreground = WBrushes.White, FontWeight = System.Windows.FontWeights.Bold
            };
            btnSalvar.Click += BtnSalvar_Click;
            System.Windows.Controls.Grid.SetRow(btnSalvar, 1);
            mainGrid.Children.Add(btnSalvar);
            
            MainForm.Content = mainGrid;
        }

        private void PreencherWorksets()
        {
            List<string> nomesWorksets = new List<string>();

            if (doc.IsWorkshared)
            {
                var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset);
                foreach (Workset ws in worksets) nomesWorksets.Add(ws.Name);
            }
            else
            {
                nomesWorksets.Add("Projeto Padrão (Local)");
            }

            foreach (string wsName in nomesWorksets)
            {
                string keyDados = $"WS_{wsName}_Dados";
                string keyTomadas = $"WS_{wsName}_Tomadas";
                string keyIlu = $"WS_{wsName}_Ilu";
                string keyForca = $"WS_{wsName}_Forca";

                bool valDados = configSalva.ContainsKey(keyDados) ? configSalva[keyDados] == "True" : false;
                bool valTomadas = configSalva.ContainsKey(keyTomadas) ? configSalva[keyTomadas] == "True" : false;
                bool valIlu = configSalva.ContainsKey(keyIlu) ? configSalva[keyIlu] == "True" : false;
                bool valForca = configSalva.ContainsKey(keyForca) ? configSalva[keyForca] == "True" : false;

                worksetItems.Add(new WorksetItem {
                    Workset = wsName,
                    Dados = valDados,
                    Tomadas = valTomadas,
                    Iluminacao = valIlu,
                    Forca = valForca
                });
            }
        }

        private WComboBox CriarComboUnificado(WStackPanel parent, string label, List<string> items, string loadKey)
        {
            WLabel lbl = new WLabel() { Content = label, FontWeight = System.Windows.FontWeights.Bold, Margin = new WThickness(10, 5, 10, 0) };
            WComboBox cb = new WComboBox() { Margin = new WThickness(10, 0, 10, 10), Width = 400, HorizontalAlignment = WHorizontalAlignment.Left };
            foreach(var item in items) cb.Items.Add(item);
            
            if (configSalva.ContainsKey(loadKey) && items.Contains(configSalva[loadKey])) cb.SelectedItem = configSalva[loadKey];
            else if (cb.Items.Count > 0) cb.SelectedIndex = 0;
            
            parent.Children.Add(lbl); 
            parent.Children.Add(cb);
            return cb;
        }

        private void CarregarConfiguracoesAntigas()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AegiaLT.json");
            if (!File.Exists(path)) return;
            try {
                string json = File.ReadAllText(path);
                
                // Manual JSON string parsing to avoid Regex CS0433
                string[] parts = json.Split(new string[] { "\",\n", "\"\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string part in parts)
                {
                    int colonIndex = part.IndexOf("\": \"");
                    if (colonIndex > 0)
                    {
                        int keyStart = part.IndexOf("\"");
                        if (keyStart >= 0 && keyStart < colonIndex)
                        {
                            string key = part.Substring(keyStart + 1, colonIndex - keyStart - 1);
                            string value = part.Substring(colonIndex + 4);
                            if (value.EndsWith("\"")) value = value.Substring(0, value.Length - 1);
                            configSalva[key] = value;
                        }
                    }
                }
            } catch { }
        }

        private void BtnSalvar_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            try {
                string valEletrica = cbEletrica.SelectedItem?.ToString() ?? "";
                string valForca = cbFor.SelectedItem?.ToString() ?? "";
                string valComunicacao = cbComunicacao.SelectedItem?.ToString() ?? "";
                string valChamadaGen = cbChamadaGen.SelectedItem?.ToString() ?? "";
                string valChamadaMulti = cbChamadaMulti.SelectedItem?.ToString() ?? "";

                string json = "{\n";
                json += $"  \"TOMADAS (TOM)\": \"{valEletrica}\",\n";
                json += $"  \"ILUMINAÇÃO (ILU)\": \"{valEletrica}\",\n";
                json += $"  \"FORÇA (FOR)\": \"{valForca}\",\n";
                json += $"  \"DADOS\": \"{valComunicacao}\",\n";
                json += $"  \"CFTV\": \"{valComunicacao}\",\n";
                json += $"  \"CHAMADA_GEN\": \"{valChamadaGen}\",\n";
                json += $"  \"CHAMADA_MULTI\": \"{valChamadaMulti}\"";

                // Salvar Filtros de Circuito
                List<string> checkedFilters = new List<string>();
                foreach (var linha in linhasFiltro)
                {
                    if (linha.ChkAtivo.IsChecked == true) checkedFilters.Add(linha.TipoCircuito);
                    
                    int maxLinha = 0;
                    if (int.TryParse(linha.TxtMaxLinha.Text, out int parsed)) maxLinha = parsed;
                    
                    json += $",\n  \"MAX_LINHA_{linha.TipoCircuito}\": \"{maxLinha}\"";
                }
                json += $",\n  \"FILTROS_ATIVOS\": \"{string.Join("|", checkedFilters)}\"";

                // Salvar Regras de Worksets
                foreach (var item in worksetItems)
                {
                    json += $",\n  \"WS_{item.Workset}_Dados\": \"{item.Dados}\"";
                    json += $",\n  \"WS_{item.Workset}_Tomadas\": \"{item.Tomadas}\"";
                    json += $",\n  \"WS_{item.Workset}_Ilu\": \"{item.Iluminacao}\"";
                    json += $",\n  \"WS_{item.Workset}_Forca\": \"{item.Forca}\"";
                }

                // Salvar Geometria das Tags (Extraindo de todas selecionadas, caso existam)
                var selectedFamilies = new[] { valEletrica, valForca, valComunicacao, valChamadaGen, valChamadaMulti }
                                        .Where(s => !string.IsNullOrEmpty(s)).Distinct();
                
                foreach (string fam in selectedFamilies) {
                    if (parametrosExtraidos.ContainsKey(fam)) {
                        var d = parametrosExtraidos[fam];
                        string key = fam.Replace("\"", "'");
                        json += $",\n  \"PARAM_{key}_Zform\": \"{d.Zform}\"";
                        json += $",\n  \"PARAM_{key}_ZlargNT\": \"{d.ZlargNT.ToString(CultureInfo.InvariantCulture)}\"";
                        json += $",\n  \"PARAM_{key}_Zalt\": \"{d.Zalt.ToString(CultureInfo.InvariantCulture)}\"";
                        json += $",\n  \"PARAM_{key}_Zaf\": \"{d.Zaf.ToString(CultureInfo.InvariantCulture)}\"";
                    }
                }
                json += "\n}";

                File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AegiaLT.json"), json);
                Autodesk.Revit.UI.TaskDialog.Show("Aegia", "Configurações salvas com sucesso!");
                MainForm.Close();
            } catch (Exception ex) { Autodesk.Revit.UI.TaskDialog.Show("Erro", "Erro ao salvar: " + ex.Message); }
        }
    }
}
