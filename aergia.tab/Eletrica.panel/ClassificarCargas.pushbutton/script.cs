using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Globalization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;

// Aliases WPF para evitar conflito com tipos Revit
using WWindow = System.Windows.Window;
using WButton = System.Windows.Controls.Button;
using WLabel = System.Windows.Controls.Label;
using WTabControl = System.Windows.Controls.TabControl;
using WTabItem = System.Windows.Controls.TabItem;
using WGrid = System.Windows.Controls.Grid;
using WStackPanel = System.Windows.Controls.StackPanel;
using WScrollViewer = System.Windows.Controls.ScrollViewer;
using WDataGrid = System.Windows.Controls.DataGrid;
using WDataGridTextColumn = System.Windows.Controls.DataGridTextColumn;
using WThickness = System.Windows.Thickness;
using WBrushes = System.Windows.Media.Brushes;
using WColor = System.Windows.Media.Color;

namespace Aegia_ClassificarCargas
{
    [Transaction(TransactionMode.Manual)]
    public class ClassificarCargasCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            ClassificarCargasHandler handler = new ClassificarCargasHandler();
            ExternalEvent exEvent = ExternalEvent.Create(handler);

            ClassificarCargasForm form = new ClassificarCargasForm(handler, exEvent);
            form.Show();
            return Result.Succeeded;
        }
    }

    // ==========================================
    // MODELO DE DADOS DA CLASSIFICAÇÃO
    // ==========================================
    public class ClassRow
    {
        public string Nome { get; set; } = "";
        public double PotenciaVA { get; set; } = 0.0;
        public double FP { get; set; } = 1.0;
    }

    // ==========================================
    // PERSISTÊNCIA (JSON manual / linhas Nome|VA|FP)
    // ==========================================
    public static class ConfigCargas
    {
        public static string ObterCaminho()
        {
            List<string> dirs = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pyRevit", "Extensions"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "pyRevit", "Extensions")
            };
            foreach (string dir in dirs)
            {
                if (Directory.Exists(dir))
                {
                    string lib = Path.Combine(dir, "BIM.extension", "lib");
                    if (Directory.Exists(lib)) return Path.Combine(lib, "cargas_nbr.json");
                }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "cargas_nbr.json");
        }

        public static List<ClassRow> Carregar()
        {
            var lista = new List<ClassRow>();
            string path = ObterCaminho();
            if (File.Exists(path))
            {
                try
                {
                    foreach (string linha in File.ReadAllLines(path))
                    {
                        if (string.IsNullOrWhiteSpace(linha)) continue;
                        string[] t = linha.Split('|');
                        if (t.Length < 3) continue;
                        double va, fp;
                        double.TryParse(t[1], NumberStyles.Any, CultureInfo.InvariantCulture, out va);
                        if (!double.TryParse(t[2], NumberStyles.Any, CultureInfo.InvariantCulture, out fp)) fp = 1.0;
                        lista.Add(new ClassRow { Nome = t[0].Trim(), PotenciaVA = va, FP = fp });
                    }
                }
                catch { }
            }
            if (lista.Count == 0) lista = Defaults();
            return lista;
        }

        public static void Salvar(IEnumerable<ClassRow> rows)
        {
            try
            {
                var linhas = rows
                    .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Nome))
                    .Select(r => string.Format("{0}|{1}|{2}",
                        r.Nome.Trim(),
                        r.PotenciaVA.ToString(CultureInfo.InvariantCulture),
                        r.FP.ToString(CultureInfo.InvariantCulture)));
                File.WriteAllText(ObterCaminho(), string.Join("\n", linhas));
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Erro", "Não foi possível salvar a configuração: " + ex.Message);
            }
        }

        // Defaults NBR 5410 (editáveis na aba Configurar)
        public static List<ClassRow> Defaults()
        {
            return new List<ClassRow>
            {
                new ClassRow { Nome = "TUG (seco)",          PotenciaVA = 100,  FP = 1.0 },
                new ClassRow { Nome = "TUG (área molhada)",  PotenciaVA = 600,  FP = 1.0 },
                new ClassRow { Nome = "Chuveiro",            PotenciaVA = 5400, FP = 1.0 },
                new ClassRow { Nome = "Torneira elétrica",   PotenciaVA = 4500, FP = 1.0 },
                new ClassRow { Nome = "Geladeira",           PotenciaVA = 200,  FP = 0.8 },
                new ClassRow { Nome = "Micro-ondas",         PotenciaVA = 1400, FP = 1.0 },
                new ClassRow { Nome = "Máquina de lavar",    PotenciaVA = 1000, FP = 1.0 },
                new ClassRow { Nome = "Ar-condicionado",     PotenciaVA = 1500, FP = 0.9 },
                new ClassRow { Nome = "Iluminação",          PotenciaVA = 100,  FP = 1.0 },
            };
        }
    }

    // ==========================================
    // EVENT HANDLER (executa no contexto da API)
    // ==========================================
    public class ClassificarCargasHandler : IExternalEventHandler
    {
        public ClassRow Classificacao { get; set; }     // classificação a aplicar
        public Action<string> OnDone { get; set; }        // callback para a janela (status)

        public void Execute(UIApplication app)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc == null) { Avisar("Nenhum documento ativo."); return; }
            Document doc = uidoc.Document;

            List<ElementId> selIds = uidoc.Selection.GetElementIds().ToList();
            if (selIds.Count == 0)
            {
                Avisar("Nenhuma família selecionada. Selecione no Revit e clique de novo.");
                return;
            }

            if (Classificacao == null || string.IsNullOrWhiteSpace(Classificacao.Nome))
            {
                Avisar("Classificação inválida.");
                return;
            }

            int ok = 0, ign = 0;
            try
            {
                using (Transaction t = new Transaction(doc, "Aegia: Aplicar Classificação de Carga"))
                {
                    t.Start();

                    ElementId lcId = ObterOuCriarLoadClassification(doc, Classificacao.Nome.Trim());

                    foreach (ElementId id in selIds)
                    {
                        FamilyInstance fi = doc.GetElement(id) as FamilyInstance;
                        if (fi == null || !fi.IsValidObject) { ign++; continue; }

                        int? posOpt = LerInt(fi, "pos");
                        if (posOpt == null) { ign++; continue; }
                        int pos = posOpt.Value;

                        string nomePat = "zpat" + pos;
                        string nomeFP = "zFP" + pos;
                        string nomeTipo = "zTipo de Carga " + pos;

                        FamilyInstance pai = AcharPaiComParam(fi, nomePat);
                        if (pai == null) { ign++; continue; }

                        SetPotencia(pai, nomePat, Classificacao.PotenciaVA);
                        SetNumero(pai, nomeFP, Classificacao.FP);
                        SetLoadClass(pai, nomeTipo, lcId);
                        ok++;
                    }

                    t.Commit();
                }
            }
            catch (Exception ex)
            {
                Avisar("Falha: " + ex.Message);
                return;
            }

            Avisar(string.Format("Classificação \"{0}\" aplicada.  Aplicados: {1} | Ignorados (sem 'pos'/pai): {2}",
                Classificacao.Nome, ok, ign));
        }

        public string GetName() => "Aegia Classificar Cargas Handler";

        private void Avisar(string msg)
        {
            if (OnDone != null) OnDone(msg);
        }

        // ---------- HELPERS DE PARÂMETRO ----------
        private static int? LerInt(FamilyInstance fi, string nome)
        {
            Parameter p = fi.LookupParameter(nome);
            if (p == null || !p.HasValue) return null;
            if (p.StorageType == StorageType.Integer) return p.AsInteger();
            if (p.StorageType == StorageType.Double) return (int)Math.Round(p.AsDouble());
            if (p.StorageType == StorageType.String)
            {
                int v;
                if (int.TryParse((p.AsString() ?? "").Trim(), out v)) return v;
            }
            return null;
        }

        // Sobe via SuperComponent até o primeiro ancestral que tenha o parâmetro alvo
        private static FamilyInstance AcharPaiComParam(FamilyInstance fi, string nomeParam)
        {
            FamilyInstance atual = fi.SuperComponent as FamilyInstance;
            while (atual != null && atual.IsValidObject)
            {
                if (atual.LookupParameter(nomeParam) != null) return atual;
                atual = atual.SuperComponent as FamilyInstance;
            }
            return null;
        }

        private static void SetPotencia(FamilyInstance el, string nome, double va)
        {
            Parameter p = el.LookupParameter(nome);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) return;
            double interno;
            try { interno = UnitUtils.ConvertToInternalUnits(va, p.GetUnitTypeId()); }
            catch { interno = va; }
            p.Set(interno);
        }

        private static void SetNumero(FamilyInstance el, string nome, double d)
        {
            Parameter p = el.LookupParameter(nome);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) return;
            p.Set(d);
        }

        private static void SetLoadClass(FamilyInstance el, string nome, ElementId lcId)
        {
            if (lcId == ElementId.InvalidElementId) return;
            Parameter p = el.LookupParameter(nome);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.ElementId) return;
            p.Set(lcId);
        }

        // Busca a Load Classification (elemento ElectricalLoadClassification) por nome;
        // cria com fator de demanda padrão se não existir.
        private static ElementId ObterOuCriarLoadClassification(Document doc, string nome)
        {
            try
            {
                ElectricalLoadClassification existente = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalLoadClassification))
                    .Cast<ElectricalLoadClassification>()
                    .FirstOrDefault(l => string.Equals(l.Name, nome, StringComparison.OrdinalIgnoreCase));
                if (existente != null) return existente.Id;

                ElectricalLoadClassification nova = ElectricalLoadClassification.Create(doc, nome);
                if (nova != null) return nova.Id;
            }
            catch { }
            return ElementId.InvalidElementId;
        }
    }

    // ==========================================
    // JANELA PRINCIPAL (modeless, permanece aberta)
    // ==========================================
    public class ClassificarCargasForm : WWindow
    {
        private readonly ClassificarCargasHandler handler;
        private readonly ExternalEvent exEvent;
        private readonly ObservableCollection<ClassRow> rows;
        private WStackPanel pnlBotoes;
        private WLabel lblStatus;

        public ClassificarCargasForm(ClassificarCargasHandler h, ExternalEvent ev)
        {
            handler = h;
            exEvent = ev;
            rows = new ObservableCollection<ClassRow>(ConfigCargas.Carregar());

            handler.OnDone = (msg) =>
            {
                Dispatcher.Invoke(new Action(() =>
                {
                    if (lblStatus != null) lblStatus.Content = msg;
                }));
            };

            Title = "Classificar Cargas";
            Width = 520; Height = 600;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            ResizeMode = System.Windows.ResizeMode.CanResize;
            Topmost = true;

            WTabControl tabs = new WTabControl() { Margin = new WThickness(8) };
            tabs.Items.Add(MontarAbaClassificar());
            tabs.Items.Add(MontarAbaConfigurar());
            Content = tabs;
        }

        // ---------- ABA 1: CLASSIFICAR ----------
        private WTabItem MontarAbaClassificar()
        {
            WTabItem tab = new WTabItem() { Header = "Classificar", Background = WBrushes.White };

            WGrid grid = new WGrid();
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition() { Height = System.Windows.GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition() { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition() { Height = System.Windows.GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition() { Height = System.Windows.GridLength.Auto });

            WLabel lblInfo = new WLabel()
            {
                Content = "Selecione as famílias no Revit e clique numa classificação. A janela permanece aberta.",
                FontWeight = System.Windows.FontWeights.Bold,
                Margin = new WThickness(10, 10, 10, 5)
            };
            System.Windows.Controls.Grid.SetRow(lblInfo, 0);

            WScrollViewer scroll = new WScrollViewer()
            {
                Margin = new WThickness(10, 0, 10, 5),
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto
            };
            System.Windows.Controls.Grid.SetRow(scroll, 1);
            pnlBotoes = new WStackPanel();
            scroll.Content = pnlBotoes;

            lblStatus = new WLabel()
            {
                Content = "Pronto.",
                Margin = new WThickness(10, 0, 10, 5),
                Foreground = WBrushes.DimGray
            };
            System.Windows.Controls.Grid.SetRow(lblStatus, 2);

            WButton btnFechar = new WButton()
            {
                Content = "Fechar",
                Height = 34,
                Margin = new WThickness(10, 0, 10, 10)
            };
            btnFechar.Click += (s, e) => Close();
            System.Windows.Controls.Grid.SetRow(btnFechar, 3);

            grid.Children.Add(lblInfo);
            grid.Children.Add(scroll);
            grid.Children.Add(lblStatus);
            grid.Children.Add(btnFechar);
            tab.Content = grid;

            ReconstruirBotoes();
            return tab;
        }

        private void ReconstruirBotoes()
        {
            if (pnlBotoes == null) return;
            pnlBotoes.Children.Clear();
            foreach (ClassRow r in rows)
            {
                ClassRow alvo = r; // captura para a closure
                WButton btn = new WButton()
                {
                    Content = string.Format("{0}    ({1} VA · FP {2})",
                        alvo.Nome,
                        alvo.PotenciaVA.ToString("0.##", CultureInfo.InvariantCulture),
                        alvo.FP.ToString("0.##", CultureInfo.InvariantCulture)),
                    Height = 38,
                    Margin = new WThickness(0, 3, 0, 3),
                    HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                    Padding = new WThickness(10, 0, 0, 0),
                    Background = new System.Windows.Media.SolidColorBrush(WColor.FromRgb(225, 240, 255))
                };
                btn.Click += (s, e) =>
                {
                    handler.Classificacao = alvo;
                    exEvent.Raise();
                };
                pnlBotoes.Children.Add(btn);
            }
        }

        // ---------- ABA 2: CONFIGURAR (NBR) ----------
        private WTabItem MontarAbaConfigurar()
        {
            WTabItem tab = new WTabItem() { Header = "Configurar (NBR)", Background = WBrushes.White };

            WGrid grid = new WGrid();
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition() { Height = System.Windows.GridLength.Auto });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition() { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition() { Height = System.Windows.GridLength.Auto });

            WLabel lbl = new WLabel()
            {
                Content = "Cadastre/edite classificações. Para remover, selecione a linha e clique \"Remover selecionada\". Nome igual ao da Load Classification.",
                Margin = new WThickness(10, 10, 10, 5),
                Foreground = WBrushes.DimGray
            };
            System.Windows.Controls.Grid.SetRow(lbl, 0);

            WDataGrid dg = new WDataGrid()
            {
                Margin = new WThickness(10, 0, 10, 5),
                AutoGenerateColumns = false,
                CanUserAddRows = true,
                CanUserDeleteRows = true,
                HeadersVisibility = System.Windows.Controls.DataGridHeadersVisibility.Column,
                Background = WBrushes.White,
                ItemsSource = rows
            };
            dg.Columns.Add(new WDataGridTextColumn()
            {
                Header = "Classificação",
                Binding = new System.Windows.Data.Binding("Nome"),
                Width = new System.Windows.Controls.DataGridLength(1, System.Windows.Controls.DataGridLengthUnitType.Star)
            });
            dg.Columns.Add(new WDataGridTextColumn()
            {
                Header = "Potência (VA)",
                Binding = new System.Windows.Data.Binding("PotenciaVA") { StringFormat = "0.##" },
                Width = new System.Windows.Controls.DataGridLength(110)
            });
            dg.Columns.Add(new WDataGridTextColumn()
            {
                Header = "FP",
                Binding = new System.Windows.Data.Binding("FP") { StringFormat = "0.##" },
                Width = new System.Windows.Controls.DataGridLength(70)
            });
            System.Windows.Controls.Grid.SetRow(dg, 1);

            WButton btnRemover = new WButton()
            {
                Content = "Remover selecionada",
                Height = 42, MinWidth = 160,
                Margin = new WThickness(10, 5, 5, 10),
                FontWeight = System.Windows.FontWeights.Bold,
                Background = new System.Windows.Media.SolidColorBrush(WColor.FromRgb(255, 215, 215))
            };
            btnRemover.Click += (s, e) =>
            {
                ClassRow sel = dg.SelectedItem as ClassRow;
                if (sel == null)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Classificar Cargas",
                        "Selecione uma classificação na lista para remover.");
                    return;
                }
                rows.Remove(sel);
                ConfigCargas.Salvar(rows);
                ReconstruirBotoes();
            };

            WButton btnSalvar = new WButton()
            {
                Content = "SALVAR E ATUALIZAR BOTÕES",
                Height = 42, MinWidth = 230,
                Margin = new WThickness(5, 5, 10, 10),
                FontWeight = System.Windows.FontWeights.Bold,
                Background = new System.Windows.Media.SolidColorBrush(WColor.FromRgb(91, 204, 46)),
                Foreground = WBrushes.White
            };
            btnSalvar.Click += (s, e) =>
            {
                // commit de edição pendente do DataGrid (célula e depois linha)
                dg.CommitEdit();
                dg.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
                ConfigCargas.Salvar(rows);
                ReconstruirBotoes();
                Autodesk.Revit.UI.TaskDialog.Show("Classificar Cargas", "Configuração salva.");
            };

            WStackPanel pnlBtns = new WStackPanel()
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            pnlBtns.Children.Add(btnRemover);
            pnlBtns.Children.Add(btnSalvar);
            System.Windows.Controls.Grid.SetRow(pnlBtns, 2);

            grid.Children.Add(lbl);
            grid.Children.Add(dg);
            grid.Children.Add(pnlBtns);
            tab.Content = grid;
            return tab;
        }
    }
}
