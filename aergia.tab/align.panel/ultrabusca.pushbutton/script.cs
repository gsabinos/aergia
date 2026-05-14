using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

// WPF Namespaces
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

// Alias para evitar ambiguidade
using RView = Autodesk.Revit.DB.View;
using WWindow = System.Windows.Window;
using WTextBox = System.Windows.Controls.TextBox;
using WCheckBox = System.Windows.Controls.CheckBox;
using WLabel = System.Windows.Controls.Label;
using WButton = System.Windows.Controls.Button;
using WDataGrid = System.Windows.Controls.DataGrid;

namespace Aegia_Ultrabusca
{
    [Transaction(TransactionMode.ReadOnly)]
    public class UltrabuscaCommand : IExternalCommand
    {
        // --- ESTRUTURAS DE DADOS ---
        public class ResultadoTexto
        {
            public string Documento { get; set; }
            public string ID { get; set; }
            public string Vista { get; set; }
            public string Conteudo { get; set; }
        }

        public class ResultadoElemento
        {
            public string Documento { get; set; }
            public string ID { get; set; }
            public string Categoria { get; set; }
            public string Familia_Tipo { get; set; }
            public string Parametro { get; set; }
            public string Valor { get; set; }
            public string Workset { get; set; }
        }

        // --- 1. O "CARTEIRO" (EVENTO EXTERNO PARA SELECIONAR ELEMENTOS) ---
        public class SelecionarElementoHandler : IExternalEventHandler
        {
            public ElementId ElementoParaSelecionar { get; set; } = ElementId.InvalidElementId;

            public void Execute(UIApplication app)
            {
                if (ElementoParaSelecionar != ElementId.InvalidElementId)
                {
                    UIDocument uidoc = app.ActiveUIDocument;
                    try
                    {
                        uidoc.Selection.SetElementIds(new List<ElementId> { ElementoParaSelecionar });
                        uidoc.ShowElements(ElementoParaSelecionar); 
                    }
                    catch { }
                }
            }

            public string GetName() => "Selecionar Elemento Ultrabusca";
        }

        // --- 2. INTERFACE DE ENTRADA (MODAL) ---
        public class BuscaDialog : WWindow
        {
            public WTextBox TxtBusca { get; private set; }
            public WCheckBox ChkElementos { get; private set; }
            public WCheckBox ChkTextos { get; private set; }
            public WCheckBox ChkLinks { get; private set; }
            public bool Confirmado { get; private set; } = false;

