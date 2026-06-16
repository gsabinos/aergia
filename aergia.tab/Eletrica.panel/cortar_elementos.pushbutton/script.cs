using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;

using WWindow = System.Windows.Window;
using WButton = System.Windows.Controls.Button;
using WTextBox = System.Windows.Controls.TextBox;
using WLabel = System.Windows.Controls.Label;
using WRadioButton = System.Windows.Controls.RadioButton;
using WCheckBox = System.Windows.Controls.CheckBox;
using WComboBox = System.Windows.Controls.ComboBox;
using WStackPanel = System.Windows.Controls.StackPanel;
using WGroupBox = System.Windows.Controls.GroupBox;
using WThickness = System.Windows.Thickness;
using WOrientation = System.Windows.Controls.Orientation;

namespace Aegia_CortarElementos
{
    [Transaction(TransactionMode.Manual)]
    public class CortarElementosCommand : IExternalCommand
    {
        // Categorias suportadas pelo corte.
        private static readonly BuiltInCategory[] CategoriasSuportadas =
        {
            BuiltInCategory.OST_Conduit,
            BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_DuctCurves
        };

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            // Shift+clique = modo "cortar e inserir caixa de transição" (só eletroduto).
            bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
            if (shift)
                return ExecutarModoCaixa(uidoc, doc);

            // 1. Coleta as opções via janela WPF modal.
            var form = new CortarOpcoesForm();
            bool? ok = form.ShowDialog();
            if (ok != true || !form.Confirmado)
                return Result.Cancelled;

            double valorPes = UnitUtils.ConvertToInternalUnits(form.ValorMetros, UnitTypeId.Meters);
            if (valorPes <= 1e-6)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Cortar Elementos", "O valor informado é inválido.");
                return Result.Cancelled;
            }

