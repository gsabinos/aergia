// Aegia — Vincular Modelos
// Janela WPF para vincular VÁRIOS arquivos de uma vez:
//   .rvt            -> RevitLinkType.Create + RevitLinkInstance.Create
//   .ifc            -> Application.OpenIFCDocument gera cache .rvt; vincula o cache
//   .nwc / .nwd     -> Modelo de coordenação (API limitada — ver E304)
//
// Cada arquivo tem seu próprio método de inserção:
//   Origem -> Origem            (padrão da API, sem ajuste)
//   Centro -> Centro            (move pelo delta dos centros de bounding box)
//   Coordenadas compartilhadas  (ProjectLocation ativa do arquivo vinculado)
//   Por site compartilhado      (escolhe o site quando há mais de um)
//
// Códigos de erro:
//   E301 - Nenhum arquivo selecionado.
//   E302 - Falha ao vincular um RVT.
//   E303 - Falha ao gerar o cache .rvt do IFC.
//   E304 - Modelo de coordenação não suportado pela API nesta versão
//          (vínculo deve ser feito pelo diálogo nativo do Revit).
//   E305 - NWF não suportado para vínculo (sem geometria; usar
//          .nwd/.nwc ou os modelos de origem).
//   E307 - Exceção não tratada no comando.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using WWindow = System.Windows.Window;
using WButton = System.Windows.Controls.Button;
using WLabel = System.Windows.Controls.Label;
using WComboBox = System.Windows.Controls.ComboBox;
using WGrid = System.Windows.Controls.Grid;
using WStackPanel = System.Windows.Controls.StackPanel;
using WWrapPanel = System.Windows.Controls.WrapPanel;
using WDataGrid = System.Windows.Controls.DataGrid;
using WDataGridTextColumn = System.Windows.Controls.DataGridTextColumn;
using WDataGridComboBoxColumn = System.Windows.Controls.DataGridComboBoxColumn;
using WThickness = System.Windows.Thickness;
using WOrientation = System.Windows.Controls.Orientation;
using WListBox = System.Windows.Controls.ListBox;

namespace Aegia_VincularModelos
{
    internal static class Metodos
    {
        public const string OrigemOrigem = "Origem → Origem";
        public const string CentroCentro = "Centro → Centro";
        public const string Compartilhadas = "Coordenadas compartilhadas";
        public const string PorSite = "Por site compartilhado";

        public static readonly string[] Todos =
            { OrigemOrigem, CentroCentro, Compartilhadas, PorSite };
    }

    public class LinkItem : INotifyPropertyChanged
    {
        private string _metodo = Metodos.OrigemOrigem;

        public string Arquivo { get; set; }
        public string Nome { get { return Path.GetFileName(Arquivo); } }
        public string Tipo { get; set; }

        public string Metodo
        {
            get { return _metodo; }
            set { _metodo = value; Notificar("Metodo"); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Notificar(string p)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs(p));
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class VincularModelosCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument.Document;

            try
            {
                VincularModelosWindow win = new VincularModelosWindow(doc.PathName);
                new System.Windows.Interop.WindowInteropHelper(win)
                    { Owner = uiapp.MainWindowHandle };
                bool? ok = win.ShowDialog();
                if (ok != true || win.Itens.Count == 0)
                    return Result.Cancelled;

                StringBuilder log = new StringBuilder();
                int sucesso = 0, falha = 0;

                foreach (LinkItem item in win.Itens)
                {
                    string ext = Path.GetExtension(item.Arquivo).ToLowerInvariant();
                    try
                    {
                        if (ext == ".rvt")
                        {
                            VincularRevit(doc, item.Arquivo, item.Arquivo, item.Metodo);
                            log.AppendLine("OK  (RVT)  " + item.Nome);
                            sucesso++;
                        }
                        else if (ext == ".ifc")
                        {
                            string cache = GerarCacheIfc(uiapp, item.Arquivo);
                            VincularRevit(doc, cache, item.Arquivo, item.Metodo);
                            log.AppendLine("OK  (IFC)  " + item.Nome);
                            sucesso++;
                        }
                        else if (ext == ".nwc" || ext == ".nwd")
                        {
                            VincularCoordenacao(uiapp, doc, item.Arquivo);
                            log.AppendLine("-- (COORD) " + item.Nome +
                                " — inserir pelo diálogo nativo (API limitada) [E304]");
                            falha++;
                        }
                        else if (ext == ".nwf")
                        {
                            log.AppendLine("-- (NWF)   " + item.Nome +
                                " — NWF não contém geometria; use o .nwd/.nwc ou os " +
                                "modelos de origem [E305]");
                            falha++;
                        }
                        else
                        {
                            log.AppendLine("?? " + item.Nome + " — extensão não suportada");
                            falha++;
                        }
                    }
                    catch (Exception exItem)
                    {
                        log.AppendLine("ERRO " + item.Nome + " — " + exItem.Message);
                        falha++;
                    }
                }

                if (falha == 0)
                {
                    Autodesk.Revit.UI.TaskDialog.Show("Aegia — Vincular Modelos",
                        sucesso + " modelo(s) vinculado(s).");
                }
                else
                {
                    Autodesk.Revit.UI.TaskDialog td =
                        new Autodesk.Revit.UI.TaskDialog("Aegia — Vincular Modelos");
                    td.MainInstruction = string.Format(
                        "{0} vinculado(s), {1} com problema.", sucesso, falha);
                    td.MainContent = log.ToString();
                    td.CommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons.Close;
                    td.AddCommandLink(
                        Autodesk.Revit.UI.TaskDialogCommandLinkId.CommandLink1,
                        "Salvar log de erros…");
                    if (td.Show() == Autodesk.Revit.UI.TaskDialogResult.CommandLink1)
                        SalvarLog(log.ToString());
                }

                return falha > 0 && sucesso == 0 ? Result.Failed : Result.Succeeded;
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aegia [E307]", ex.Message);
                return Result.Failed;
            }
        }