            public BuscaDialog()
            {
                this.Title = "Ultrabusca Aegia";
                this.Width = 350; this.Height = 250;
                this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                this.ResizeMode = ResizeMode.NoResize;
                this.Topmost = true;

                Canvas canvas = new Canvas();
                this.Content = canvas;

                WLabel lblBusca = new WLabel() { Content = "Termo de Busca (Ignora IFC GUID):" };
                Canvas.SetLeft(lblBusca, 10); Canvas.SetTop(lblBusca, 10);
                canvas.Children.Add(lblBusca);

                TxtBusca = new WTextBox() { Width = 300, Height = 22 };
                Canvas.SetLeft(TxtBusca, 15); Canvas.SetTop(TxtBusca, 35);
                canvas.Children.Add(TxtBusca);

                ChkElementos = new WCheckBox() { Content = "1️⃣ Buscar em Elementos", IsChecked = true };
                Canvas.SetLeft(ChkElementos, 15); Canvas.SetTop(ChkElementos, 70);
                canvas.Children.Add(ChkElementos);

                ChkTextos = new WCheckBox() { Content = "2️⃣ Buscar em Notas de Texto", IsChecked = true };
                Canvas.SetLeft(ChkTextos, 15); Canvas.SetTop(ChkTextos, 95);
                canvas.Children.Add(ChkTextos);

                ChkLinks = new WCheckBox() { Content = "🔗 Incluir Vínculos (Links)", IsChecked = false };
                Canvas.SetLeft(ChkLinks, 15); Canvas.SetTop(ChkLinks, 120);
                canvas.Children.Add(ChkLinks);

                WButton btnOk = new WButton() { Content = "Buscar", Width = 80, Height = 25, Background = Brushes.LightGreen };
                Canvas.SetLeft(btnOk, 140); Canvas.SetTop(btnOk, 160);
                btnOk.Click += (sender, e) => {
                    if (string.IsNullOrWhiteSpace(TxtBusca.Text)) {
                        Autodesk.Revit.UI.TaskDialog.Show("Erro", "Digite um termo para buscar."); return;
                    }
                    Confirmado = true; this.Close();
                };
                canvas.Children.Add(btnOk);

                WButton btnCancel = new WButton() { Content = "Cancelar", Width = 80, Height = 25 };
                Canvas.SetLeft(btnCancel, 235); Canvas.SetTop(btnCancel, 160);
                btnCancel.Click += (sender, e) => { this.Close(); };
                canvas.Children.Add(btnCancel);

                // Enter e Esc para Ok e Cancelar
                this.KeyDown += (sender, e) => {
                    if (e.Key == Key.Enter) { btnOk.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
                    else if (e.Key == Key.Escape) { btnCancel.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
                };
                
                TxtBusca.Focus();
            }
        }

        // --- 3. DASHBOARD DE RESULTADOS (MODELESS) ---
        public class RelatorioDialog : WWindow
        {
            public RelatorioDialog(List<ResultadoElemento> elementos, List<ResultadoTexto> textos, string termo, SelecionarElementoHandler handler, ExternalEvent exEvent)
            {
                this.Title = $"Resultados da Ultrabusca: '{termo}'";
                this.Width = 1000;
                this.Height = 500;
                this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                this.Topmost = true; 

                TabControl tabControl = new TabControl();
                this.Content = tabControl;

                if (elementos.Count > 0)
                {
                    TabItem tabElem = new TabItem { Header = "🧱 Elementos e Parâmetros" };
                    WDataGrid gridElem = new WDataGrid {
                        AutoGenerateColumns = true,
                        IsReadOnly = true,
                        SelectionMode = DataGridSelectionMode.Single,
                        SelectionUnit = DataGridSelectionUnit.FullRow,
                        ItemsSource = elementos
                    };
                    
                    gridElem.MouseDoubleClick += (s, e) => {
                        var selected = gridElem.SelectedItem as ResultadoElemento;
                        if (selected != null && selected.Documento == "Local") {
                            handler.ElementoParaSelecionar = new ElementId(Convert.ToInt64(selected.ID));
                            exEvent.Raise();
                        }
                    };

                    tabElem.Content = gridElem;
                    tabControl.Items.Add(tabElem);
                }

                if (textos.Count > 0)
                {
                    TabItem tabTxt = new TabItem { Header = "📝 Notas de Texto" };
                    WDataGrid gridTxt = new WDataGrid {
                        AutoGenerateColumns = true,
                        IsReadOnly = true,
                        SelectionMode = DataGridSelectionMode.Single,
                        SelectionUnit = DataGridSelectionUnit.FullRow,
                        ItemsSource = textos
                    };
                    
                    gridTxt.MouseDoubleClick += (s, e) => {
                        var selected = gridTxt.SelectedItem as ResultadoTexto;
                        if (selected != null && selected.Documento == "Local") {
                            handler.ElementoParaSelecionar = new ElementId(Convert.ToInt64(selected.ID));
                            exEvent.Raise();
                        }
                    };

                    tabTxt.Content = gridTxt;
                    tabControl.Items.Add(tabTxt);
                }
            }
        }

        // --- LOOP PRINCIPAL DO COMANDO ---
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument.Document;

            BuscaDialog dialog = new BuscaDialog();
            dialog.ShowDialog();

            if (!dialog.Confirmado) return Result.Cancelled;

            string termoBusca = dialog.TxtBusca.Text.ToLower();
            var resultadosTextos = new List<ResultadoTexto>();
            var resultadosElementos = new List<ResultadoElemento>();

            var documentosAlvo = new List<Tuple<Document, string, bool>>();
            documentosAlvo.Add(new Tuple<Document, string, bool>(doc, "Local", false));

            if (dialog.ChkLinks.IsChecked == true)
            {
                var links = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>();
                foreach (var link in links)
                {
                    Document linkDoc = link.GetLinkDocument();
                    if (linkDoc != null) documentosAlvo.Add(new Tuple<Document, string, bool>(linkDoc, link.Name, true));
                }
            }

            foreach (var docTuple in documentosAlvo)
            {
                Document alvoDoc = docTuple.Item1;
                string nomeDoc = docTuple.Item2;
                bool isLink = docTuple.Item3;

                if (dialog.ChkTextos.IsChecked == true)
                {
                    var textNotes = new FilteredElementCollector(alvoDoc).OfClass(typeof(TextNote)).Cast<TextNote>();
                    foreach (TextNote txt in textNotes)
                    {
                        try
                        {
                            if (!txt.IsValidObject) continue;
                            string conteudo = txt.Text;
                            if (!string.IsNullOrEmpty(conteudo) && conteudo.ToLower().Contains(termoBusca))
                            {
                                resultadosTextos.Add(new ResultadoTexto() {
                                    Documento = isLink ? nomeDoc : "Local",
                                    ID = txt.Id.ToString(), Vista = ObterNomeVista(alvoDoc, txt),
                                    Conteudo = conteudo.Replace("\r", " ").Replace("\n", " ")
                                });
                            }
                        }
                        catch { }
                    }
                }

                if (dialog.ChkElementos.IsChecked == true)
                {
                    var collector = new FilteredElementCollector(alvoDoc).WhereElementIsNotElementType();
                    foreach (Element el in collector)
                    {
                        try
                        {
                            if (el is TextNote || el.Category == null || !el.IsValidObject) continue;

                            Tuple<string, string> match = BuscarNosParametros(el, termoBusca);
                            
                            if (match == null)
                            {
                                ElementId typeId = el.GetTypeId();
                                if (typeId != ElementId.InvalidElementId)
                                {
                                    Element elType = alvoDoc.GetElement(typeId);
                                    if (elType != null && elType.IsValidObject)
                                        match = BuscarNosParametros(elType, termoBusca, true);
                                }
                            }

                            if (match != null)
                            {
                                string famName = "N/A";
                                if (alvoDoc.GetElement(el.GetTypeId()) is ElementType eType) famName = eType.FamilyName;

                                resultadosElementos.Add(new ResultadoElemento() {
                                    Documento = isLink ? nomeDoc : "Local",
                                    ID = el.Id.ToString(), Categoria = el.Category.Name,
                                    Familia_Tipo = $"{famName} - {el.Name}",
                                    Parametro = match.Item1,
                                    Valor = match.Item2.Replace("\r", " ").Replace("\n", " "),
                                    Workset = ObterNomeWorkset(alvoDoc, el)
                                });
                            }
                        }
                        catch { }
                    }
                }
            }

            if (resultadosTextos.Count == 0 && resultadosElementos.Count == 0)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Ultrabusca", $"Nenhuma ocorrência encontrada para: '{termoBusca}'");
                return Result.Succeeded;
            }

