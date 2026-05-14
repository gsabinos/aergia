using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

// WPF Namespaces
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

// Win32 para Dialogs
using Microsoft.Win32;

// Alias para evitar ambiguidade (Sem System.Drawing e WinForms)
using RView = Autodesk.Revit.DB.View;
using WWindow = System.Windows.Window;
using WTextBox = System.Windows.Controls.TextBox;
using WCheckBox = System.Windows.Controls.CheckBox;
using WLabel = System.Windows.Controls.Label;
using WButton = System.Windows.Controls.Button;
using WDataGrid = System.Windows.Controls.DataGrid;
using WCanvas = System.Windows.Controls.Canvas;

namespace Aegia
{
    [Transaction(TransactionMode.Manual)]
    public class DataSyncCommand : IExternalCommand
    {
        // --- FUNÇÕES DE APOIO ---
        public static Parameter ObterParametroSeguro(Element el, ElementId paramId)
        {
            if (el == null || paramId == null || paramId == ElementId.InvalidElementId) return null;
            foreach (Parameter p in el.Parameters)
            {
                if (p.Id.Equals(paramId)) return p;
            }
            return null;
        }

        // Método de conversão de Cor RGB para OLE do Excel (Evitando usar System.Drawing.ColorTranslator)
        public static int ToOleColor(byte r, byte g, byte b)
        {
            return r + (g * 256) + (b * 65536);
        }

        public static class ExcelCOM
        {
            public static object Get(object obj, string propName, params object[] args)
            {
                return obj.GetType().InvokeMember(propName, BindingFlags.GetProperty, null, obj, args.Length == 0 ? null : args);
            }
            public static void Set(object obj, string propName, params object[] args)
            {
                obj.GetType().InvokeMember(propName, BindingFlags.SetProperty, null, obj, args);
            }
            public static object Call(object obj, string methodName, params object[] args)
            {
                return obj.GetType().InvokeMember(methodName, BindingFlags.InvokeMethod, null, obj, args.Length == 0 ? null : args);
            }
        }

        // --- ESTRUTURAS DE DADOS ---
        // Sem INotifyPropertyChanged porque em .NET 8/10 o tipo está forwarded para
        // System.ObjectModel.dll e o pyRevit não referencia essa assembly. Mudanças no
        // valor de Exportar são propagadas para a UI via GridTabelas.Items.Refresh().
        public class TabelaRevit
        {
            public string Nome { get; set; }
            public string Categoria { get; set; }
            public bool Exportar { get; set; }
            public string ID { get; set; }
            public ViewSchedule ElementoTabela { get; set; }
        }

        public class ExportContext
        {
            public List<ViewSchedule> Tabelas { get; set; }
            public string CaminhoSalvar { get; set; }
        }

        public class ImportContext
        {
            public string ArquivoExcel { get; set; }
        }

        // --- 1. OS "CARTEIROS" (EVENTOS EXTERNOS) ---
        
        // HANDLER 1: EXPORTAÇÃO DE DADOS (PARA SINCRONIZAÇÃO / EDIÇÃO)
        public class ExcelExportHandler : IExternalEventHandler
        {
            public ExportContext Context { get; set; }

