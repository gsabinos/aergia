using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;

using WWindow = System.Windows.Window;
using WButton = System.Windows.Controls.Button;
using WTabControl = System.Windows.Controls.TabControl;
using WTabItem = System.Windows.Controls.TabItem;
using WDataGrid = System.Windows.Controls.DataGrid;
using WDataGridTextColumn = System.Windows.Controls.DataGridTextColumn;
using WGrid = System.Windows.Controls.Grid;
using WRowDefinition = System.Windows.Controls.RowDefinition;
using WGridUnitType = System.Windows.GridUnitType;
using WGridLength = System.Windows.GridLength;
using WThickness = System.Windows.Thickness;
using WSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WColor = System.Windows.Media.Color;
using WHorizontalAlignment = System.Windows.HorizontalAlignment;
using WVerticalAlignment = System.Windows.VerticalAlignment;

namespace Aegia_Automations
{
    [Transaction(TransactionMode.Manual)]
    public class QuantitativoCommand : IExternalCommand
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int VK_SHIFT = 0x10;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            short initialShiftState = GetAsyncKeyState(VK_SHIFT);
            bool isShiftInvoked = (initialShiftState & 0x8000) != 0;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string bimExtDir = Path.Combine(appData, "pyRevit", "Extensions", "BIM.extension", "lib");
            if (!Directory.Exists(bimExtDir)) try { Directory.CreateDirectory(bimExtDir); } catch { }
            string configPath = Path.Combine(bimExtDir, "aegialt_quantitativo_map.json");

            if (isShiftInvoked)
            {
                var form = new AegiaQuantitativoConfigForm(configPath);
                form.ShowDialog();
                return Result.Succeeded;
            }

            var config = File.Exists(configPath) ? ParseJsonSimple(File.ReadAllText(configPath)) : GerarConfigPadrao();
            if (!File.Exists(configPath)) SalvarConfiguracao(configPath, config);

            using (Transaction t = new Transaction(doc, "Gerar Quantitativo Aegia"))
            {
                t.Start();

                int countCir = 0;
                int countElev = 0;
                int countItem = 0;

                // =================================================================================
                // 1. Lógica do 'SET NOME CIR'
                // =================================================================================
                var categoriasEletricas = new List<BuiltInCategory>
                {
                    BuiltInCategory.OST_ElectricalFixtures,
                    BuiltInCategory.OST_LightingFixtures,
                    BuiltInCategory.OST_SpecialityEquipment,
                    BuiltInCategory.OST_DataDevices,
                    BuiltInCategory.OST_FireAlarmDevices,
                    BuiltInCategory.OST_CommunicationDevices,
                    BuiltInCategory.OST_SecurityDevices,
                    BuiltInCategory.OST_NurseCallDevices,
                    BuiltInCategory.OST_TelephoneDevices,
                    BuiltInCategory.OST_ElectricalEquipment
                };

                var multiCatFilter = new ElementMulticategoryFilter(categoriasEletricas);
                var elemEletricos = new FilteredElementCollector(doc)
                    .WherePasses(multiCatFilter)
                    .WhereElementIsNotElementType()
                    .ToList();

                foreach (Element elem in elemEletricos)
                {
                    if (elem is FamilyInstance fi && fi.MEPModel != null)
                    {
                        var sistemas = fi.MEPModel.GetElectricalSystems();
                        if (sistemas != null && sistemas.Count > 0)
                        {
                            ElectricalSystem sys = sistemas.Cast<ElectricalSystem>().FirstOrDefault();
                            if (sys != null)
                            {
                                string loadName = GetParamStringOrValue(sys, "Load Name") ?? sys.LoadName ?? sys.Name;
                                if (!string.IsNullOrEmpty(loadName))
                                {
                                    if (SetParamRobusto(elem, "Comments", loadName) || SetParamRobusto(elem, "Comentários", loadName))
                                    {
                                        countCir++;
                                    }
                                }
                            }
                        }
                    }
                }

                // =================================================================================
                // 2. Lógica do 'set elev' e 'set item'
                // =================================================================================
                var condutos = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Conduit)
                    .WhereElementIsNotElementType()
                    .ToList();