            SelecionarElementoHandler handler = new SelecionarElementoHandler();
            ExternalEvent exEvent = ExternalEvent.Create(handler);

            RelatorioDialog relatorio = new RelatorioDialog(resultadosElementos, resultadosTextos, dialog.TxtBusca.Text, handler, exEvent);
            relatorio.Show(); 

            return Result.Succeeded;
        }

        // --- FUNÇÕES DE APOIO ---
        private Tuple<string, string> BuscarNosParametros(Element el, string termoBusca, bool isType = false)
        {
            foreach (Parameter param in el.Parameters)
            {
                try
                {
                    if (!param.HasValue || param.StorageType != StorageType.String) continue;
                    string pName = param.Definition.Name;
                    if (string.IsNullOrEmpty(pName) || (pName.ToLower().Contains("ifc") && pName.ToLower().Contains("guid"))) continue;

                    string val = param.AsString();
                    if (!string.IsNullOrEmpty(val) && val.ToLower().Contains(termoBusca))
                        return new Tuple<string, string>(isType ? $"{pName} (Tipo)" : pName, val);
                }
                catch { } 
            }
            return null;
        }

        private string ObterNomeWorkset(Document doc, Element el)
        {
            try { return (doc.IsWorkshared && el.WorksetId != WorksetId.InvalidWorksetId) ? doc.GetWorksetTable().GetWorkset(el.WorksetId)?.Name ?? "N/A" : "N/A (Local)"; }
            catch { return "N/A"; }
        }

        private string ObterNomeVista(Document doc, Element el)
        {
            try { return doc.GetElement(el.OwnerViewId)?.Name ?? "Modelo (3D)"; }
            catch { return "Modelo (3D)"; }
        }
    }
}