using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;

// Aliases WPF (evita conflito com tipos Revit)
using WWindow = System.Windows.Window;
using WStackPanel = System.Windows.Controls.StackPanel;
using WLabel = System.Windows.Controls.Label;
using WButton = System.Windows.Controls.Button;
using WTextBox = System.Windows.Controls.TextBox;
using WListBox = System.Windows.Controls.ListBox;
using WComboBox = System.Windows.Controls.ComboBox;
using WThickness = System.Windows.Thickness;
using WBrushes = System.Windows.Media.Brushes;
using WOrientation = System.Windows.Controls.Orientation;
using WFontWeights = System.Windows.FontWeights;
using WScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;

namespace Aegia_SwitchID
{
    // =====================================================================
    // COMANDO: lê luminárias + interruptores por ambiente (Room ou MEP Space),
    // agrupa por circuito, define a letra do comando (A-Z, reinicia por
    // circuito, ordem física direita->esquerda / baixo->cima) e abre um
    // SELETOR INTERATIVO para facilitar a criação dos "switch systems":
    //   - clicar num comando seleciona/zooma os elementos no modelo;
    //   - o usuário clica em "Sistema de comando" na faixa do Revit;
    //   - botão para gravar o parâmetro "ID do comando" em todos.
    // Obs.: a API do Revit não cria o switch system formal; só dá pra
    // pré-selecionar os elementos e gravar o ID do comando.
    // =====================================================================
    [Transaction(TransactionMode.Manual)]
    public class SwitchIDCommand : IExternalCommand
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private const int VK_SHIFT = 0x10;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) { Aviso("Nenhum documento ativo."); return Result.Cancelled; }
            Document doc = uidoc.Document;

            // Shift+clique abre a configuração
            bool isShift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            Config cfg = Config.Carregar();
            if (isShift)
            {
                var cfgForm = new ConfigForm(cfg);
                cfgForm.ShowDialog();
                return Result.Succeeded;
            }

            try
            {
                var ambientes = ColetarAmbientes(doc);
                if (ambientes.Count == 0) { Aviso("Nenhum ambiente (Room ou MEP Space) com área foi encontrado no modelo."); return Result.Cancelled; }

                var luminariasTodas = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_LightingFixtures)
                    .WhereElementIsNotElementType().Cast<FamilyInstance>().ToList();
                // Ignora luminárias com Ztipofam = "LEME"
                var luminarias = luminariasTodas.Where(fi => !IgnorarLuminaria(fi)).ToList();
                int lumIgnoradasLeme = luminariasTodas.Count - luminarias.Count;

                var interruptores = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_LightingDevices)
                    .WhereElementIsNotElementType().Cast<FamilyInstance>().ToList();

                if (luminarias.Count == 0) { Aviso("Nenhuma luminária (Lighting Fixtures) encontrada (após filtrar Ztipofam=LEME)."); return Result.Cancelled; }
                if (interruptores.Count == 0) { Aviso("Nenhum interruptor (Lighting Devices) encontrado."); return Result.Cancelled; }

                double tolLumFt = cfg.TolLumM / 0.3048;
                double tolIntFt = cfg.TolIntM / 0.3048;

                int lumSemAmbiente = 0;
                foreach (var fi in luminarias)
                {
                    var amb = AmbienteDoPonto(ambientes, PontoDe(fi), tolLumFt);
                    if (amb != null) amb.Luminarias.Add(fi);
                    else lumSemAmbiente++;
                }
                foreach (var fi in interruptores)
                {
                    var amb = AmbienteDoPonto(ambientes, PontoDe(fi), tolIntFt);
                    if (amb != null) amb.Interruptores.Add(fi);
                }

                var validos = new List<AmbienteInfo>();
                var ignorados = new List<string>();
                foreach (var a in ambientes)
                {
                    if (a.Luminarias.Count > 0 && a.Interruptores.Count > 0) validos.Add(a);
                    else if (a.Luminarias.Count > 0 || a.Interruptores.Count > 0)
                        ignorados.Add(a.Nome + " (" + a.Luminarias.Count + " lum / " + a.Interruptores.Count + " int)");
                }
                if (validos.Count == 0) { Aviso("Nenhum ambiente possui luminárias E interruptor ao mesmo tempo."); return Result.Cancelled; }

                foreach (var a in validos)
                {
                    a.Circuito = CircuitoDominante(a.Luminarias, out a.MisturaCircuito);
                    if (a.Circuito == null) a.Circuito = CircuitoDominante(a.Interruptores, out _);
                    a.CircuitoKey = a.Circuito != null ? a.Circuito.Id.Value : -1L;
                }

                // Agrupa conforme o escopo; ordena fisicamente; rotula (reinicia por grupo)
                var comandos = new List<ComandoItem>();
                foreach (var grupo in validos.GroupBy(a => GrupoChave(a, cfg.Escopo)).OrderBy(g => g.Key))
                {
                    var ordenados = OrdenarFisicamente(grupo.ToList());
                    for (int i = 0; i < ordenados.Count; i++)
                    {
                        var a = ordenados[i];
                        a.Letra = Rotulo(i, cfg.Estilo);
                        comandos.Add(new ComandoItem
                        {
                            Letra = a.Letra,
                            Ambiente = a.Nome,
                            Circuito = a.Circuito != null ? NomeCircuito(a.Circuito) : "(sem circuito)",
                            Interruptores = a.Interruptores.Select(x => x.Id).ToList(),
                            Luminarias = a.Luminarias.Select(x => x.Id).ToList()
                        });
                    }
                }

                int lumNaLista = comandos.Sum(c => c.Luminarias.Count);

                string escTxt = cfg.Escopo == Escopo.Projeto ? "projeto" : (cfg.Escopo == Escopo.Quadro ? "quadro" : "circuito");
                string estTxt = cfg.Estilo == Estilo.Numeros ? "1,2,3" : (cfg.Estilo == Estilo.Maiusculas ? "A,B,C" : "a,b,c");
                string resumo = "Regra: reinicia por " + escTxt + " · estilo " + estTxt +
                                " · tol int " + cfg.TolIntM.ToString("0.00", CultureInfo.InvariantCulture) + "m / lum " +
                                cfg.TolLumM.ToString("0.00", CultureInfo.InvariantCulture) + "m   (Shift+clique = configurar)";

                var handler = new SelHandler();
                var ev = ExternalEvent.Create(handler);
                var form = new SelForm(handler, ev, comandos, ignorados, luminarias.Count, lumNaLista, lumSemAmbiente, lumIgnoradasLeme, resumo);
                handler.Form = form;
                form.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Aviso("Erro: " + ex.Message);
                return Result.Failed;
            }
        }

        // =================================================================
        // Coleta de ambientes + geometria
        // =================================================================
        private static List<AmbienteInfo> ColetarAmbientes(Document doc)
        {
            var lista = new List<AmbienteInfo>();
            // Tenta Rooms; se o modelo não tiver Rooms (ex.: arquitetura em vínculo),
            // usa os MEP Spaces. Ambos herdam de SpatialElement.
            var spatiais = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType().Cast<SpatialElement>().ToList();
            if (spatiais.Count == 0)
                spatiais = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_MEPSpaces)
                    .WhereElementIsNotElementType().Cast<SpatialElement>().ToList();

            foreach (var se in spatiais)
            {
                try
                {
                    if (se.Area <= 0) continue;
                    var loops = ExtrairLoops(se);
                    if (loops.Count == 0) continue;
                    lista.Add(new AmbienteInfo(se, loops));
                }
                catch { }
            }
            return lista;
        }

        private static List<List<XYZ>> ExtrairLoops(SpatialElement se)
        {
            var res = new List<List<XYZ>>();
            try
            {
                var loops = se.GetBoundarySegments(new SpatialElementBoundaryOptions());
                if (loops == null) return res;
                foreach (var loop in loops)
                {
                    var pts = new List<XYZ>();
                    foreach (var seg in loop)
                    {
                        Curve c = seg.GetCurve();
                        if (c == null) continue;
                        foreach (XYZ p in c.Tessellate()) pts.Add(p);
                    }
                    if (pts.Count >= 3) res.Add(pts);
                }
            }
            catch { }
            return res;
        }

        // Ignora luminárias cujo parâmetro Ztipofam == "LEME" (instance ou tipo).
        private static bool IgnorarLuminaria(FamilyInstance fi)
        {
            string v = LerParamTexto(fi, "Ztipofam");
            return !string.IsNullOrEmpty(v) && v.Trim().Equals("LEME", StringComparison.OrdinalIgnoreCase);
        }

        private static string LerParamTexto(FamilyInstance fi, string nome)
        {
            try
            {
                var p = fi.LookupParameter(nome);
                if (p != null && p.HasValue && p.StorageType == StorageType.String)
                {
                    string s = p.AsString();
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                if (fi.Symbol != null)
                {
                    var pt = fi.Symbol.LookupParameter(nome);
                    if (pt != null && pt.HasValue && pt.StorageType == StorageType.String) return pt.AsString();
                }
            }
            catch { }
            return null;
        }

        private static XYZ PontoDe(Element el)
        {
            // 1) ponto de inserção
            try
            {
                var lp = el.Location as LocationPoint;
                if (lp != null && lp.Point != null) return lp.Point;
            }
            catch { }
            // 2) fallback: centro da bounding box (luminárias lineares / hospedadas em face,
            //    sem LocationPoint, ainda são localizadas pelo XY do centro)
            try
            {
                var bb = el.get_BoundingBox(null);
                if (bb != null) return (bb.Min + bb.Max) * 0.5;
            }
            catch { }
            return null;
        }

        private static AmbienteInfo AmbienteDoPonto(List<AmbienteInfo> ambientes, XYZ p, double tol)
        {
            if (p == null) return null;
            // 1) contido estritamente
            foreach (var a in ambientes)
            {
                if (p.X < a.MinX || p.X > a.MaxX || p.Y < a.MinY || p.Y > a.MaxY) continue; // bbox rápido
                if (DentroPoligono(a.Loops, p.X, p.Y)) return a;
            }
            // 2) fallback por proximidade: ambiente cuja borda está mais perto, até 'tol'
            if (tol <= 0) return null;
            AmbienteInfo melhor = null; double dmin = tol;
            foreach (var a in ambientes)
            {
                if (p.X < a.MinX - tol || p.X > a.MaxX + tol || p.Y < a.MinY - tol || p.Y > a.MaxY + tol) continue;
                double d = DistanciaAoContorno(a.Loops, p.X, p.Y);
                if (d <= dmin) { dmin = d; melhor = a; }
            }
            return melhor;
        }

        private static double DistanciaAoContorno(List<List<XYZ>> loops, double x, double y)
        {
            double dmin = double.MaxValue;
            foreach (var poly in loops)
            {
                int n = poly.Count;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double d = DistPontoSegmento(x, y, poly[j].X, poly[j].Y, poly[i].X, poly[i].Y);
                    if (d < dmin) dmin = d;
                }
            }
            return dmin;
        }

        private static double DistPontoSegmento(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double len2 = dx * dx + dy * dy;
            double t = len2 <= 1e-12 ? 0 : ((px - ax) * dx + (py - ay) * dy) / len2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double cx = ax + t * dx, cy = ay + t * dy;
            double ex = px - cx, ey = py - cy;
            return Math.Sqrt(ex * ex + ey * ey);
        }

        public static bool DentroPoligono(List<List<XYZ>> loops, double x, double y)
        {
            bool dentro = false;
            foreach (var poly in loops)
            {
                int n = poly.Count;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double xi = poly[i].X, yi = poly[i].Y, xj = poly[j].X, yj = poly[j].Y;
                    bool inter = ((yi > y) != (yj > y)) &&
                                 (x < (xj - xi) * (y - yi) / ((yj - yi) == 0 ? 1e-12 : (yj - yi)) + xi);
                    if (inter) dentro = !dentro;
                }
            }
            return dentro;
        }

        // =================================================================
        // Circuito elétrico
        // =================================================================
        private static ElectricalSystem CircuitoDominante(List<FamilyInstance> fixtures, out bool mistura)
        {
            mistura = false;
            var contagem = new Dictionary<long, int>();
            var mapa = new Dictionary<long, ElectricalSystem>();
            foreach (var fi in fixtures)
            {
                try
                {
                    if (fi.MEPModel == null) continue;
                    var syss = fi.MEPModel.GetElectricalSystems();
                    if (syss == null) continue;
                    foreach (ElectricalSystem es in syss)
                    {
                        if (es == null) continue;
                        if (es.SystemType != ElectricalSystemType.PowerCircuit) continue;
                        long id = es.Id.Value;
                        contagem[id] = contagem.ContainsKey(id) ? contagem[id] + 1 : 1;
                        mapa[id] = es;
                    }
                }
                catch { }
            }
            if (contagem.Count == 0) return null;
            mistura = contagem.Count > 1;
            long melhor = contagem.OrderByDescending(kv => kv.Value).First().Key;
            return mapa[melhor];
        }

        private static string NomeCircuito(ElectricalSystem es)
        {
            if (es == null) return "~~~"; // sem circuito por último
            try { return es.Name ?? es.Id.Value.ToString(); }
            catch { return es.Id.Value.ToString(); }
        }

        // =================================================================
        // Ordenação física: fileiras de baixo->cima, dentro de cada fileira
        // da direita->esquerda. Usa o centroide de cada ambiente.
        // =================================================================
        private static List<AmbienteInfo> OrdenarFisicamente(List<AmbienteInfo> grupo)
        {
            if (grupo.Count <= 1) return grupo;

            var alturas = grupo.Select(a => a.MaxY - a.MinY).Where(h => h > 0).OrderBy(h => h).ToList();
            double tol = alturas.Count > 0 ? alturas[alturas.Count / 2] * 0.5 : 1.0; // metade da altura mediana
            if (tol < 1e-6) tol = 1.0;

            var porY = grupo.OrderBy(a => a.Centroide.Y).ToList();
            var fileiras = new List<List<AmbienteInfo>>();
            foreach (var a in porY)
            {
                if (fileiras.Count == 0 || Math.Abs(a.Centroide.Y - fileiras[fileiras.Count - 1][0].Centroide.Y) > tol)
                    fileiras.Add(new List<AmbienteInfo>());
                fileiras[fileiras.Count - 1].Add(a);
            }

            var final = new List<AmbienteInfo>();
            foreach (var fila in fileiras) // já de baixo->cima
            {
                fila.Sort((p, q) => q.Centroide.X.CompareTo(p.Centroide.X)); // X decrescente: direita->esquerda
                final.AddRange(fila);
            }
            return final;
        }

        // =================================================================
        // Switch ID (gravação) — usado pelo handler
        // =================================================================
        // Nomes candidatos do parâmetro de Switch ID (pt-BR e en-US).
        public static readonly string[] NOMES_SWID =
            { "ID do comando", "Switch ID", "ID do interruptor", "Comando do interruptor", "Comando" };

        public static bool SetSwitchId(Element el, string valor)
        {
            try
            {
                Parameter bip = el.get_Parameter(BuiltInParameter.RBS_ELEC_SWITCH_ID_PARAM);
                if (bip != null && !bip.IsReadOnly && bip.StorageType == StorageType.String) { bip.Set(valor); return true; }
                foreach (var n in NOMES_SWID)
                {
                    var p = el.LookupParameter(n);
                    if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String) { p.Set(valor); return true; }
                }
            }
            catch { }
            return false;
        }

        public static string DiagParametros(Element el)
        {
            if (el == null) return "(elemento nulo)";
            var achados = new List<string>();
            try
            {
                foreach (Parameter p in el.Parameters)
                {
                    try
                    {
                        string nome = p.Definition != null ? p.Definition.Name : "?";
                        string baixo = nome.ToLowerInvariant();
                        if (baixo.Contains("comando") || baixo.Contains("switch") || baixo.Contains("interruptor"))
                            achados.Add(nome + " [" + p.StorageType + (p.IsReadOnly ? ", read-only" : "") + "]");
                    }
                    catch { }
                }
            }
            catch { }
            return achados.Count > 0 ? string.Join(" | ", achados) : "(nenhum parâmetro com 'comando/switch/interruptor')";
        }

        // Chave de agrupamento conforme o escopo de nomeação
        private static string GrupoChave(AmbienteInfo a, Escopo escopo)
        {
            if (escopo == Escopo.Projeto) return "*";
            if (escopo == Escopo.Quadro)
            {
                try
                {
                    string pn = a.Circuito != null ? a.Circuito.PanelName : null;
                    return string.IsNullOrWhiteSpace(pn) ? "~~~ (sem quadro)" : pn;
                }
                catch { return "~~~ (sem quadro)"; }
            }
            // Circuito (padrão)
            return a.Circuito != null ? NomeCircuito(a.Circuito) : "~~~ (sem circuito)";
        }

        // Rótulo sequencial conforme o estilo. idx 0-based.
        private static string Rotulo(int idx, Estilo estilo)
        {
            if (estilo == Estilo.Numeros) return (idx + 1).ToString();
            char baseChar = (estilo == Estilo.Maiusculas) ? 'A' : 'a';
            string s = "";
            int n = idx + 1;
            while (n > 0) { n--; s = (char)(baseChar + n % 26) + s; n /= 26; }
            return s;
        }

        private static void Aviso(string msg)
        {
            Autodesk.Revit.UI.TaskDialog.Show("Aegia - Switch ID", msg);
        }

        // =================================================================
        // Modelo de ambiente (uso interno, durante a coleta)
        // =================================================================
        private class AmbienteInfo
        {
            public SpatialElement Espaco;
            public List<List<XYZ>> Loops;
            public XYZ Centroide;
            public double MinX, MinY, MaxX, MaxY;
            public List<FamilyInstance> Luminarias = new List<FamilyInstance>();
            public List<FamilyInstance> Interruptores = new List<FamilyInstance>();
            public ElectricalSystem Circuito;
            public long CircuitoKey = -1L;
            public bool MisturaCircuito;
            public string Letra = "";
            public string Nome;

            public AmbienteInfo(SpatialElement se, List<List<XYZ>> loops)
            {
                Espaco = se;
                Loops = loops;
                MinX = MinY = double.MaxValue; MaxX = MaxY = double.MinValue;
                double sx = 0, sy = 0; int n = 0;
                var ext = loops[0]; // loop externo
                foreach (var p in ext) { sx += p.X; sy += p.Y; n++; }
                Centroide = n > 0 ? new XYZ(sx / n, sy / n, 0) : XYZ.Zero;
                foreach (var loop in loops)
                    foreach (var p in loop)
                    {
                        if (p.X < MinX) MinX = p.X; if (p.X > MaxX) MaxX = p.X;
                        if (p.Y < MinY) MinY = p.Y; if (p.Y > MaxY) MaxY = p.Y;
                    }
                try
                {
                    string num = se.get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString();
                    string nm = se.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
                    if (string.IsNullOrWhiteSpace(nm)) nm = se.Name;
                    Nome = (string.IsNullOrWhiteSpace(num) ? "" : num + " - ") + (string.IsNullOrWhiteSpace(nm) ? "Ambiente" : nm);
                }
                catch { Nome = "Ambiente " + se.Id.Value; }
            }
        }
    }

    // =====================================================================
    // Item de comando exibido no seletor (guarda só ElementIds, seguro no modeless)
    // =====================================================================
    public class ComandoItem
    {
        public string Letra;
        public string Ambiente;
        public string Circuito;
        public List<ElementId> Interruptores = new List<ElementId>();
        public List<ElementId> Luminarias = new List<ElementId>();

        public List<ElementId> Todos()
        {
            var l = new List<ElementId>();
            l.AddRange(Interruptores);
            l.AddRange(Luminarias);
            return l;
        }

        public override string ToString()
        {
            return Letra + "   |   " + Ambiente + "   (" + Luminarias.Count + " lum, " + Interruptores.Count + " int)   ·   " + Circuito;
        }
    }

    // =====================================================================
    // Handler (executa no contexto Revit)
    // =====================================================================
    public class SelHandler : IExternalEventHandler
    {
        public enum Acao { Selecionar, GravarIds }
        public Acao Modo = Acao.Selecionar;
        public ComandoItem Alvo;
        public List<ComandoItem> Todos;
        public SelForm Form;

        public string GetName() { return "Aegia Switch ID Seletor"; }

        public void Execute(UIApplication app)
        {
            try
            {
                UIDocument uidoc = app.ActiveUIDocument;
                if (uidoc == null) return;
                Document doc = uidoc.Document;
                if (Modo == Acao.Selecionar) Selecionar(uidoc);
                else GravarIds(doc);
            }
            catch (Exception ex)
            {
                if (Form != null) Form.Log("ERRO (" + ex.GetType().Name + "): " + ex.Message);
            }
        }

        private void Selecionar(UIDocument uidoc)
        {
            if (Alvo == null) return;
            // Seleciona APENAS as luminárias, para permitir criar o sistema de
            // comando (o interruptor é escolhido depois, na faixa do Revit).
            var ids = Alvo.Luminarias.Where(id => id != null && id != ElementId.InvalidElementId).ToList();
            if (ids.Count == 0) { if (Form != null) Form.Log("Comando " + Alvo.Letra + " sem luminárias."); return; }
            uidoc.Selection.SetElementIds(ids);
            try { uidoc.ShowElements(ids); } catch { }
            if (Form != null)
                Form.Log("Comando " + Alvo.Letra + " (" + Alvo.Ambiente + "): " + ids.Count +
                         " luminária(s) selecionada(s). Agora clique em 'Sistema de comando' na faixa do Revit.");
        }

        private void GravarIds(Document doc)
        {
            if (Todos == null) return;
            int n = 0;
            using (Transaction t = new Transaction(doc, "Aegia: Gravar ID do comando"))
            {
                t.Start();
                foreach (var ci in Todos)
                    foreach (var id in ci.Todos())
                    {
                        var el = doc.GetElement(id);
                        if (el != null && SwitchIDCommand.SetSwitchId(el, ci.Letra)) n++;
                    }
                t.Commit();
            }
            if (Form == null) return;
            Form.Log("ID do comando gravado em " + n + " elemento(s).");
            if (n == 0)
            {
                var ex0 = Todos.SelectMany(c => c.Todos()).Select(id => doc.GetElement(id)).FirstOrDefault(e => e != null);
                Form.Log("[Diag] Procurados: " + string.Join(", ", SwitchIDCommand.NOMES_SWID));
                Form.Log("[Diag] Exemplo -> " + SwitchIDCommand.DiagParametros(ex0));
            }
        }
    }

    // =====================================================================
    // UI — janela modeless (seletor de comandos)
    // =====================================================================
    public class SelForm : WWindow
    {
        private readonly SelHandler handler;
        private readonly ExternalEvent ev;
        private readonly List<ComandoItem> comandos;
        private WListBox lst;
        private WTextBox txtLog;

        public SelForm(SelHandler h, ExternalEvent e, List<ComandoItem> comandos, List<string> ignorados,
                       int totalLum, int lumNaLista, int lumSemAmbiente, int lumIgnoradasLeme, string resumo)
        {
            handler = h; ev = e; this.comandos = comandos;
            Title = "Aegia — Sistemas de comando (Switch ID)";
            Width = 580; Height = 660;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            Topmost = true;

            WStackPanel root = new WStackPanel { Margin = new WThickness(12) };
            Content = root;

            root.Children.Add(new WLabel { Content = comandos.Count + " comando(s) detectado(s).", FontWeight = WFontWeights.Bold });
            if (!string.IsNullOrEmpty(resumo))
                root.Children.Add(new WLabel { Content = resumo, Foreground = WBrushes.SteelBlue, FontSize = 11 });
            root.Children.Add(new WLabel { Content = "1) Clique num comando abaixo — os elementos são selecionados/zoom no modelo." });
            root.Children.Add(new WLabel { Content = "2) Clique em 'Sistema de comando' (Switch System) na faixa do Revit para criar.", FontWeight = WFontWeights.Bold });

            lst = new WListBox { Height = 340, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12 };
            foreach (var ci in comandos) lst.Items.Add(ci);
            lst.SelectionChanged += (s, a) => SelecionarAtual();
            root.Children.Add(lst);

            WStackPanel bts = new WStackPanel { Orientation = WOrientation.Horizontal, Margin = new WThickness(0, 8, 0, 6) };
            bts.Children.Add(Botao("Selecionar no modelo", WBrushes.LightBlue, (s, a) => SelecionarAtual()));
            bts.Children.Add(Botao("Gravar 'ID do comando' (todos)", WBrushes.LightGreen, (s, a) => GravarTodos()));
            root.Children.Add(bts);

            root.Children.Add(new WLabel
            {
                Content = "Luminárias na lista: " + lumNaLista + " de " + totalLum +
                          "   (fora de ambiente: " + lumSemAmbiente +
                          (ignorados != null ? "; ambientes sem interruptor: " + ignorados.Count : "") +
                          (lumIgnoradasLeme > 0 ? "; ignoradas Ztipofam=LEME: " + lumIgnoradasLeme : "") + ")",
                Foreground = (lumNaLista < totalLum) ? WBrushes.DarkOrange : WBrushes.Gray
            });

            txtLog = new WTextBox
            {
                Height = 120,
                IsReadOnly = true,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                VerticalScrollBarVisibility = WScrollBarVisibility.Auto,
                FontSize = 11
            };
            root.Children.Add(txtLog);
        }

        private void SelecionarAtual()
        {
            var ci = lst.SelectedItem as ComandoItem;
            if (ci == null) return;
            handler.Modo = SelHandler.Acao.Selecionar;
            handler.Alvo = ci;
            ev.Raise();
        }

        private void GravarTodos()
        {
            handler.Modo = SelHandler.Acao.GravarIds;
            handler.Todos = comandos;
            ev.Raise();
        }

        private WButton Botao(string txt, System.Windows.Media.Brush bg, System.Windows.RoutedEventHandler click)
        {
            WButton b = new WButton { Content = txt, Width = 250, Height = 30, Margin = new WThickness(0, 0, 8, 0), Background = bg };
            b.Click += click;
            return b;
        }

        public void Log(string s)
        {
            txtLog.Text = (string.IsNullOrEmpty(txtLog.Text) ? "" : txtLog.Text + "\n") + s;
            txtLog.ScrollToEnd();
        }
    }

    // =====================================================================
    // Configuração (persistida em %APPDATA%\Aegia\SwitchID.cfg)
    // =====================================================================
    public enum Escopo { Projeto, Circuito, Quadro }
    public enum Estilo { Minusculas, Maiusculas, Numeros }

    public class Config
    {
        public double TolIntM = 0.60;
        public double TolLumM = 0.30;
        public Escopo Escopo = Escopo.Circuito;
        public Estilo Estilo = Estilo.Minusculas;

        private static string Caminho()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aegia");
            return Path.Combine(dir, "SwitchID.cfg");
        }

        public static Config Carregar()
        {
            var c = new Config();
            try
            {
                string f = Caminho();
                if (!File.Exists(f)) return c;
                foreach (var linha in File.ReadAllLines(f))
                {
                    int eq = linha.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = linha.Substring(0, eq).Trim();
                    string v = linha.Substring(eq + 1).Trim();
                    if (k == "TolIntM") c.TolIntM = ParseD(v, c.TolIntM);
                    else if (k == "TolLumM") c.TolLumM = ParseD(v, c.TolLumM);
                    else if (k == "Escopo") { Escopo e; if (Enum.TryParse(v, out e)) c.Escopo = e; }
                    else if (k == "Estilo") { Estilo e; if (Enum.TryParse(v, out e)) c.Estilo = e; }
                }
            }
            catch { }
            return c;
        }

        public void Salvar()
        {
            try
            {
                string f = Caminho();
                Directory.CreateDirectory(Path.GetDirectoryName(f));
                File.WriteAllLines(f, new[]
                {
                    "TolIntM=" + TolIntM.ToString(CultureInfo.InvariantCulture),
                    "TolLumM=" + TolLumM.ToString(CultureInfo.InvariantCulture),
                    "Escopo=" + Escopo,
                    "Estilo=" + Estilo
                });
            }
            catch { }
        }

        public static double ParseD(string s, double def)
        {
            if (string.IsNullOrWhiteSpace(s)) return def;
            s = s.Trim().Replace(',', '.');
            double d;
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : def;
        }
    }

    // =====================================================================
    // UI — janela de configuração (Shift+clique)
    // =====================================================================
    public class ConfigForm : WWindow
    {
        private readonly Config cfg;
        private WTextBox txtInt, txtLum;
        private WComboBox cmbEscopo, cmbEstilo;

        public ConfigForm(Config c)
        {
            cfg = c;
            Title = "Aegia — Configuração (Switch ID)";
            Width = 430; Height = 380;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            Topmost = true;

            WStackPanel root = new WStackPanel { Margin = new WThickness(14) };
            Content = root;

            root.Children.Add(new WLabel { Content = "Tolerância de captação fora do ambiente", FontWeight = WFontWeights.Bold });
            txtInt = Linha(root, "Interruptor (m):", cfg.TolIntM.ToString("0.00", CultureInfo.InvariantCulture));
            txtLum = Linha(root, "Luminária (m):", cfg.TolLumM.ToString("0.00", CultureInfo.InvariantCulture));

            root.Children.Add(new WLabel { Content = "Regra de nomeação (reinicia a contagem por):", FontWeight = WFontWeights.Bold, Margin = new WThickness(0, 12, 0, 0) });
            cmbEscopo = Combo(root, "Escopo:", new[] { "Projeto inteiro", "Por circuito", "Por quadro" });
            cmbEscopo.SelectedIndex = (int)cfg.Escopo;

            root.Children.Add(new WLabel { Content = "Estilo do identificador:", FontWeight = WFontWeights.Bold, Margin = new WThickness(0, 12, 0, 0) });
            cmbEstilo = Combo(root, "Estilo:", new[] { "a, b, c (minúsculas)", "A, B, C (maiúsculas)", "1, 2, 3 (números)" });
            cmbEstilo.SelectedIndex = (int)cfg.Estilo;

            WStackPanel bts = new WStackPanel { Orientation = WOrientation.Horizontal, Margin = new WThickness(0, 18, 0, 0) };
            var btSalvar = new WButton { Content = "Salvar", Width = 130, Height = 30, Background = WBrushes.LightGreen, Margin = new WThickness(0, 0, 8, 0) };
            btSalvar.Click += (s, a) => { Salvar(); Close(); };
            var btCancel = new WButton { Content = "Cancelar", Width = 130, Height = 30 };
            btCancel.Click += (s, a) => Close();
            bts.Children.Add(btSalvar); bts.Children.Add(btCancel);
            root.Children.Add(bts);
        }

        private void Salvar()
        {
            cfg.TolIntM = Config.ParseD(txtInt.Text, cfg.TolIntM);
            cfg.TolLumM = Config.ParseD(txtLum.Text, cfg.TolLumM);
            cfg.Escopo = (Escopo)Math.Max(0, cmbEscopo.SelectedIndex);
            cfg.Estilo = (Estilo)Math.Max(0, cmbEstilo.SelectedIndex);
            cfg.Salvar();
        }

        private WTextBox Linha(WStackPanel root, string rotulo, string valor)
        {
            WStackPanel sp = new WStackPanel { Orientation = WOrientation.Horizontal, Margin = new WThickness(0, 3, 0, 0) };
            sp.Children.Add(new WLabel { Content = rotulo, Width = 170 });
            WTextBox box = new WTextBox { Text = valor, Width = 120 };
            sp.Children.Add(box); root.Children.Add(sp); return box;
        }

        private WComboBox Combo(WStackPanel root, string rotulo, string[] itens)
        {
            WStackPanel sp = new WStackPanel { Orientation = WOrientation.Horizontal, Margin = new WThickness(0, 3, 0, 0) };
            sp.Children.Add(new WLabel { Content = rotulo, Width = 170 });
            WComboBox cmb = new WComboBox { Width = 220 };
            foreach (var it in itens) cmb.Items.Add(it);
            sp.Children.Add(cmb); root.Children.Add(sp); return cmb;
        }
    }
}