                var eletrocalhas = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_CableTray)
                    .WhereElementIsNotElementType()
                    .ToList();

                var todosConduitsETrays = condutos.Concat(eletrocalhas).ToList();

                foreach (Element elem in todosConduitsETrays)
                {
                    string refLevel = GetParamStringOrValue(elem, "Reference Level") ?? GetParamStringOrValue(elem, "Nível de referência");
                    if (!string.IsNullOrEmpty(refLevel))
                    {
                        if (SetParamRobusto(elem, "ZZ.ELNIV", refLevel)) countElev++;
                    }

                    bool isCableTray = elem.Category != null && elem.Category.Id.Value == (long)BuiltInCategory.OST_CableTray;
                    
                    string dimensao = "";
                    string comprimento = GetParamStringOrValue(elem, "Length") ?? GetParamStringOrValue(elem, "Comprimento");
                    string subItem = "NA";

                    if (isCableTray)
                    {
                        string width = (GetParamStringOrValue(elem, "Width") ?? "").Replace(",000000", "").Replace(".000000", "");
                        string height = (GetParamStringOrValue(elem, "Height") ?? "").Replace(",000000", "").Replace(".000000", "");
                        dimensao = $"{height}x{width}";

                        string key = $"TRAY_{dimensao}";
                        if (config.ContainsKey(key)) subItem = config[key];
                    }
                    else
                    {
                        string size = GetParamStringOrValue(elem, "Size") ?? GetParamStringOrValue(elem, "Tamanho") ?? "";
                        size = size.Replace("ø", "").Replace("Ø", "").Trim();
                        dimensao = $"Ø{size}";

                        string key = $"COND_{dimensao}";
                        if (config.ContainsKey(key)) subItem = config[key];
                    }

                    bool setDim = SetParamRobusto(elem, "Dimensões", dimensao);
                    bool setComp = SetParamRobusto(elem, "Comp", comprimento);
                    bool setSI = SetParamRobusto(elem, "SUB ITEM", subItem);

                    if (setDim || setComp || setSI) countItem++;
                }

                t.Commit();

                Autodesk.Revit.UI.TaskDialog.Show("Aegia | Quantitativo",
                    $"Atualização de Quantitativos concluída!\n\n" +
                    $"- Elementos c/ Nome do Circuito: {countCir}\n" +
                    $"- Condutos c/ ZZ.ELNIV atualizado: {countElev}\n" +
                    $"- Condutos c/ Dimensões, Comp e SUB ITEM: {countItem}");
            }

            return Result.Succeeded;
        }

        public static string GetParamStringOrValue(Element elem, string paramName)
        {
            Parameter p = elem.LookupParameter(paramName);
            
            if (p == null && elem.GetTypeId() != ElementId.InvalidElementId)
            {
                Element elemType = elem.Document.GetElement(elem.GetTypeId());
                if (elemType != null) p = elemType.LookupParameter(paramName);
            }

            if (p == null) return null;
            
            if (p.StorageType == StorageType.String) return p.AsString() ?? "";
            if (p.StorageType == StorageType.Integer) return p.AsValueString() ?? p.AsInteger().ToString();
            if (p.StorageType == StorageType.Double) return p.AsValueString() ?? p.AsDouble().ToString("0.00");
            
            return p.AsValueString() ?? "";
        }

        private bool SetParamRobusto(Element inst, string nome, string valor)
        {
            if (valor == null) return false;
            Parameter p = inst.LookupParameter(nome);
            if (p != null && !p.IsReadOnly)
            {
                if (p.StorageType == StorageType.String) p.Set(valor);
                else if (p.StorageType == StorageType.Integer) { if(int.TryParse(valor, out int v)) p.Set(v); }
                else if (p.StorageType == StorageType.Double) { if(double.TryParse(valor, out double v)) p.Set(v); }
                return true;
            }
            return false;
        }

        public static Dictionary<string, string> ParseJsonSimple(string j) 
        {
            var d = new Dictionary<string, string>();
            string[] parts = j.Split(new char[] { ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                int colon = part.IndexOf(':');
                if (colon > 0)
                {
                    string key = part.Substring(0, colon).Replace("{", "").Replace("}", "").Replace("\"", "").Trim();
                    string val = part.Substring(colon + 1).Replace("{", "").Replace("}", "").Replace("\"", "").Trim();
                    if (!string.IsNullOrEmpty(key)) d[key] = val;
                }
            }
            return d;
        }

        private Dictionary<string, string> GerarConfigPadrao()
        {
            return new Dictionary<string, string>
            {
                { "TRAY_38x38", "" }, { "TRAY_50x100", "1" }, { "TRAY_50x150", "2" }, { "TRAY_50x200", "3" },
                { "TRAY_50x300", "4" }, { "TRAY_50x400", "5" }, { "TRAY_100x100", "1" }, { "TRAY_100x150", "2" },
                { "TRAY_100x200", "3" }, { "TRAY_100x300", "4" }, { "TRAY_100x400", "5" }, { "TRAY_100x500", "6" },
                { "TRAY_100x600", "7" }, { "TRAY_100x800", "8" },
                { "COND_Ø1/2", "1" }, { "COND_Ø3/4", "2" }, { "COND_Ø1", "3" }, { "COND_Ø1 1/2", "4" },
                { "COND_Ø2", "5" }, { "COND_Ø2 1/2", "6" }, { "COND_Ø3", "7" }, { "COND_Ø4", "8" }
            };
        }

        public static void SalvarConfiguracao(string path, Dictionary<string, string> dict)
        {
            List<string> jsonLines = new List<string>();
            foreach (var kvp in dict)
            {
                string safeVal = kvp.Value.Replace("\"", "'").Replace("\n", "").Replace("\r", "");
                jsonLines.Add($"  \"{kvp.Key}\": \"{safeVal}\"");
            }
            string jsonOut = "{\n" + string.Join(",\n", jsonLines) + "\n}";
            File.WriteAllText(path, jsonOut);
        }
    }

    public class MappingItem
    {
        public string Dimensao { get; set; }
        public string SubItem { get; set; }
    }

    public class AegiaQuantitativoConfigForm : WWindow
    {
        private string configPath;
        private Dictionary<string, string> configCompleta;

        private WTabControl tabControl;
        private WTabItem tabEletrodutos;
        private WTabItem tabEletrocalhas;

        private WDataGrid dgvCond;
        private WDataGrid dgvTray;
        private WButton btnSalvar;

        public AegiaQuantitativoConfigForm(string path)
        {
            this.configPath = path;
            this.Title = "Aegia | Mapeamento de SUB ITEM (Quantitativo)";
            this.Width = 450;
            this.Height = 500;
            this.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            this.Topmost = true;
            this.ResizeMode = System.Windows.ResizeMode.NoResize;
            this.Background = new WSolidColorBrush(WColor.FromRgb(255, 255, 255));

            CarregarConfiguracoes();
            InitializeComponents();
            PreencherTabelas();
        }

        private void CarregarConfiguracoes()
        {
            if (File.Exists(configPath))
            {
                configCompleta = QuantitativoCommand.ParseJsonSimple(File.ReadAllText(configPath));
            }
            else
            {
                configCompleta = new Dictionary<string, string>
                {
                    { "TRAY_38x38", "" }, { "TRAY_50x100", "1" }, { "TRAY_50x150", "2" }, { "TRAY_50x200", "3" },
                    { "TRAY_50x300", "4" }, { "TRAY_50x400", "5" }, { "TRAY_100x100", "1" }, { "TRAY_100x150", "2" },
                    { "TRAY_100x200", "3" }, { "TRAY_100x300", "4" }, { "TRAY_100x400", "5" }, { "TRAY_100x500", "6" },
                    { "TRAY_100x600", "7" }, { "TRAY_100x800", "8" },
                    { "COND_Ø1/2", "1" }, { "COND_Ø3/4", "2" }, { "COND_Ø1", "3" }, { "COND_Ø1 1/2", "4" },
                    { "COND_Ø2", "5" }, { "COND_Ø2 1/2", "6" }, { "COND_Ø3", "7" }, { "COND_Ø4", "8" }
                };
            }
        }

        private void InitializeComponents()
        {
            WGrid grid = new WGrid();
            grid.RowDefinitions.Add(new WRowDefinition() { Height = new WGridLength(1, WGridUnitType.Star) });
            grid.RowDefinitions.Add(new WRowDefinition() { Height = new WGridLength(60) });

            tabControl = new WTabControl() { Margin = new WThickness(10) };

            tabEletrodutos = new WTabItem() { Header = "Eletrodutos", Background = new WSolidColorBrush(WColor.FromRgb(255, 255, 255)) };
            dgvCond = CriarGrid();
            tabEletrodutos.Content = dgvCond;

            tabEletrocalhas = new WTabItem() { Header = "Eletrocalhas", Background = new WSolidColorBrush(WColor.FromRgb(255, 255, 255)) };
            dgvTray = CriarGrid();
            tabEletrocalhas.Content = dgvTray;

            tabControl.Items.Add(tabEletrodutos);
            tabControl.Items.Add(tabEletrocalhas);

            btnSalvar = new WButton()
            {
                Content = "SALVAR MAPEAMENTO",
                Margin = new WThickness(10, 0, 10, 10),
                Background = new WSolidColorBrush(WColor.FromRgb(91, 204, 46)),
                Foreground = new WSolidColorBrush(WColor.FromRgb(255, 255, 255)),
                FontWeight = System.Windows.FontWeights.Bold
            };
            btnSalvar.Click += BtnSalvar_Click;

            WGrid.SetRow(tabControl, 0);
            WGrid.SetRow(btnSalvar, 1);

            grid.Children.Add(tabControl);
            grid.Children.Add(btnSalvar);

            this.Content = grid;
        }

        private WDataGrid CriarGrid()
        {
            var dgv = new WDataGrid()
            {
                CanUserAddRows = true,
                CanUserDeleteRows = true,
                HeadersVisibility = System.Windows.Controls.DataGridHeadersVisibility.Column,
                AutoGenerateColumns = false,
                Background = new WSolidColorBrush(WColor.FromRgb(245, 245, 245)),
                SelectionMode = System.Windows.Controls.DataGridSelectionMode.Single
            };

            var colDim = new WDataGridTextColumn() { Header = "Dimensão Lida", Binding = new System.Windows.Data.Binding("Dimensao"), Width = new System.Windows.Controls.DataGridLength(60, System.Windows.Controls.DataGridLengthUnitType.Star) };
            var colSub = new WDataGridTextColumn() { Header = "Valor 'SUB ITEM'", Binding = new System.Windows.Data.Binding("SubItem"), Width = new System.Windows.Controls.DataGridLength(40, System.Windows.Controls.DataGridLengthUnitType.Star) };

            dgv.Columns.Add(colDim);
            dgv.Columns.Add(colSub);

            return dgv;
        }

        private void PreencherTabelas()
        {
            var condList = new System.Collections.ObjectModel.ObservableCollection<MappingItem>();
            var trayList = new System.Collections.ObjectModel.ObservableCollection<MappingItem>();

            foreach (var kvp in configCompleta)
            {
                if (kvp.Key.StartsWith("COND_")) condList.Add(new MappingItem { Dimensao = kvp.Key.Substring(5), SubItem = kvp.Value });
                else if (kvp.Key.StartsWith("TRAY_")) trayList.Add(new MappingItem { Dimensao = kvp.Key.Substring(5), SubItem = kvp.Value });
            }

            dgvCond.ItemsSource = condList;
            dgvTray.ItemsSource = trayList;
        }

        private void BtnSalvar_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var novoConfig = new Dictionary<string, string>();

            var condList = dgvCond.ItemsSource as System.Collections.ObjectModel.ObservableCollection<MappingItem>;
            if (condList != null)
            {
                foreach (var item in condList)
                {
                    string dim = item.Dimensao ?? "";
                    string sub = item.SubItem ?? "";
                    if (!string.IsNullOrWhiteSpace(dim)) novoConfig["COND_" + dim.Trim()] = sub.Trim();
                }
            }

            var trayList = dgvTray.ItemsSource as System.Collections.ObjectModel.ObservableCollection<MappingItem>;
            if (trayList != null)
            {
                foreach (var item in trayList)
                {
                    string dim = item.Dimensao ?? "";
                    string sub = item.SubItem ?? "";
                    if (!string.IsNullOrWhiteSpace(dim)) novoConfig["TRAY_" + dim.Trim()] = sub.Trim();
                }
            }

            QuantitativoCommand.SalvarConfiguracao(configPath, novoConfig);

            Autodesk.Revit.UI.TaskDialog.Show("Aegia", "Mapeamento salvo com sucesso!");
            this.Close();
        }
    }
}