        // ---------- RVT (também usado pelo cache do IFC) ----------

        private static void VincularRevit(Document doc, string caminho, string origem, string metodo)
        {
            if (!File.Exists(caminho))
                throw new Exception("Arquivo não encontrado: " + caminho);

            ModelPath mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(caminho);
            RevitLinkOptions opt = new RevitLinkOptions(false);

            using (Transaction t = new Transaction(doc, "Aegia: Vincular " + Path.GetFileName(origem)))
            {
                t.Start();

                LinkLoadResult res;
                try
                {
                    res = RevitLinkType.Create(doc, mp, opt);
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    throw new Exception("[E302] Falha ao criar o vínculo de '" +
                        Path.GetFileName(origem) + "': " + ex.Message);
                }

                if (res == null || res.ElementId == ElementId.InvalidElementId)
                {
                    t.RollBack();
                    throw new Exception("[E302] Revit não retornou o tipo de vínculo de '" +
                        Path.GetFileName(origem) + "'.");
                }

                RevitLinkInstance inst = RevitLinkInstance.Create(doc, res.ElementId);
                AplicarMetodo(doc, inst, metodo);
                inst.Pinned = true; // trava o link após posicionar
                t.Commit();
            }
        }

        private static void AplicarMetodo(Document doc, RevitLinkInstance inst, string metodo)
        {
            if (metodo == Metodos.OrigemOrigem)
                return; // RevitLinkInstance.Create já coloca origem → origem

            if (metodo == Metodos.CentroCentro)
            {
                XYZ delta = DeltaCentroCentro(doc, inst);
                if (delta != null && !delta.IsZeroLength())
                    ElementTransformUtils.MoveElement(doc, inst.Id, delta);
                return;
            }

            // Coordenadas compartilhadas / Por site compartilhado
            Document linkDoc = inst.GetLinkDocument();
            if (linkDoc == null)
                return; // link não carregado — mantém origem → origem

            ProjectLocation locLink = linkDoc.ActiveProjectLocation;

            if (metodo == Metodos.PorSite)
            {
                List<ProjectLocation> sites = new List<ProjectLocation>();
                foreach (ProjectLocation pl in linkDoc.ProjectLocations)
                    sites.Add(pl);

                if (sites.Count > 1)
                {
                    SitePickerDialog sp = new SitePickerDialog(
                        sites.Select(s => s.Name).ToList());
                    if (sp.ShowDialog() == true && sp.IndiceEscolhido >= 0)
                        locLink = sites[sp.IndiceEscolhido];
                }
            }

            // GetTotalTransform() converte SHARED -> INTERNAL (em cada doc).
            // Para o ponto P do link (interno) cair no host com as mesmas
            // coords compartilhadas: Q = H.OfPoint( L.Inverse.OfPoint(P) ),
            // ou seja, transform do instance = H * L⁻¹.
            Transform H = doc.ActiveProjectLocation.GetTotalTransform();
            Transform L = locLink.GetTotalTransform();
            Transform alvo = H.Multiply(L.Inverse);
            AplicarTransform(doc, inst.Id, alvo);
        }