            public void Execute(UIApplication app)
            {
                if (Context == null || Context.Tabelas == null || Context.Tabelas.Count == 0 || string.IsNullOrEmpty(Context.CaminhoSalvar)) return;
                Document doc = app.ActiveUIDocument.Document;

                object excel = null;
                object workbook = null;
                try
                {
                    Type excelType = Type.GetTypeFromProgID("Excel.Application");
                    if (excelType == null)
                    {
                        Autodesk.Revit.UI.TaskDialog.Show("Erro", "Microsoft Excel não encontrado nesta máquina.");
                        return;
                    }

                    excel = Activator.CreateInstance(excelType);
                    ExcelCOM.Set(excel, "Visible", false); 
                    ExcelCOM.Set(excel, "DisplayAlerts", false); 
                    
                    object workbooks = ExcelCOM.Get(excel, "Workbooks");
                    workbook = ExcelCOM.Call(workbooks, "Add");
                    object worksheets = ExcelCOM.Get(workbook, "Worksheets");

                    int sheetIndex = 1;
                    List<string> sheetNames = new List<string>();

                    foreach (ViewSchedule schedule in Context.Tabelas)
                    {
                        object worksheet;
                        if (sheetIndex == 1) worksheet = ExcelCOM.Get(worksheets, "Item", 1);
                        else worksheet = ExcelCOM.Call(worksheets, "Add", Missing.Value, ExcelCOM.Get(worksheets, "Item", sheetIndex - 1));

                        string safeName = string.IsNullOrEmpty(schedule.Name) ? "Tabela" : schedule.Name;
                        safeName = safeName.Length > 30 ? safeName.Substring(0, 30) : safeName;
                        if (sheetNames.Contains(safeName)) safeName += "_" + sheetIndex;
                        sheetNames.Add(safeName);

                        ExcelCOM.Set(worksheet, "Name", safeName);

                        ScheduleDefinition def = schedule.Definition;
                        int fieldCount = def.GetFieldCount();
                        
                        var collector = new FilteredElementCollector(doc, schedule.Id).WhereElementIsNotElementType();
                        var elementos = collector.ToElements();

                        if (elementos.Count == 0) continue;

                        object cell11 = ExcelCOM.Get(worksheet, "Cells", 1, 1);
                        ExcelCOM.Set(cell11, "Value", "Element_ID (NÃO MODIFICAR)");
                        ExcelCOM.Set(ExcelCOM.Get(cell11, "Interior"), "Color", ToOleColor(255, 0, 0)); // Red

                        object cell21 = ExcelCOM.Get(worksheet, "Cells", 2, 1);
                        ExcelCOM.Set(cell21, "Value", "UNIDADE ->");
                        ExcelCOM.Set(ExcelCOM.Get(cell21, "Interior"), "Color", ToOleColor(211, 211, 211)); // LightGray

                        List<ScheduleField> validFields = new List<ScheduleField>();
                        int colIndex = 2;

                        for (int i = 0; i < fieldCount; i++)
                        {
                            ScheduleField field = def.GetField(i);
                            if (field.IsHidden) continue;
                            
                            validFields.Add(field);
                            
                            object cellHead = ExcelCOM.Get(worksheet, "Cells", 1, colIndex);
                            ExcelCOM.Set(cellHead, "Value", field.GetName());

                            Element firstEl = elementos[0];
                            bool isReadOnly = true;
                            bool isType = false;
                            string unitSymbol = "";

                            Parameter p = ObterParametroSeguro(firstEl, field.ParameterId);
                            if (p == null)
                            {
                                ElementId typeId = firstEl.GetTypeId();
                                if (typeId != ElementId.InvalidElementId)
                                {
                                    Element elType = doc.GetElement(typeId);
                                    p = ObterParametroSeguro(elType, field.ParameterId);
                                    if (p != null) { isType = true; isReadOnly = p.IsReadOnly; }
                                }
                            }
                            else { isReadOnly = p.IsReadOnly; }

                            if (p != null && p.StorageType == StorageType.Double)
                            {
                                try
                                {
                                    ForgeTypeId unitTypeId = p.GetUnitTypeId();
                                    if (unitTypeId != null && !unitTypeId.Empty())
                                    {
                                        FormatOptions fo = doc.GetUnits().GetFormatOptions(unitTypeId);
                                        ForgeTypeId symbolId = fo.GetSymbolTypeId();
                                        if (symbolId != null && !symbolId.Empty()) unitSymbol = LabelUtils.GetLabelForSymbol(symbolId);
                                    }
                                }
                                catch { }
                            }

                            int oleColor = ToOleColor(144, 238, 144); // LightGreen
                            if (isReadOnly) oleColor = ToOleColor(240, 128, 128); // LightCoral
                            else if (isType) oleColor = ToOleColor(255, 255, 0); // Yellow
                            
                            ExcelCOM.Set(ExcelCOM.Get(cellHead, "Interior"), "Color", oleColor);

                            object cellUnit = ExcelCOM.Get(worksheet, "Cells", 2, colIndex);
                            ExcelCOM.Set(cellUnit, "Value", unitSymbol);
                            ExcelCOM.Set(ExcelCOM.Get(cellUnit, "Interior"), "Color", ToOleColor(211, 211, 211)); // LightGray

                            colIndex++;
                        }

                        int rowIndex = 3; 
                        foreach (Element el in elementos)
                        {
                            if (el == null || !el.IsValidObject || el.Category == null) continue;

                            object cellId = ExcelCOM.Get(worksheet, "Cells", rowIndex, 1);
                            ExcelCOM.Set(cellId, "Value", el.Id.ToString());
                            
                            colIndex = 2;
                            foreach (var field in validFields)
                            {
                                Parameter p = ObterParametroSeguro(el, field.ParameterId);
                                if (p == null)
                                {
                                    ElementId typeId = el.GetTypeId();
                                    if (typeId != ElementId.InvalidElementId)
                                    {
                                        Element elType = doc.GetElement(typeId);
                                        p = ObterParametroSeguro(elType, field.ParameterId);
                                    }
                                }

                                object cellVal = ExcelCOM.Get(worksheet, "Cells", rowIndex, colIndex);
                                
                                if (p != null)
                                {
                                    if (p.StorageType == StorageType.Double)
                                    {
                                        try
                                        {
                                            ForgeTypeId unitTypeId = p.GetUnitTypeId();
                                            double valProject = UnitUtils.ConvertFromInternalUnits(p.AsDouble(), unitTypeId);
                                            ExcelCOM.Set(cellVal, "Value", valProject);
                                        }
                                        catch
                                        {
                                            ExcelCOM.Set(cellVal, "Value", p.AsDouble());
                                        }
                                    }
                                    else
                                    {
                                        string valStr = p.AsValueString() ?? p.AsString() ?? "";
                                        ExcelCOM.Set(cellVal, "Value", valStr);
                                    }
                                }
                                colIndex++;
                            }
                            rowIndex++;
                        }

                        object columns = ExcelCOM.Get(worksheet, "Columns");
                        ExcelCOM.Call(columns, "AutoFit");
                        
                        sheetIndex++;
                    }

                    ExcelCOM.Call(workbook, "SaveAs", Context.CaminhoSalvar);
                }
                catch (Exception ex)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Erro de Exportação", ex.Message);
                }
                finally
                {
                    if (workbook != null) ExcelCOM.Call(workbook, "Close", false);
                    if (excel != null) ExcelCOM.Call(excel, "Quit");
                    if (excel != null) Marshal.ReleaseComObject(excel);
                }
            }
            public string GetName() => "Aegia DataSync Export Handler";
        }