            // 2. Monta a lista de elementos-alvo conforme o escopo.
            List<Element> alvos = ObterAlvos(uidoc, doc, form.ProjetoInteiro);
            if (alvos.Count == 0)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Cortar Elementos",
                    form.ProjetoInteiro
                        ? "Nenhum eletroduto, eletrocalha, tubo ou duto encontrado no projeto."
                        : "Nenhum eletroduto, eletrocalha, tubo ou duto válido na seleção atual.");
                return Result.Cancelled;
            }

            // 3. Corta dentro de uma única transação.
            int cortados = 0, intactos = 0, ignorados = 0, falhas = 0, segmentosCriados = 0;
            bool inserirUniao = form.InserirUniao;
            _unioesCriadas = 0;

            using (Transaction t = new Transaction(doc, "Cortar Elementos"))
            {
                t.Start();

                foreach (Element el in alvos)
                {
                    try
                    {
                        LocationCurve lc = el.Location as LocationCurve;
                        Line line = lc?.Curve as Line;
                        if (line == null) { ignorados++; continue; } // só elementos retos

                        XYZ p0 = line.GetEndPoint(0);
                        XYZ p1 = line.GetEndPoint(1);
                        double comprimento = line.Length;

                        List<double> distancias = CalcularDistancias(comprimento, valorPes);
                        if (distancias.Count == 0) { intactos++; continue; } // menor que o passo

                        XYZ dir = (p1 - p0).Normalize();
                        List<XYZ> pontos = distancias.Select(d => p0 + dir * d).ToList();

                        long cat = el.Category.Id.Value;
                        int segs;
                        if (cat == (long)BuiltInCategory.OST_PipeCurves)
                            segs = QuebrarNativo(doc, el, pontos, true, inserirUniao);
                        else if (cat == (long)BuiltInCategory.OST_DuctCurves)
                            segs = QuebrarNativo(doc, el, pontos, false, inserirUniao);
                        else
                            segs = SplitManual(doc, el, pontos, p0, p1, inserirUniao);

                        cortados++;
                        segmentosCriados += segs;
                    }
                    catch
                    {
                        falhas++; // elemento problemático não interrompe os demais
                    }
                }

                t.Commit();
            }

            Autodesk.Revit.UI.TaskDialog.Show("Cortar Elementos",
                $"Elementos cortados: {cortados}\n" +
                $"Segmentos resultantes: {segmentosCriados}\n" +
                (inserirUniao ? $"Uniões/luvas inseridas: {_unioesCriadas}\n" : "") +
                $"Sem corte (≤ passo): {intactos}\n" +
                $"Ignorados (não retos): {ignorados}\n" +
                $"Falhas: {falhas}\n\n" +
                (inserirUniao
                    ? "Obs.: a luva/união só é inserida se o tipo do elemento tiver esse fitting nas Routing Preferences; "
                      + "senão os segmentos ficam apenas conectados. "
                    : "") +
                "Nos eletrodutos/eletrocalhas, as conexões com curvas/luvas existentes nas pontas podem se desfazer.");

            return Result.Succeeded;
        }

        /// <summary>Coleta os elementos das categorias suportadas conforme o escopo.</summary>
        private List<Element> ObterAlvos(UIDocument uidoc, Document doc, bool projetoInteiro)
        {
            var filtro = new ElementMulticategoryFilter(CategoriasSuportadas);

            if (projetoInteiro)
            {
                return new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WherePasses(filtro)
                    .ToList();
            }

            var ids = uidoc.Selection.GetElementIds();
            var resultado = new List<Element>();
            var suportadas = new HashSet<long>(CategoriasSuportadas.Select(c => (long)c));
            foreach (var id in ids)
            {
                Element el = doc.GetElement(id);
                if (el?.Category != null && suportadas.Contains(el.Category.Id.Value))
                    resultado.Add(el);
            }
            return resultado;
        }

        /// <summary>
        /// Distâncias de corte (a partir de p0), de passo em passo, sequenciais.
        /// O resto fica no último segmento. Retorna lista vazia se o elemento
        /// for menor ou igual ao passo.
        /// </summary>
        private List<double> CalcularDistancias(double comprimento, double passo)
        {
            const double tol = 1e-6;
            var dists = new List<double>();
            for (double d = passo; d < comprimento - tol; d += passo)
                dists.Add(d);
            return dists;
        }

        private int _unioesCriadas; // contador de luvas/uniões inseridas (corte simples)

        /// <summary>
        /// Corte nativo de Tubo (PlumbingUtils) ou Duto (MechanicalUtils).
        /// Processa de trás para frente: o original mantém o lado de p0, então
        /// os pontos restantes continuam válidos sobre ele. Já religa os segmentos.
        /// </summary>
        private int QuebrarNativo(Document doc, Element el, List<XYZ> pontos, bool isTubo, bool inserirUniao)
        {
            int segmentos = 1;
            for (int i = pontos.Count - 1; i >= 0; i--)
            {
                ElementId novo = isTubo
                    ? PlumbingUtils.BreakCurve(doc, el.Id, pontos[i])
                    : MechanicalUtils.BreakCurve(doc, el.Id, pontos[i]);

                if (novo != null && novo != ElementId.InvalidElementId)
                {
                    segmentos++;
                    if (inserirUniao)
                        LigarOuUnir(doc, el, doc.GetElement(novo), pontos[i], true);
                }
            }
            return segmentos;
        }

        /// <summary>
        /// Split manual de Eletroduto/Eletrocalha (sem BreakCurve nativo).
        /// Duplica o original (CopyElement preserva TODOS os parâmetros) uma vez
        /// por segmento, reposiciona cada cópia no trecho correspondente, religa
        /// os segmentos adjacentes e apaga o original.
        /// </summary>
        private int SplitManual(Document doc, Element el, List<XYZ> pontos, XYZ p0, XYZ p1, bool inserirUniao)
        {
            // Fronteiras dos segmentos: p0, cortes..., p1.
            var fronteiras = new List<XYZ> { p0 };
            fronteiras.AddRange(pontos);
            fronteiras.Add(p1);

            var novos = new List<Element>();
            for (int i = 0; i < fronteiras.Count - 1; i++)
            {
                ICollection<ElementId> copias = ElementTransformUtils.CopyElement(doc, el.Id, XYZ.Zero);
                Element copia = doc.GetElement(copias.First());
                if (copia.Location is LocationCurve lc)
                    lc.Curve = Line.CreateBound(fronteiras[i], fronteiras[i + 1]);
                novos.Add(copia);
            }

            // Liga cada par de segmentos no ponto de corte (com luva/união se pedido).
            for (int i = 0; i < novos.Count - 1; i++)
                LigarOuUnir(doc, novos[i], novos[i + 1], fronteiras[i + 1], inserirUniao);

            doc.Delete(el.Id);
            return novos.Count;
        }

        /// <summary>
        /// Liga dois MEPCurve no ponto de corte. Se <paramref name="inserirUniao"/> for true,
        /// tenta inserir a luva/união (NewUnionFitting, das Routing Preferences do tipo);
        /// se não houver união no tipo, cai para conexão lógica (ConnectTo).
        /// </summary>
        private void LigarOuUnir(Document doc, Element a, Element b, XYZ ponto, bool inserirUniao)
        {
            Connector ca = ConectorMaisProximo(a, ponto);
            Connector cb = ConectorMaisProximo(b, ponto);
            if (ca == null || cb == null) return;
            if (ca.IsConnected || cb.IsConnected) return; // já ligados (ex.: break nativo)

            if (inserirUniao)
            {
                try { doc.Create.NewUnionFitting(ca, cb); _unioesCriadas++; return; }
                catch { /* tipo sem união nas Routing Preferences: cai para ConnectTo */ }
            }

            try { ca.ConnectTo(cb); } catch { /* segmentos não-conectáveis: ignora */ }
        }

        private Connector ConectorMaisProximo(Element el, XYZ ponto)
        {
            ConnectorManager cm = (el as MEPCurve)?.ConnectorManager;
            if (cm == null) return null;

            Connector melhor = null;
            double min = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                double d = c.Origin.DistanceTo(ponto);
                if (d < min) { min = d; melhor = c; }
            }
            return melhor;
        }

        // =====================================================================
        // MODO CAIXA (Shift+clique): corta o eletroduto de X em X metros e, em
        // cada corte, insere uma família (com conectores de eletroduto) numa
        // cota Z absoluta, descendo/subindo o eletroduto com cotovelos até ela
        // (pass-through). Só atua em eletroduto (OST_Conduit).
        // =====================================================================

        private const double TolGeom = 1e-6;
        private int _junOk;       // junções (descida) ligadas com sucesso
        private int _junFalha;    // junções sem ligação lógica (geometria mantida)
        private int _cxCurto;     // eletrodutos não cortados por serem ≤ passo
        private int _cxFamPlacement; // falhas ao inserir a família (NewFamilyInstance)
        private int _cxFamConector;  // famílias rejeitadas por não ter ≥2 conectores de eletroduto
        private string _cxMotivo = ""; // último motivo de falha (para o resumo)

        private Result ExecutarModoCaixa(UIDocument uidoc, Document doc)
        {
            var form = new CaixaOpcoesForm(doc);
            bool? ok = form.ShowDialog();
            if (ok != true || !form.Confirmado)
                return Result.Cancelled;

            double passoPes = UnitUtils.ConvertToInternalUnits(form.PassoMetros, UnitTypeId.Meters);
            double zAbsPes = UnitUtils.ConvertToInternalUnits(form.ZMetros, UnitTypeId.Meters);
            if (passoPes <= TolGeom)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Cortar e Inserir Caixa", "O passo informado é inválido.");
                return Result.Cancelled;
            }

            FamilySymbol sym = doc.GetElement(form.SymbolId) as FamilySymbol;
            if (sym == null)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Cortar e Inserir Caixa", "Família/tipo inválido.");
                return Result.Cancelled;
            }

            // Só eletrodutos no escopo.
            List<Element> conduites = ObterConduites(uidoc, doc, form.ProjetoInteiro);
            if (conduites.Count == 0)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Cortar e Inserir Caixa",
                    form.ProjetoInteiro
                        ? "Nenhum eletroduto encontrado no projeto."
                        : "Nenhum eletroduto válido na seleção atual.");
                return Result.Cancelled;
            }

            int caixasInseridas = 0, eletrodutosProcessados = 0, ignorados = 0, falhas = 0;
            _junOk = 0; _junFalha = 0;
            _cxCurto = 0; _cxFamPlacement = 0; _cxFamConector = 0; _cxMotivo = "";

            using (Transaction t = new Transaction(doc, "Cortar e Inserir Caixa"))
            {
                t.Start();

                if (!sym.IsActive) { sym.Activate(); doc.Regenerate(); }

                foreach (Element el in conduites)
                {
                    try
                    {
                        int inseridas = ProcessarConduiteCaixa(doc, el, passoPes, zAbsPes, sym, out bool semCorte);
                        if (semCorte) { ignorados++; continue; }
                        caixasInseridas += inseridas;
                        eletrodutosProcessados++;
                    }
                    catch
                    {
                        falhas++;
                    }
                }

                t.Commit();
            }

            string msg =
                $"Eletrodutos processados: {eletrodutosProcessados}\n" +
                $"Caixas inseridas: {caixasInseridas}\n" +
                $"Junções ligadas: {_junOk}\n" +
                $"Junções sem ligação (só geometria): {_junFalha}\n\n" +
                $"Sem corte (total): {ignorados}\n" +
                $"  • eletroduto ≤ passo / não reto: {_cxCurto}\n" +
                $"  • falha ao inserir a família: {_cxFamPlacement}\n" +
                $"  • família sem 2 conectores de eletroduto: {_cxFamConector}\n" +
                $"Falhas inesperadas: {falhas}\n";

            if (!string.IsNullOrEmpty(_cxMotivo))
                msg += $"\nÚltimo motivo: {_cxMotivo}\n";

            msg += "\nObs.: a ligação física com cotovelos depende do tipo do eletroduto ter cotovelo " +
                   "nas Routing Preferences e de a família ter ≥2 conectores de eletroduto.";

            Autodesk.Revit.UI.TaskDialog.Show("Cortar e Inserir Caixa", msg);

            return Result.Succeeded;
        }

        /// <summary>Coleta apenas eletrodutos (OST_Conduit) conforme o escopo.</summary>
        private List<Element> ObterConduites(UIDocument uidoc, Document doc, bool projetoInteiro)
        {
            if (projetoInteiro)
            {
                return new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Conduit)
                    .WhereElementIsNotElementType()
                    .ToList();
            }

            var resultado = new List<Element>();
            foreach (var id in uidoc.Selection.GetElementIds())
            {
                Element el = doc.GetElement(id);
                if (el?.Category != null && el.Category.Id.Value == (long)BuiltInCategory.OST_Conduit)
                    resultado.Add(el);
            }
            return resultado;
        }

        /// <summary>
        /// Corta um eletroduto reto nos pontos de passo, insere a família em cada
        /// corte (cota Z absoluta) e roteia o eletroduto (descida + cotovelos) até
        /// os 2 conectores da família (pass-through). Retorna o nº de caixas inseridas;
        /// <paramref name="semCorte"/> = true se o eletroduto não foi cortado.
        /// </summary>
        private int ProcessarConduiteCaixa(Document doc, Element el, double passo, double zAbs,
            FamilySymbol sym, out bool semCorte)
        {
            semCorte = true;
            LocationCurve lc = el.Location as LocationCurve;
            Line line = lc?.Curve as Line;
            if (line == null) return 0;

            XYZ p0 = line.GetEndPoint(0);
            XYZ p1 = line.GetEndPoint(1);
            double zCond = p0.Z; // eletroduto horizontal: z constante
            List<double> distancias = CalcularDistancias(line.Length, passo);
            if (distancias.Count == 0) { _cxCurto++; return 0; }

            XYZ u = (p1 - p0).Normalize();
            ElementId typeId = el.GetTypeId();

            // Posiciona/orienta a família em cada ponto de corte.
            var caixas = new List<CaixaInfo>();
            foreach (double d in distancias)
            {
                XYZ P = p0 + u * d;
                CaixaInfo info = PosicionarFamilia(doc, sym, P, u, zAbs);
                if (info != null) caixas.Add(info);
            }
            if (caixas.Count == 0) return 0;
            semCorte = false;

            // Cria os trechos horizontais (cópias do original p/ preservar parâmetros).
            // Fronteiras: p0 -> cornerIn[0]; cornerOut[i] -> cornerIn[i+1]; cornerOut[last] -> p1
            var horizontais = new List<Element>();
            XYZ inicio = p0;
            for (int i = 0; i < caixas.Count; i++)
            {
                horizontais.Add(CriarConduiteCopia(doc, el, inicio, caixas[i].CornerIn));
                inicio = caixas[i].CornerOut;
            }
            horizontais.Add(CriarConduiteCopia(doc, el, inicio, p1));

            // Para cada caixa, desce os dois lados e liga (antes de apagar o original).
            for (int i = 0; i < caixas.Count; i++)
            {
                Element segEsq = horizontais[i];      // termina em CornerIn
                Element segDir = horizontais[i + 1];  // começa em CornerOut
                LigarDescida(doc, el, segEsq, caixas[i].CornerIn, caixas[i].CIn, zCond);
                LigarDescida(doc, el, segDir, caixas[i].CornerOut, caixas[i].COut, zCond);
            }

            doc.Delete(el.Id);
            return caixas.Count;
        }

        private class CaixaInfo
        {
            public Connector CIn;      // conector do lado de p0
            public Connector COut;     // conector do lado de p1
            public XYZ CornerIn;       // acima de CIn, na cota do eletroduto
            public XYZ CornerOut;      // acima de COut, na cota do eletroduto
        }

        /// <summary>Insere a família, ajusta a cota Z dos conectores, orienta no eixo do eletroduto e identifica os conectores in/out.</summary>
        private CaixaInfo PosicionarFamilia(Document doc, FamilySymbol sym, XYZ P, XYZ u, double zAbs)
        {
            FamilyInstance inst;
            try { inst = doc.Create.NewFamilyInstance(new XYZ(P.X, P.Y, zAbs), sym, StructuralType.NonStructural); }
            catch (Exception ex)
            {
                _cxFamPlacement++;
                _cxMotivo = "Falha ao inserir a família (talvez seja baseada em face/nível e exija hospedeiro): " + ex.Message;
                return null;
            }
            doc.Regenerate();

            List<Connector> conns = ConectoresEletricos(inst);
            if (conns.Count < 2)
            {
                _cxFamConector++;
                _cxMotivo = $"Família \"{sym.Family.Name} - {sym.Name}\": {ContarConectores(inst)} conector(es) no total, " +
                            $"{conns.Count} de eletroduto (End/Elétrico). Precisa de ≥2 de eletroduto.";
                doc.Delete(inst.Id);
                return null;
            }

            // Orienta o par de conectores mais separados no plano XY para a direção do eletroduto.
            OrientarParaU(doc, inst, conns, P, u, zAbs);

            // Ajusta a cota para que a média do par in/out fique em zAbs.
            conns = ConectoresEletricos(inst);
            EscolherInOut(conns, P, u, out Connector cIn, out Connector cOut);
            double zMid = (cIn.Origin.Z + cOut.Origin.Z) / 2.0;
            if (Math.Abs(zMid - zAbs) > TolGeom)
            {
                ElementTransformUtils.MoveElement(doc, inst.Id, new XYZ(0, 0, zAbs - zMid));
                doc.Regenerate();
                conns = ConectoresEletricos(inst);
                EscolherInOut(conns, P, u, out cIn, out cOut);
            }

            return new CaixaInfo
            {
                CIn = cIn,
                COut = cOut,
                CornerIn = new XYZ(cIn.Origin.X, cIn.Origin.Y, P.Z),
                CornerOut = new XYZ(cOut.Origin.X, cOut.Origin.Y, P.Z)
            };
        }

        /// <summary>Gira a instância (eixo vertical por P) para alinhar o par de conectores mais separados no XY à direção u.</summary>
        private void OrientarParaU(Document doc, FamilyInstance inst, List<Connector> conns, XYZ P, XYZ u, double zAbs)
        {
            double best = -1; int ia = 0, ib = 1;
            for (int i = 0; i < conns.Count; i++)
                for (int j = i + 1; j < conns.Count; j++)
                {
                    XYZ d = conns[i].Origin - conns[j].Origin;
                    double len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
                    if (len > best) { best = len; ia = i; ib = j; }
                }
            if (best < TolGeom) return; // conectores coincidentes/verticais no XY

            XYZ eixo = conns[ib].Origin - conns[ia].Origin;
            XYZ eixoXY = new XYZ(eixo.X, eixo.Y, 0).Normalize();
            double theta = Math.Atan2(eixoXY.X * u.Y - eixoXY.Y * u.X, eixoXY.X * u.X + eixoXY.Y * u.Y);
            if (Math.Abs(theta) > 1e-9)
            {
                Line axisV = Line.CreateBound(new XYZ(P.X, P.Y, zAbs), new XYZ(P.X, P.Y, zAbs + 1));
                ElementTransformUtils.RotateElement(doc, inst.Id, axisV, theta);
                doc.Regenerate();
            }
        }

        /// <summary>Escolhe o conector mais "para trás" (in, lado p0) e mais "à frente" (out, lado p1) na direção u.</summary>
        private void EscolherInOut(List<Connector> conns, XYZ P, XYZ u, out Connector cIn, out Connector cOut)
        {
            cIn = conns[0]; cOut = conns[0];
            double min = double.MaxValue, max = double.MinValue;
            foreach (Connector c in conns)
            {
                double proj = (c.Origin - P).DotProduct(u);
                if (proj < min) { min = proj; cIn = c; }
                if (proj > max) { max = proj; cOut = c; }
            }
        }

        private List<Connector> ConectoresEletricos(FamilyInstance inst)
        {
            var list = new List<Connector>();
            ConnectorManager cm = inst.MEPModel?.ConnectorManager;
            if (cm == null) return list;
            foreach (Connector c in cm.Connectors)
            {
                if (c.ConnectorType == ConnectorType.End && c.Domain == Domain.DomainElectrical)
                    list.Add(c);
            }
            return list;
        }

        private int ContarConectores(FamilyInstance inst)
        {
            ConnectorManager cm = inst.MEPModel?.ConnectorManager;
            if (cm == null) return 0;
            int n = 0;
            foreach (Connector c in cm.Connectors) n++;
            return n;
        }

        /// <summary>Liga a ponta de um trecho horizontal ao conector da família, criando o trecho vertical e os cotovelos.</summary>
        private void LigarDescida(Document doc, Element original, Element segHoriz, XYZ corner, Connector famConn, double zCond)
        {
            Connector cSeg = ConectorMaisProximo(segHoriz, corner);
            double drop = Math.Abs(zCond - famConn.Origin.Z);

            if (drop < TolGeom)
            {
                // Mesma cota: liga direto o trecho ao conector da família.
                if (Cotovelo(doc, cSeg, famConn)) _junOk++; else _junFalha++;
                return;
            }

            // Trecho vertical (cópia do original p/ herdar diâmetro/parâmetros).
            Element vert = CriarConduiteCopia(doc, original, corner, famConn.Origin);
            Connector vTopo = ConectorMaisProximo(vert, corner);
            Connector vBase = ConectorMaisProximo(vert, famConn.Origin);

            bool topo = Cotovelo(doc, cSeg, vTopo);     // horizontal <-> vertical
            bool baseOk = Cotovelo(doc, vBase, famConn); // vertical <-> família
            if (topo && baseOk) _junOk++; else _junFalha++;
        }

        /// <summary>Cria um cotovelo entre dois conectores; se falhar, tenta ligação direta (colinear). Retorna true se ligou.</summary>
        private bool Cotovelo(Document doc, Connector a, Connector b)
        {
            if (a == null || b == null) return false;
            try { doc.Create.NewElbowFitting(a, b); return true; }
            catch
            {
                try
                {
                    if (!a.IsConnected && !b.IsConnected) { a.ConnectTo(b); return true; }
                }
                catch { }
            }
            return false;
        }

        /// <summary>Duplica o eletroduto (preserva parâmetros) e reposiciona a cópia entre dois pontos.</summary>
        private Element CriarConduiteCopia(Document doc, Element el, XYZ a, XYZ b)
        {
            ICollection<ElementId> copias = ElementTransformUtils.CopyElement(doc, el.Id, XYZ.Zero);
            Element c = doc.GetElement(copias.First());
            if (c.Location is LocationCurve lc)
                lc.Curve = Line.CreateBound(a, b);
            return c;
        }
    }

    /// <summary>Janela modal de opções: escopo, modo e valor (em metros).</summary>
    public class CortarOpcoesForm : WWindow
    {
        private readonly WRadioButton _rbProjeto;
        private readonly WRadioButton _rbSelecao;
        private readonly WRadioButton _rbFixo;
        private readonly WRadioButton _rbMaximo;
        private readonly WTextBox _txtValor;
        private readonly WLabel _lblValor;
        private readonly WCheckBox _chkUniao;

        public bool Confirmado { get; private set; }
        public bool ProjetoInteiro => _rbProjeto.IsChecked == true;
        public bool ModoMaximo => _rbMaximo.IsChecked == true;
        public bool InserirUniao => _chkUniao.IsChecked == true;
        public double ValorMetros { get; private set; }

        public CortarOpcoesForm()
        {
            Title = "Cortar Elementos";
            Width = 360;
            SizeToContent = System.Windows.SizeToContent.Height;
            ResizeMode = System.Windows.ResizeMode.NoResize;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            Topmost = true;

            var raiz = new WStackPanel { Margin = new WThickness(12) };

            // Escopo
            _rbProjeto = new WRadioButton { Content = "Projeto inteiro", IsChecked = true, Margin = new WThickness(0, 2, 0, 2) };
            _rbSelecao = new WRadioButton { Content = "Elementos selecionados", Margin = new WThickness(0, 2, 0, 2) };
            var grpEscopo = new WGroupBox { Header = "Escopo", Margin = new WThickness(0, 0, 0, 8) };
            var spEscopo = new WStackPanel { Margin = new WThickness(6) };
            spEscopo.Children.Add(_rbProjeto);
            spEscopo.Children.Add(_rbSelecao);
            grpEscopo.Content = spEscopo;

            // Modo
            _rbFixo = new WRadioButton { Content = "Espaçamento fixo", IsChecked = true, Margin = new WThickness(0, 2, 0, 2) };
            _rbMaximo = new WRadioButton { Content = "Quebrar distância máxima", Margin = new WThickness(0, 2, 0, 2) };
            _rbFixo.Checked += (s, e) => AtualizarLabel();
            _rbMaximo.Checked += (s, e) => AtualizarLabel();
            var grpModo = new WGroupBox { Header = "Modo", Margin = new WThickness(0, 0, 0, 8) };
            var spModo = new WStackPanel { Margin = new WThickness(6) };
            spModo.Children.Add(_rbFixo);
            spModo.Children.Add(_rbMaximo);
            grpModo.Content = spModo;

            // Valor
            _lblValor = new WLabel { Content = "Cortar a cada (m):" };
            _txtValor = new WTextBox { Text = "3", Width = 80, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            var spValor = new WStackPanel { Orientation = WOrientation.Horizontal, Margin = new WThickness(0, 0, 0, 8) };
            spValor.Children.Add(_lblValor);
            spValor.Children.Add(_txtValor);

            // União/luva nos cortes
            _chkUniao = new WCheckBox
            {
                Content = "Inserir luva/união nos cortes",
                IsChecked = true,
                Margin = new WThickness(0, 0, 0, 8)
            };

            // Botões
            var btnCortar = new WButton { Content = "Cortar", Width = 80, Margin = new WThickness(0, 0, 8, 0), IsDefault = true };
            var btnCancelar = new WButton { Content = "Cancelar", Width = 80, IsCancel = true };
            btnCortar.Click += BtnCortar_Click;
            btnCancelar.Click += (s, e) => { DialogResult = false; Close(); };
            var spBotoes = new WStackPanel
            {
                Orientation = WOrientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            spBotoes.Children.Add(btnCortar);
            spBotoes.Children.Add(btnCancelar);

            raiz.Children.Add(grpEscopo);
            raiz.Children.Add(grpModo);
            raiz.Children.Add(spValor);
            raiz.Children.Add(_chkUniao);
            raiz.Children.Add(spBotoes);
            Content = raiz;
        }

        private void AtualizarLabel()
        {
            if (_lblValor != null)
                _lblValor.Content = ModoMaximo ? "Comprimento máximo (m):" : "Cortar a cada (m):";
        }

        private void BtnCortar_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (!double.TryParse(_txtValor.Text.Trim().Replace(',', '.'),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out double valor) || valor <= 0)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Cortar Elementos", "Informe um valor numérico maior que zero (em metros).");
                return;
            }

            ValorMetros = valor;
            Confirmado = true;
            DialogResult = true;
            Close();
        }
    }

    /// <summary>Item de família para o ComboBox (texto + ElementId do FamilySymbol).</summary>
    public class ItemFamilia
    {
        public string Nome { get; set; }
        public ElementId Id { get; set; }
        public override string ToString() => Nome;
    }

    /// <summary>Janela modal do modo caixa: escopo, passo, família e cota Z absoluta.</summary>
    public class CaixaOpcoesForm : WWindow
    {
        private readonly WRadioButton _rbProjeto;
        private readonly WRadioButton _rbSelecao;
        private readonly WTextBox _txtPasso;
        private readonly WComboBox _cbFamilia;
        private readonly WTextBox _txtZ;

        public bool Confirmado { get; private set; }
        public bool ProjetoInteiro => _rbProjeto.IsChecked == true;
        public double PassoMetros { get; private set; }
        public double ZMetros { get; private set; }
        public ElementId SymbolId { get; private set; }

        public CaixaOpcoesForm(Document doc)
        {
            Title = "Cortar e Inserir Caixa";
            Width = 420;
            SizeToContent = System.Windows.SizeToContent.Height;
            ResizeMode = System.Windows.ResizeMode.NoResize;
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            Topmost = true;

            var raiz = new WStackPanel { Margin = new WThickness(12) };

            // Escopo (só eletroduto).
            _rbProjeto = new WRadioButton { Content = "Projeto inteiro", IsChecked = true, Margin = new WThickness(0, 2, 0, 2) };
            _rbSelecao = new WRadioButton { Content = "Eletrodutos selecionados", Margin = new WThickness(0, 2, 0, 2) };
            var grpEscopo = new WGroupBox { Header = "Escopo (somente eletroduto)", Margin = new WThickness(0, 0, 0, 8) };
            var spEscopo = new WStackPanel { Margin = new WThickness(6) };
            spEscopo.Children.Add(_rbProjeto);
            spEscopo.Children.Add(_rbSelecao);
            grpEscopo.Content = spEscopo;

            // Passo.
            var spPasso = new WStackPanel { Orientation = WOrientation.Horizontal, Margin = new WThickness(0, 0, 0, 8) };
            spPasso.Children.Add(new WLabel { Content = "Cortar a cada (m):", Width = 150 });
            _txtPasso = new WTextBox { Text = "3", Width = 80, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            spPasso.Children.Add(_txtPasso);

            // Família.
            var spFam = new WStackPanel { Margin = new WThickness(0, 0, 0, 8) };
            spFam.Children.Add(new WLabel { Content = "Família da caixa (com conector de eletroduto):" });
            _cbFamilia = new WComboBox { ItemsSource = ColetarFamilias(doc) };
            if (_cbFamilia.Items.Count > 0) _cbFamilia.SelectedIndex = 0;
            spFam.Children.Add(_cbFamilia);

            // Cota Z absoluta.
            var spZ = new WStackPanel { Orientation = WOrientation.Horizontal, Margin = new WThickness(0, 8, 0, 8) };
            spZ.Children.Add(new WLabel { Content = "Cota Z absoluta (m):", Width = 150 });
            _txtZ = new WTextBox { Text = "0", Width = 80, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            spZ.Children.Add(_txtZ);

            // Botões.
            var btnOk = new WButton { Content = "Executar", Width = 90, Margin = new WThickness(0, 0, 8, 0), IsDefault = true };
            var btnCancelar = new WButton { Content = "Cancelar", Width = 90, IsCancel = true };
            btnOk.Click += BtnOk_Click;
            btnCancelar.Click += (s, e) => { DialogResult = false; Close(); };
            var spBotoes = new WStackPanel
            {
                Orientation = WOrientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            spBotoes.Children.Add(btnOk);
            spBotoes.Children.Add(btnCancelar);

            raiz.Children.Add(grpEscopo);
            raiz.Children.Add(spPasso);
            raiz.Children.Add(spFam);
            raiz.Children.Add(spZ);
            raiz.Children.Add(spBotoes);
            Content = raiz;
        }

        /// <summary>Coleta FamilySymbols de categorias que costumam ter conectores de eletroduto.</summary>
        private List<ItemFamilia> ColetarFamilias(Document doc)
        {
            var categorias = new HashSet<long>
            {
                (long)BuiltInCategory.OST_ElectricalFixtures,
                (long)BuiltInCategory.OST_ElectricalEquipment,
                (long)BuiltInCategory.OST_ConduitFitting,
                (long)BuiltInCategory.OST_GenericModel,
                (long)BuiltInCategory.OST_DataDevices,
                (long)BuiltInCategory.OST_CommunicationDevices,
                (long)BuiltInCategory.OST_FireAlarmDevices,
                (long)BuiltInCategory.OST_SecurityDevices,
                (long)BuiltInCategory.OST_LightingDevices,
                (long)BuiltInCategory.OST_NurseCallDevices,
                (long)BuiltInCategory.OST_TelephoneDevices
            };

            var itens = new List<ItemFamilia>();
            var col = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).WhereElementIsElementType();
            foreach (FamilySymbol sym in col)
            {
                if (sym?.Category == null) continue;
                if (!categorias.Contains(sym.Category.Id.Value)) continue;
                itens.Add(new ItemFamilia { Nome = $"{sym.Family.Name} - {sym.Name}", Id = sym.Id });
            }
            return itens.OrderBy(i => i.Nome).ToList();
        }

        private void BtnOk_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (!double.TryParse(_txtPasso.Text.Trim().Replace(',', '.'),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out double passo) || passo <= 0)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Cortar e Inserir Caixa", "Informe um passo numérico maior que zero (em metros).");
                return;
            }
            if (!double.TryParse(_txtZ.Text.Trim().Replace(',', '.'),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out double z))
            {
                Autodesk.Revit.UI.TaskDialog.Show("Cortar e Inserir Caixa", "Informe uma cota Z numérica (em metros).");
                return;
            }
            if (!(_cbFamilia.SelectedItem is ItemFamilia item))
            {
                Autodesk.Revit.UI.TaskDialog.Show("Cortar e Inserir Caixa", "Selecione uma família de caixa.");
                return;
            }

            PassoMetros = passo;
            ZMetros = z;
            SymbolId = item.Id;
            Confirmado = true;
            DialogResult = true;
            Close();
        }
    }
}