        // Diferença de coordenadas compartilhadas é sempre rotação em torno
        // da vertical + translação (sem inclinação), então decompomos assim.
        private static void AplicarTransform(Document doc, ElementId id, Transform tf)
        {
            double ang = Math.Atan2(tf.BasisX.Y, tf.BasisX.X);
            if (Math.Abs(ang) > 1e-9)
                ElementTransformUtils.RotateElement(doc, id,
                    Line.CreateBound(XYZ.Zero, XYZ.Zero + XYZ.BasisZ), ang);
            if (!tf.Origin.IsZeroLength())
                ElementTransformUtils.MoveElement(doc, id, tf.Origin);
        }

        private static XYZ DeltaCentroCentro(Document doc, RevitLinkInstance inst)
        {
            BoundingBoxXYZ bbLink = inst.get_BoundingBox(null);
            if (bbLink == null) return null;

            BoundingBoxXYZ bbHost = BBoxModelo(doc, inst.Id);
            if (bbHost == null) return null;

            XYZ cLink = (bbLink.Min + bbLink.Max) * 0.5;
            XYZ cHost = (bbHost.Min + bbHost.Max) * 0.5;
            return cHost - cLink;
        }

        // União dos bounding boxes do modelo host, ignorando vínculos.
        private static BoundingBoxXYZ BBoxModelo(Document doc, ElementId ignorar)
        {
            XYZ min = null, max = null;
            FilteredElementCollector col = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType();

            foreach (Element el in col)
            {
                try
                {
                    if (el.Id == ignorar) continue;
                    if (el is RevitLinkInstance) continue;
                    BoundingBoxXYZ bb = el.get_BoundingBox(null);
                    if (bb == null) continue;

                    if (min == null) { min = bb.Min; max = bb.Max; }
                    else
                    {
                        min = new XYZ(Math.Min(min.X, bb.Min.X),
                                      Math.Min(min.Y, bb.Min.Y),
                                      Math.Min(min.Z, bb.Min.Z));
                        max = new XYZ(Math.Max(max.X, bb.Max.X),
                                      Math.Max(max.Y, bb.Max.Y),
                                      Math.Max(max.Z, bb.Max.Z));
                    }
                }
                catch { }
            }

            if (min == null) return null;
            BoundingBoxXYZ res = new BoundingBoxXYZ();
            res.Min = min; res.Max = max;
            return res;
        }

        // ---------- IFC ----------

        private static string GerarCacheIfc(UIApplication uiapp, string ifcPath)
        {
            try
            {
                Document ifcDoc = uiapp.Application.OpenIFCDocument(ifcPath);
                if (ifcDoc == null)
                    throw new Exception("OpenIFCDocument retornou nulo.");

                string rvtCache = ifcDoc.PathName;
                ifcDoc.Close(false);

                if (string.IsNullOrEmpty(rvtCache) || !File.Exists(rvtCache))
                {
                    // Convenção do Revit: cache ao lado do .ifc
                    string alt = Path.ChangeExtension(ifcPath, ".rvt");
                    if (File.Exists(alt)) return alt;
                    throw new Exception("Cache .rvt do IFC não foi localizado.");
                }
                return rvtCache;
            }
            catch (Exception ex)
            {
                throw new Exception("[E303] Falha ao gerar cache do IFC '" +
                    Path.GetFileName(ifcPath) + "': " + ex.Message);
            }
        }

        // ---------- Coordenação (.nwc / .nwd) ----------

        private static void VincularCoordenacao(UIApplication uiapp, Document doc, string caminho)
        {
            // A API pública do Revit (2024-2027) não expõe inserção de
            // Coordination Model. Abrimos o diálogo nativo de Inserir.
            try
            {
                foreach (string nome in new[] { "LinkCoordinationModel", "CoordinationModel" })
                {
                    if (Enum.IsDefined(typeof(PostableCommand), nome))
                    {
                        PostableCommand pc = (PostableCommand)Enum.Parse(typeof(PostableCommand), nome);
                        RevitCommandId cmd = RevitCommandId.LookupPostableCommandId(pc);
                        if (cmd != null && uiapp.CanPostCommand(cmd))
                        {
                            uiapp.PostCommand(cmd);
                            return;
                        }
                    }
                }
            }
            catch { }
            // Sem fallback disponível — apenas registrado no resumo (E304).
        }