        // HANDLER 2: EXPORTAÇÃO VISUAL (100% IDÊNTICA AO REVIT)
        public class ExcelVisualExportHandler : IExternalEventHandler
        {
            public ExportContext Context { get; set; }

            public void Execute(UIApplication app)
            {
                if (Context == null || Context.Tabelas == null || Context.Tabelas.Count == 0 || string.IsNullOrEmpty(Context.CaminhoSalvar)) return;
                
                object excel = null;
                object workbook = null;
                try
                {
                    Type excelType = Type.GetTypeFromProgID("Excel.Application");
                    excel = Activator.CreateInstance(excelType);
                    ExcelCOM.Set(excel, "Visible", false); 
                    ExcelCOM.Set(excel, "DisplayAlerts", false); 
                    
                    object workbooks = ExcelCOM.Get(excel, "Workbooks");
                    workbook = ExcelCOM.Call(workbooks, "Add");
                    object worksheets = ExcelCOM.Get(workbook, "Worksheets");

                    int sheetIndex = 1;
                    List<string> sheetNames = new List<string>();

                    foreach (ViewSchedule schedule in Context.Tabelas)
                    {
                        object worksheet;
                        if (sheetIndex == 1) worksheet = ExcelCOM.Get(worksheets, "Item", 1);
                        else worksheet = ExcelCOM.Call(worksheets, "Add", Missing.Value, ExcelCOM.Get(worksheets, "Item", sheetIndex - 1));

                        string safeName = string.IsNullOrEmpty(schedule.Name) ? "Tabela" : schedule.Name;
                        safeName = safeName.Length > 30 ? safeName.Substring(0, 30) : safeName;
                        if (sheetNames.Contains(safeName)) safeName += "_" + sheetIndex;
                        sheetNames.Add(safeName);

                        ExcelCOM.Set(worksheet, "Name", safeName);

                        int excelRow = 1;
                        TableData tableData = schedule.GetTableData();
                        
                        Action<SectionType, TableSectionData> ExportarSecaoVisual = (secType, secData) => {
                            if (secData == null) return;

                            int startExRow = excelRow;

                            for (int r = secData.FirstRowNumber; r < secData.FirstRowNumber + secData.NumberOfRows; r++) {
                                for (int c = secData.FirstColumnNumber; c < secData.FirstColumnNumber + secData.NumberOfColumns; c++) {
                                    
                                    int exR = startExRow + (r - secData.FirstRowNumber);
                                    int exC = c - secData.FirstColumnNumber + 1;
                                    
                                    object excelCell = ExcelCOM.Get(worksheet, "Cells", exR, exC);
                                    
                                    // 1. Textos e Valores
                                    string val = schedule.GetCellText(secType, r, c);
                                    ExcelCOM.Set(excelCell, "Value", val);

                                    // 2. Extração de Estilo (TableCellStyle)
                                    TableCellStyle style = secData.GetTableCellStyle(r, c);
                                    if (style != null)
                                    {
                                        // Cor de Fundo (Shading)
                                        Autodesk.Revit.DB.Color bgColor = style.BackgroundColor;
                                        if (bgColor.IsValid) {
                                            object interior = ExcelCOM.Get(excelCell, "Interior");
                                            ExcelCOM.Set(interior, "Color", ToOleColor(bgColor.Red, bgColor.Green, bgColor.Blue));
                                        }

                                        // Estilos de Fonte
                                        object font = ExcelCOM.Get(excelCell, "Font");
                                        Autodesk.Revit.DB.Color fgColor = style.TextColor;
                                        if (fgColor.IsValid) ExcelCOM.Set(font, "Color", ToOleColor(fgColor.Red, fgColor.Green, fgColor.Blue));
                                        
                                        ExcelCOM.Set(font, "Bold", style.IsFontBold);
                                        ExcelCOM.Set(font, "Italic", style.IsFontItalic);
                                        ExcelCOM.Set(font, "Underline", style.IsFontUnderline);

                                        // Tamanho da fonte (Conversão Pés internos -> Pontos Excel)
                                        try {
                                            double fontSizePts = style.TextSize * 864.0; // 1 pé = 12 polegadas, 1 polegada = 72 pt (12*72 = 864)
                                            if (fontSizePts > 4 && fontSizePts < 100) ExcelCOM.Set(font, "Size", fontSizePts);
                                        } catch { }

                                        // Alinhamento Horizontal
                                        int xlAlign = -4131; // xlLeft
                                        if (style.FontHorizontalAlignment == HorizontalAlignmentStyle.Center) xlAlign = -4108; // xlCenter
                                        else if (style.FontHorizontalAlignment == HorizontalAlignmentStyle.Right) xlAlign = -4152; // xlRight
                                        ExcelCOM.Set(excelCell, "HorizontalAlignment", xlAlign);
                                        ExcelCOM.Set(excelCell, "VerticalAlignment", -4108); // xlCenter vertical
                                    }

                                    // 3. Mesclagens de Células (Merged Cells)
                                    TableMergedCell merge = secData.GetMergedCell(r, c);
                                    if (merge.Top == r && merge.Left == c) // Apenas executa na célula âncora (topo-esquerda da mescla)
                                    {
                                        if (merge.Bottom > merge.Top || merge.Right > merge.Left)
                                        {
                                            int exBottom = startExRow + (merge.Bottom - secData.FirstRowNumber);
                                            int exRight = (merge.Right - secData.FirstColumnNumber) + 1;
                                            
                                            object cellEnd = ExcelCOM.Get(worksheet, "Cells", exBottom, exRight);
                                            object mergeRange = ExcelCOM.Get(worksheet, "Range", excelCell, cellEnd);
                                            ExcelCOM.Call(mergeRange, "Merge");
                                        }
                                    }
                                }
                            }
                            excelRow += secData.NumberOfRows;
                        };

                        // Executar cabeçalho e corpo
                        ExportarSecaoVisual(SectionType.Header, tableData.GetSectionData(SectionType.Header));
                        ExportarSecaoVisual(SectionType.Body, tableData.GetSectionData(SectionType.Body));

                        // 4. Auto Ajuste de Colunas
                        object columns = ExcelCOM.Get(worksheet, "Columns");
                        ExcelCOM.Call(columns, "AutoFit");

                        // 5. Aplicar Bordas de Tabela em toda a área usada
                        object usedRange = ExcelCOM.Get(worksheet, "UsedRange");
                        if (usedRange != null) {
                            object borders = ExcelCOM.Get(usedRange, "Borders");
                            ExcelCOM.Set(borders, "LineStyle", 1); // xlContinuous
                            ExcelCOM.Set(borders, "Weight", 2);    // xlThin
                        }
                        
                        sheetIndex++;
                    }

                    ExcelCOM.Call(workbook, "SaveAs", Context.CaminhoSalvar);
                }
                catch (Exception ex)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Erro de Exportação Visual", ex.Message);
                }
                finally
                {
                    if (workbook != null) ExcelCOM.Call(workbook, "Close", false);
                    if (excel != null) ExcelCOM.Call(excel, "Quit");
                    if (excel != null) Marshal.ReleaseComObject(excel);
                }
            }

            public string GetName() => "Aegia Visual Export Handler";
        }

        // HANDLER 3: IMPORTAÇÃO DE DADOS EM LOTE
        public class ExcelImportHandler : IExternalEventHandler
        {
            public ImportContext Context { get; set; }

            public void Execute(UIApplication app)
            {
                if (Context == null || string.IsNullOrEmpty(Context.ArquivoExcel)) return;
                Document doc = app.ActiveUIDocument.Document;

                object excel = null;
                object workbook = null;

                try
                {
                    Type excelType = Type.GetTypeFromProgID("Excel.Application");
                    excel = Activator.CreateInstance(excelType);
                    ExcelCOM.Set(excel, "Visible", false);
                    
                    object workbooks = ExcelCOM.Get(excel, "Workbooks");
                    workbook = ExcelCOM.Call(workbooks, "Open", Context.ArquivoExcel);
                    object worksheets = ExcelCOM.Get(workbook, "Worksheets");
                    int sheetCount = (int)ExcelCOM.Get(worksheets, "Count");

                    using (Transaction t = new Transaction(doc, "Aegia - Importar Dados do Excel"))
                    {
                        t.Start();
                        int totalRowsUpdated = 0;

                        for (int s = 1; s <= sheetCount; s++)
                        {
                            object worksheet = ExcelCOM.Get(worksheets, "Item", s);
                            string sheetName = (string)ExcelCOM.Get(worksheet, "Name");

                            var schedule = new FilteredElementCollector(doc)
                                .OfClass(typeof(ViewSchedule))
                                .Cast<ViewSchedule>()
                                .FirstOrDefault(v => v.Name.StartsWith(sheetName) || sheetName.StartsWith(v.Name.Substring(0, Math.Min(v.Name.Length, 30))));

                            if (schedule == null) continue;

                            ScheduleDefinition def = schedule.Definition;
                            int fieldCount = def.GetFieldCount();
                            
                            List<ScheduleField> validFields = new List<ScheduleField>();
                            for (int i = 0; i < fieldCount; i++)
                            {
                                ScheduleField field = def.GetField(i);
                                if (!field.IsHidden) validFields.Add(field);
                            }

                            Dictionary<int, string> columnUnits = new Dictionary<int, string>();
                            int colIdx = 2;
                            foreach (var field in validFields)
                            {
                                object cellUnit = ExcelCOM.Get(worksheet, "Cells", 2, colIdx);
                                object unitVal = ExcelCOM.Get(cellUnit, "Value");
                                columnUnits[colIdx] = unitVal != null ? unitVal.ToString().Trim() : "";
                                colIdx++;
                            }

                            int rowIndex = 3;
                            int rowsUpdated = 0;

                            while (true)
                            {
                                object cellId = ExcelCOM.Get(worksheet, "Cells", rowIndex, 1);
                                object idCellVal = ExcelCOM.Get(cellId, "Value");
                                
                                if (idCellVal == null || string.IsNullOrWhiteSpace(idCellVal.ToString())) break;

                                string idStr = idCellVal.ToString();
                                if (long.TryParse(idStr, out long idLong))
                                {
                                    ElementId elId = new ElementId(idLong);
                                    Element el = doc.GetElement(elId);

                                    if (el != null && el.IsValidObject)
                                    {
                                        int cIndex = 2;
                                        foreach (var field in validFields)
                                        {
                                            object cellData = ExcelCOM.Get(worksheet, "Cells", rowIndex, cIndex);
                                            object valData = ExcelCOM.Get(cellData, "Value");
                                            string cellValue = valData != null ? valData.ToString() : "";
                                            string unitStr = columnUnits.ContainsKey(cIndex) ? columnUnits[cIndex] : "";

                                            Parameter p = ObterParametroSeguro(el, field.ParameterId);
                                            Element typeEl = null;

                                            if (p == null)
                                            {
                                                ElementId typeId = el.GetTypeId();
                                                if (typeId != ElementId.InvalidElementId)
                                                {
                                                    typeEl = doc.GetElement(typeId);
                                                    p = ObterParametroSeguro(typeEl, field.ParameterId);
                                                }
                                            }

                                            if (p != null && !p.IsReadOnly && !string.IsNullOrEmpty(cellValue))
                                            {
                                                try
                                                {
                                                    if (p.StorageType == StorageType.Double)
                                                    {
                                                        string valWithUnit = string.IsNullOrEmpty(unitStr) ? cellValue : $"{cellValue} {unitStr}";
                                                        bool success = p.SetValueString(valWithUnit);
                                                        
                                                        if (!success && double.TryParse(cellValue, out double rawVal)) 
                                                        {
                                                            p.Set(rawVal);
                                                        }
                                                    }
                                                    else if (p.StorageType == StorageType.String) 
                                                    {
                                                        p.Set(cellValue);
                                                    }
                                                    else if (p.StorageType == StorageType.Integer)
                                                    {
                                                        if (int.TryParse(cellValue, out int iVal)) p.Set(iVal);
                                                    }
                                                }
                                                catch { }
                                            }
                                            cIndex++;
                                        }
                                        rowsUpdated++;
                                    }
                                }
                                rowIndex++;
                            }
                            totalRowsUpdated += rowsUpdated;
                        }
                        t.Commit();
                        Autodesk.Revit.UI.TaskDialog.Show("Sucesso", $"Importação em lote concluída.\n{totalRowsUpdated} elementos analisados/atualizados no total.");
                    }
                }
                catch (Exception ex)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Erro de Importação", ex.Message);
                }
                finally
                {
                    if (workbook != null) ExcelCOM.Call(workbook, "Close", false);
                    if (excel != null) ExcelCOM.Call(excel, "Quit");
                    if (excel != null) Marshal.ReleaseComObject(excel);
                }
            }
            public string GetName() => "Aegia DataSync Import Handler";
        }

        // --- 2. INTERFACE DE USUÁRIO (MODELESS WPF) ---
        public class DataSyncDashboard : WWindow
        {
            private List<TabelaRevit> _todasTabelas;
            public WDataGrid GridTabelas { get; private set; }
            private bool todasMarcadas = false;

            // Adiciona uma coluna via reflection para evitar referência estática a
            // ObservableCollection<DataGridColumn> (forwarded para System.ObjectModel.dll
            // em .NET 8/10, e o pyRevit não inclui essa assembly).
            private static void AddDataGridColumn(WDataGrid dg, object col)
            {
                object cols = dg.GetType().GetProperty("Columns").GetValue(dg, null);
                cols.GetType().InvokeMember("Add", BindingFlags.InvokeMethod, null, cols, new object[] { col });
            }

            public DataSyncDashboard(List<TabelaRevit> tabelas, string activeViewId, ExcelExportHandler expHandler, ExternalEvent expEvent, ExcelImportHandler impHandler, ExternalEvent impEvent, ExcelVisualExportHandler visualHandler, ExternalEvent visualEvent)
            {
                _todasTabelas = tabelas;

                this.Title = "Aegia - Data Sync";
                this.Width = 720;
                this.Height = 550; 
                this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                this.Topmost = true; 
                
                WCanvas canvas = new WCanvas();
                this.Content = canvas;

                WLabel lblTitulo = new WLabel() {
                    Content = "Marque as Tabelas (Schedules) para Exportar/Sincronizar:",
                    Width = 400
                };
                WCanvas.SetLeft(lblTitulo, 10); WCanvas.SetTop(lblTitulo, 10);

                WTextBox txtBusca = new WTextBox() {
                    Width = 340, Height = 25,
                    Text = ""
                };
                WCanvas.SetLeft(txtBusca, 10); WCanvas.SetTop(txtBusca, 35);

                WButton btnSelecionarAtiva = new WButton() {
                    Content = "🎯 Marcar Vista Ativa",
                    Width = 150, Height = 25
                };
                WCanvas.SetLeft(btnSelecionarAtiva, 360); WCanvas.SetTop(btnSelecionarAtiva, 35);

                WButton btnMarcarTodas = new WButton() {
                    Content = "☑ Marcar Todas",
                    Width = 170, Height = 25
                };
                WCanvas.SetLeft(btnMarcarTodas, 520); WCanvas.SetTop(btnMarcarTodas, 35);

                GridTabelas = new WDataGrid() {
                    Width = 680, Height = 330,
                    AutoGenerateColumns = false,
                    CanUserAddRows = false,
                    SelectionMode = DataGridSelectionMode.Single,
                    SelectionUnit = DataGridSelectionUnit.FullRow,
                    Background = Brushes.White,
                    HeadersVisibility = DataGridHeadersVisibility.Column
                };
                WCanvas.SetLeft(GridTabelas, 10); WCanvas.SetTop(GridTabelas, 65);
                
                AddDataGridColumn(GridTabelas, new DataGridTextColumn() {
                    Header = "Nome da Tabela", Binding = new System.Windows.Data.Binding("Nome"), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star)
                });
                AddDataGridColumn(GridTabelas, new DataGridTextColumn() {
                    Header = "Categoria do Revit", Binding = new System.Windows.Data.Binding("Categoria"), IsReadOnly = true, Width = 150
                });
                AddDataGridColumn(GridTabelas, new DataGridCheckBoxColumn() {
                    Header = "Sel.", Binding = new System.Windows.Data.Binding("Exportar") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 45
                });

                GridTabelas.ItemsSource = _todasTabelas;

                // BOTÕES DE SINCRONIZAÇÃO
                WButton btnExportar = new WButton() {
                    Content = "🔼 Exportar para Excel (Sincronizar)",
                    Width = 330, Height = 35,
                    Background = Brushes.LightBlue,
                    FontWeight = FontWeights.Bold
                };
                WCanvas.SetLeft(btnExportar, 10); WCanvas.SetTop(btnExportar, 410);

                WButton btnImportar = new WButton() {
                    Content = "🔽 Importar Abas do Excel",
                    Width = 330, Height = 35,
                    Background = Brushes.LightGreen,
                    FontWeight = FontWeights.Bold
                };
                WCanvas.SetLeft(btnImportar, 10); WCanvas.SetTop(btnImportar, 455);

                WButton btnExportarVisual = new WButton() {
                    Content = "👁️ Exportar Formatação (Leitura)\nCopia o visual exato da tabela\n(Campos Calculados, Cores, Bordas)",
                    Width = 340, Height = 80,
                    Background = Brushes.Moccasin,
                    FontWeight = FontWeights.Bold
                };
                WCanvas.SetLeft(btnExportarVisual, 350); WCanvas.SetTop(btnExportarVisual, 410);

                // --- Eventos da Interface ---

                txtBusca.TextChanged += (s, e) => {
                    string termo = txtBusca.Text.ToLower();
                    if (string.IsNullOrWhiteSpace(termo)) {
                        GridTabelas.ItemsSource = _todasTabelas;
                    } else {
                        var filtrado = _todasTabelas.Where(t => t.Nome.ToLower().Contains(termo) || t.Categoria.ToLower().Contains(termo)).ToList();
                        GridTabelas.ItemsSource = filtrado;
                    }
                };

                btnSelecionarAtiva.Click += (s, e) => {
                    if (string.IsNullOrEmpty(activeViewId)) {
                        Autodesk.Revit.UI.TaskDialog.Show("Aviso", "A vista atual não é uma Tabela (Schedule)."); return;
                    }
                    foreach (var item in _todasTabelas) {
                        if (item.ID == activeViewId) {
                            item.Exportar = true; 
                            break;
                        }
                    }
                    GridTabelas.Items.Refresh();
                };

                btnMarcarTodas.Click += (s, e) => {
                    todasMarcadas = !todasMarcadas;
                    var currentViewData = GridTabelas.ItemsSource as IEnumerable<TabelaRevit>;
                    if (currentViewData != null) {
                        foreach (var tab in currentViewData) { tab.Exportar = todasMarcadas; }
                        GridTabelas.Items.Refresh();
                    }
                    btnMarcarTodas.Content = todasMarcadas ? "☐ Desmarcar Todas" : "☑ Marcar Todas";
                };

                btnExportar.Click += (s, e) => {
                    var selecionadas = _todasTabelas.Where(t => t.Exportar).Select(t => t.ElementoTabela).ToList();
                    
                    if (selecionadas.Count > 0) {
                        SaveFileDialog sfd = new SaveFileDialog();
                        sfd.Filter = "Excel Workbook|*.xlsx";
                        sfd.Title = "Aegia - Salvar Arquivo de Sincronização Excel";
                        sfd.FileName = "Aegia_DataSync.xlsx";
                        if (sfd.ShowDialog() == true) {
                            expHandler.Context = new ExportContext() { Tabelas = selecionadas, CaminhoSalvar = sfd.FileName };
                            expEvent.Raise();
                        }
                    }
                    else Autodesk.Revit.UI.TaskDialog.Show("Aviso", "Marque pelo menos uma tabela na coluna 'Sel.'.");
                };

                btnImportar.Click += (s, e) => {
                    OpenFileDialog ofd = new OpenFileDialog();
                    ofd.Filter = "Excel Files|*.xlsx;*.xls";
                    ofd.Title = "Aegia - Selecione o arquivo Excel para importar";
                    if (ofd.ShowDialog() == true) {
                        impHandler.Context = new ImportContext() { ArquivoExcel = ofd.FileName };
                        impEvent.Raise();
                    }
                };

                btnExportarVisual.Click += (s, e) => {
                    var selecionadas = _todasTabelas.Where(t => t.Exportar).Select(t => t.ElementoTabela).ToList();
                    
                    if (selecionadas.Count > 0) {
                        SaveFileDialog sfd = new SaveFileDialog();
                        sfd.Filter = "Excel Workbook|*.xlsx";
                        sfd.Title = "Aegia - Salvar Arquivo Formato Revit (Apenas Leitura)";
                        sfd.FileName = "Aegia_Relatorio_Visual.xlsx";
                        if (sfd.ShowDialog() == true) {
                            visualHandler.Context = new ExportContext() { Tabelas = selecionadas, CaminhoSalvar = sfd.FileName };
                            visualEvent.Raise();
                        }
                    }
                    else Autodesk.Revit.UI.TaskDialog.Show("Aviso", "Marque pelo menos uma tabela na coluna 'Sel.'.");
                };

                canvas.Children.Add(lblTitulo); canvas.Children.Add(txtBusca); canvas.Children.Add(btnSelecionarAtiva);
                canvas.Children.Add(btnMarcarTodas); canvas.Children.Add(GridTabelas);
                canvas.Children.Add(btnExportar); canvas.Children.Add(btnImportar); canvas.Children.Add(btnExportarVisual);

                this.Loaded += (s, e) => {
                    if (!string.IsNullOrEmpty(activeViewId)) {
                        foreach (var t in _todasTabelas) { if (t.ID == activeViewId) t.Exportar = true; }
                        GridTabelas.Items.Refresh();
                    }
                };
            }
        }

        // --- 3. LOOP PRINCIPAL DO COMANDO ---
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIApplication uiapp = commandData.Application;
                UIDocument uidoc = uiapp.ActiveUIDocument;

                if (uidoc == null || uidoc.Document == null)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia - Data Sync", "Por favor, abra um projeto no Revit antes de executar o script.");
                    return Result.Cancelled;
                }

                Document doc = uidoc.Document;
                List<TabelaRevit> schedules = new List<TabelaRevit>();

                string activeViewId = (doc.ActiveView != null && doc.ActiveView is ViewSchedule) ? doc.ActiveView.Id.ToString() : null;

                var collector = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule));
                
                foreach (Element el in collector)
                {
                    ViewSchedule v = el as ViewSchedule;
                    if (v == null || !v.IsValidObject) continue;
                    
                    try 
                    {
                        if (v.IsTemplate || v.IsInternalKeynoteSchedule || v.IsTitleblockRevisionSchedule) continue;
                        if (string.IsNullOrEmpty(v.Name)) continue;

                        string catName = "Multicategoria";
                        if (v.Definition.CategoryId != ElementId.InvalidElementId)
                        {
                            Category cat = Category.GetCategory(doc, v.Definition.CategoryId);
                            if (cat != null) catName = cat.Name;
                        }
                        
                        schedules.Add(new TabelaRevit() { Exportar = false, ID = v.Id.ToString(), Categoria = catName, Nome = v.Name, ElementoTabela = v });
                    }
                    catch { }
                }

                schedules = schedules.OrderBy(v => v.Categoria).ThenBy(v => v.Nome).ToList();

                if (schedules.Count == 0)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia - Data Sync", "Nenhuma tabela válida encontrada no modelo.");
                    return Result.Cancelled;
                }

                ExcelExportHandler expHandler = new ExcelExportHandler();
                ExternalEvent expEvent = ExternalEvent.Create(expHandler);

                ExcelImportHandler impHandler = new ExcelImportHandler();
                ExternalEvent impEvent = ExternalEvent.Create(impHandler);

                ExcelVisualExportHandler visualHandler = new ExcelVisualExportHandler();
                ExternalEvent visualEvent = ExternalEvent.Create(visualHandler);

                DataSyncDashboard dashboard = new DataSyncDashboard(schedules, activeViewId, expHandler, expEvent, impHandler, impEvent, visualHandler, visualEvent);
                dashboard.Show(); 

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Erro Crítico", $"Falha ao iniciar comando:\n\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}");
                return Result.Failed;
            }
        }
    }
}