        // ---------- Log de erros ----------

        private static void SalvarLog(string conteudo)
        {
            try
            {
                Microsoft.Win32.SaveFileDialog sfd = new Microsoft.Win32.SaveFileDialog()
                {
                    Title = "Salvar log de erros",
                    FileName = "aegia_vinculos_log.txt",
                    Filter = "Texto (*.txt)|*.txt|Todos (*.*)|*.*"
                };
                if (sfd.ShowDialog() != true) return;
                File.WriteAllText(sfd.FileName, conteudo);
            }
            catch (Exception ex)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Aegia",
                    "Não foi possível salvar o log: " + ex.Message);
            }
        }
    }

    // ---------- Janela principal ----------

    public class VincularModelosWindow : WWindow
    {
        public ObservableCollection<LinkItem> Itens { get; private set; }
            = new ObservableCollection<LinkItem>();

        private WComboBox cmbGlobal;
        private WDataGrid grid;
        private readonly string hostPath;

        public VincularModelosWindow(string hostPath)
        {
            this.hostPath = hostPath ?? "";
            Title = "Aegia — Vincular Modelos";
            Width = 900; Height = 460;
            MinWidth = 520; MinHeight = 360;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

            WGrid raiz = new WGrid() { Margin = new WThickness(10) };
            raiz.RowDefinitions.Add(new System.Windows.Controls.RowDefinition()
                { Height = System.Windows.GridLength.Auto });
            raiz.RowDefinitions.Add(new System.Windows.Controls.RowDefinition());
            raiz.RowDefinitions.Add(new System.Windows.Controls.RowDefinition()
                { Height = System.Windows.GridLength.Auto });
            Content = raiz;

            // Barra superior
            WWrapPanel topo = new WWrapPanel()
                { Margin = new WThickness(0, 0, 0, 8) };

            WButton btnAdd = new WButton()
                { Content = "Adicionar arquivos…", Width = 150, Height = 26 };
            btnAdd.Click += (s, e) => AdicionarArquivos();
            topo.Children.Add(btnAdd);

            WButton btnRem = new WButton()
                { Content = "Remover", Width = 90, Height = 26, Margin = new WThickness(8, 0, 0, 0) };
            btnRem.Click += (s, e) => RemoverSelecionados();
            topo.Children.Add(btnRem);

            topo.Children.Add(new WLabel()
                { Content = "Método p/ todos:", Margin = new WThickness(20, 0, 4, 0) });
            cmbGlobal = new WComboBox() { Width = 200, Height = 26 };
            foreach (string m in Metodos.Todos) cmbGlobal.Items.Add(m);
            cmbGlobal.SelectedIndex = 0;
            topo.Children.Add(cmbGlobal);

            WButton btnAplicar = new WButton()
                { Content = "Aplicar a todos", Width = 110, Height = 26, Margin = new WThickness(8, 0, 0, 0) };
            btnAplicar.Click += (s, e) =>
            {
                string m = cmbGlobal.SelectedItem as string;
                foreach (LinkItem it in Itens) it.Metodo = m;
            };
            topo.Children.Add(btnAplicar);

            WGrid.SetRow(topo, 0);
            raiz.Children.Add(topo);

            // DataGrid
            grid = new WDataGrid()
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                ItemsSource = Itens,
                SelectionMode = System.Windows.Controls.DataGridSelectionMode.Extended
            };
            grid.Columns.Add(new WDataGridTextColumn()
            {
                Header = "Arquivo",
                Binding = new System.Windows.Data.Binding("Nome"),
                IsReadOnly = true,
                Width = new System.Windows.Controls.DataGridLength(1,
                    System.Windows.Controls.DataGridLengthUnitType.Star)
            });
            grid.Columns.Add(new WDataGridTextColumn()
            {
                Header = "Tipo",
                Binding = new System.Windows.Data.Binding("Tipo"),
                IsReadOnly = true,
                Width = new System.Windows.Controls.DataGridLength(70)
            });
            WDataGridComboBoxColumn colMet = new WDataGridComboBoxColumn()
            {
                Header = "Método de inserção",
                ItemsSource = Metodos.Todos,
                SelectedItemBinding = new System.Windows.Data.Binding("Metodo")
                    { Mode = System.Windows.Data.BindingMode.TwoWay },
                Width = new System.Windows.Controls.DataGridLength(220)
            };
            grid.Columns.Add(colMet);

            WGrid.SetRow(grid, 1);
            raiz.Children.Add(grid);

            // Rodapé: memória à esquerda, Vincular/Cancelar à direita
            WGrid rodape = new WGrid() { Margin = new WThickness(0, 8, 0, 0) };

            WStackPanel rodEsq = new WStackPanel()
            {
                Orientation = WOrientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left
            };
            WButton btnSalvarMem = new WButton()
                { Content = "Salvar memória", Width = 120, Height = 28 };
            btnSalvarMem.Click += (s, e) => SalvarMemoria();
            rodEsq.Children.Add(btnSalvarMem);

            WButton btnCarregarMem = new WButton()
            {
                Content = "Carregar memória", Width = 130, Height = 28,
                Margin = new WThickness(8, 0, 0, 0)
            };
            btnCarregarMem.Click += (s, e) => CarregarMemoria();
            rodEsq.Children.Add(btnCarregarMem);
            rodape.Children.Add(rodEsq);

            WStackPanel rodDir = new WStackPanel()
            {
                Orientation = WOrientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            WButton btnOk = new WButton()
                { Content = "Vincular", Width = 100, Height = 28 };
            btnOk.Click += (s, e) =>
            {
                if (Itens.Count == 0)
                {
                    System.Windows.MessageBox.Show(this,
                        "Nenhum arquivo selecionado.", "Aergia [E301]");
                    return;
                }
                DialogResult = true;
                Close();
            };
            rodDir.Children.Add(btnOk);

            WButton btnCancel = new WButton()
            {
                Content = "Cancelar", Width = 100, Height = 28,
                Margin = new WThickness(8, 0, 0, 0), IsCancel = true
            };
            rodDir.Children.Add(btnCancel);
            rodape.Children.Add(rodDir);

            WGrid.SetRow(rodape, 2);
            raiz.Children.Add(rodape);
        }

        private void AdicionarArquivos()
        {
            Microsoft.Win32.OpenFileDialog ofd = new Microsoft.Win32.OpenFileDialog()
            {
                Multiselect = true,
                Filter = "Modelos vinculáveis (*.rvt;*.ifc;*.nwc;*.nwd)|*.rvt;*.ifc;*.nwc;*.nwd|" +
                         "Revit (*.rvt)|*.rvt|IFC (*.ifc)|*.ifc|" +
                         "Navisworks (*.nwc;*.nwd)|*.nwc;*.nwd|Todos (*.*)|*.*"
            };
            if (ofd.ShowDialog(this) != true) return;

            string mGlobal = cmbGlobal.SelectedItem as string;
            foreach (string f in ofd.FileNames)
            {
                if (Itens.Any(x => string.Equals(x.Arquivo, f,
                        StringComparison.OrdinalIgnoreCase)))
                    continue;
                Itens.Add(new LinkItem()
                {
                    Arquivo = f,
                    Tipo = TipoPorExtensao(f),
                    Metodo = mGlobal
                });
            }
        }

        private static string TipoPorExtensao(string f)
        {
            string e = Path.GetExtension(f).ToLowerInvariant();
            if (e == ".rvt") return "RVT";
            if (e == ".ifc") return "IFC";
            if (e == ".nwc" || e == ".nwd") return "COORD";
            if (e == ".nwf") return "NWF";
            return "?";
        }

        private void RemoverSelecionados()
        {
            List<LinkItem> sel = grid.SelectedItems.Cast<LinkItem>().ToList();
            foreach (LinkItem it in sel) Itens.Remove(it);
        }

        private bool EhProjetoAberto(string caminho)
        {
            return !string.IsNullOrEmpty(hostPath) &&
                   string.Equals(caminho, hostPath, StringComparison.OrdinalIgnoreCase);
        }

        // Lembra o caminho da última memória salva (AppData, texto puro).
        private static string ConfigPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AergiaVinculos.txt");
        }

        private static string LerUltimaMemoria()
        {
            try
            {
                string p = ConfigPath();
                return File.Exists(p) ? File.ReadAllText(p).Trim() : "";
            }
            catch { return ""; }
        }

        private static void GravarUltimaMemoria(string caminho)
        {
            try { File.WriteAllText(ConfigPath(), caminho ?? ""); }
            catch { }
        }

        // Formato: uma linha por item -> "caminho|metodo"
        private void SalvarMemoria()
        {
            Microsoft.Win32.SaveFileDialog sfd = new Microsoft.Win32.SaveFileDialog()
            {
                Title = "Salvar memória de vínculos",
                FileName = "vinculos.aergia",
                Filter = "Memória Aergia (*.aergia)|*.aergia|Texto (*.txt)|*.txt"
            };

            string ultima = LerUltimaMemoria();
            if (!string.IsNullOrEmpty(ultima))
            {
                try
                {
                    string dir = Path.GetDirectoryName(ultima);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        sfd.InitialDirectory = dir;
                    string nome = Path.GetFileName(ultima);
                    if (!string.IsNullOrEmpty(nome)) sfd.FileName = nome;
                }
                catch { }
            }

            if (sfd.ShowDialog(this) != true) return;

            try
            {
                List<string> linhas = new List<string>();
                foreach (LinkItem it in Itens)
                {
                    if (EhProjetoAberto(it.Arquivo)) continue; // ignora o projeto aberto
                    linhas.Add(it.Arquivo + "|" + it.Metodo);
                }
                File.WriteAllLines(sfd.FileName, linhas);
                GravarUltimaMemoria(sfd.FileName);
                System.Windows.MessageBox.Show(this,
                    linhas.Count + " item(ns) salvo(s) na memória.", "Aergia");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this,
                    "Não foi possível salvar a memória: " + ex.Message, "Aergia");
            }
        }

        private void CarregarMemoria()
        {
            Microsoft.Win32.OpenFileDialog ofd = new Microsoft.Win32.OpenFileDialog()
            {
                Title = "Carregar memória de vínculos",
                Filter = "Memória Aergia (*.aergia)|*.aergia|Texto (*.txt)|*.txt|Todos (*.*)|*.*"
            };

            string ultima = LerUltimaMemoria();
            if (!string.IsNullOrEmpty(ultima))
            {
                try
                {
                    string dir = Path.GetDirectoryName(ultima);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        ofd.InitialDirectory = dir;
                }
                catch { }
            }

            if (ofd.ShowDialog(this) != true) return;

            try
            {
                int add = 0;
                foreach (string linha in File.ReadAllLines(ofd.FileName))
                {
                    if (string.IsNullOrWhiteSpace(linha)) continue;
                    int sep = linha.LastIndexOf('|');
                    string caminho = sep >= 0 ? linha.Substring(0, sep) : linha.Trim();
                    string metodo = sep >= 0 ? linha.Substring(sep + 1).Trim() : Metodos.OrigemOrigem;
                    if (string.IsNullOrEmpty(caminho)) continue;
                    if (EhProjetoAberto(caminho)) continue; // ignora o projeto aberto
                    if (Itens.Any(x => string.Equals(x.Arquivo, caminho,
                            StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (!Metodos.Todos.Contains(metodo)) metodo = Metodos.OrigemOrigem;

                    Itens.Add(new LinkItem()
                    {
                        Arquivo = caminho,
                        Tipo = TipoPorExtensao(caminho),
                        Metodo = metodo
                    });
                    add++;
                }
                System.Windows.MessageBox.Show(this,
                    add + " item(ns) carregado(s) da memória.", "Aergia");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this,
                    "Não foi possível carregar a memória: " + ex.Message, "Aergia");
            }
        }
    }

    // ---------- Seletor de site compartilhado ----------

    public class SitePickerDialog : WWindow
    {
        public int IndiceEscolhido { get; private set; } = -1;
        private WListBox lista;

        public SitePickerDialog(List<string> nomes)
        {
            Title = "Escolher site compartilhado";
            Width = 360; Height = 280;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

            WStackPanel sp = new WStackPanel() { Margin = new WThickness(10) };
            Content = sp;

            sp.Children.Add(new WLabel()
                { Content = "Este arquivo tem vários sites. Escolha um:" });

            lista = new WListBox() { Height = 160 };
            foreach (string n in nomes) lista.Items.Add(n);
            lista.SelectedIndex = 0;
            sp.Children.Add(lista);

            WStackPanel rod = new WStackPanel()
            {
                Orientation = WOrientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new WThickness(0, 10, 0, 0)
            };
            WButton ok = new WButton()
                { Content = "OK", Width = 80, Height = 26, IsDefault = true };
            ok.Click += (s, e) =>
            {
                IndiceEscolhido = lista.SelectedIndex;
                DialogResult = true;
                Close();
            };
            rod.Children.Add(ok);
            WButton cc = new WButton()
            {
                Content = "Cancelar", Width = 80, Height = 26,
                Margin = new WThickness(8, 0, 0, 0), IsCancel = true
            };
            rod.Children.Add(cc);
            sp.Children.Add(rod);
        }
    }
}
