using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;

// Resolvendo ambiguidades de nomes entre Revit e WPF
using WWindow = System.Windows.Window;
using WLabel = System.Windows.Controls.Label;
using WKey = System.Windows.Input.Key;
using WKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Aegia_Tools
{
    [Transaction(TransactionMode.Manual)]
    public class SuperAlignCommand : Autodesk.Revit.UI.IExternalCommand
    {
        public Autodesk.Revit.UI.Result Execute(Autodesk.Revit.UI.ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Autodesk.Revit.UI.UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            Autodesk.Revit.DB.View activeView = doc.ActiveView;

            // 1. Escudo de Memória e Filtro
            List<Element> selecionados = uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Where(el => el != null && el.IsValidObject && el.get_BoundingBox(activeView) != null)
                .ToList();

            if (selecionados.Count < 2)
            {
                Autodesk.Revit.UI.TaskDialog.Show("Super Align", "Selecione ao menos 2 elementos válidos.");
                return Autodesk.Revit.UI.Result.Cancelled;
            }

            // 2. Chamada da Interface
            WKey selectedDirection = WKey.None;
            DirectionPickerWindow picker = new DirectionPickerWindow();
            bool? result = picker.ShowDialog();
            
            if (result != true) return Autodesk.Revit.UI.Result.Cancelled;
            selectedDirection = picker.SelectedDirection;

            using (Transaction t = new Transaction(doc, "Super Alinhamento"))
            {
                t.Start();
                try 
                {
                    ExecutarAlinhamento(doc, activeView, selecionados, selectedDirection);
                    t.Commit();
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    Autodesk.Revit.UI.TaskDialog.Show("Erro", ex.Message);
                }
            }

            return Autodesk.Revit.UI.Result.Succeeded;
        }

        private void ExecutarAlinhamento(Document doc, Autodesk.Revit.DB.View view, List<Element> elementos, WKey direcao)
        {
            double margem = 2.0 / 304.8; // 2mm

            if (direcao == WKey.Left || direcao == WKey.Right)
            {
                elementos = elementos.OrderBy(el => el.get_BoundingBox(view).Min.X).ToList();
                
                if (direcao == WKey.Left) // Mantém o da DIREITA
                {
                    elementos.Reverse();
                    ProcessarStack(doc, view, elementos, "X", -1, margem);
                }
                else // Mantém o da ESQUERDA
                {
                    ProcessarStack(doc, view, elementos, "X", 1, margem);
                }
            }
            else if (direcao == WKey.Up || direcao == WKey.Down)
            {
                elementos = elementos.OrderBy(el => el.get_BoundingBox(view).Min.Y).ToList();

                if (direcao == WKey.Up) // Mantém o INFERIOR
                {
                    ProcessarStack(doc, view, elementos, "Y", 1, margem);
                }
                else // Mantém o SUPERIOR
                {
                    elementos.Reverse();
                    ProcessarStack(doc, view, elementos, "Y", -1, margem);
                }
            }
        }

        private void ProcessarStack(Document doc, Autodesk.Revit.DB.View view, List<Element> lista, string eixo, int direcaoMult, double margem)
        {
            Element mestre = lista.First();
            BoundingBoxXYZ boxMestre = mestre.get_BoundingBox(view);
            
            double centroMestre = (eixo == "X") 
                ? (boxMestre.Max.Y + boxMestre.Min.Y) / 2.0 
                : (boxMestre.Max.X + boxMestre.Min.X) / 2.0;

            foreach (var el in lista.Skip(1))
            {
                if (el.Pinned) continue;
                BoundingBoxXYZ box = el.get_BoundingBox(view);
                double centroEl = (eixo == "X") ? (box.Max.Y + box.Min.Y) / 2.0 : (box.Max.X + box.Min.X) / 2.0;
                double diff = centroMestre - centroEl;
                ElementTransformUtils.MoveElement(doc, el.Id, (eixo == "X") ? new XYZ(0, diff, 0) : new XYZ(diff, 0, 0));
            }

            doc.Regenerate();

            for (int i = 1; i < lista.Count; i++)
            {
                Element anterior = doc.GetElement(lista[i - 1].Id);
                Element atual = doc.GetElement(lista[i].Id);
                if (atual.Pinned) continue;

                BoundingBoxXYZ bAnt = anterior.get_BoundingBox(view);
                BoundingBoxXYZ bAtu = atual.get_BoundingBox(view);

                double deslocamento = 0;
                if (eixo == "X")
                {
                    double alvoX = (direcaoMult > 0) ? bAnt.Max.X + margem : bAnt.Min.X - margem;
                    deslocamento = alvoX - ((direcaoMult > 0) ? bAtu.Min.X : bAtu.Max.X);
                    ElementTransformUtils.MoveElement(doc, atual.Id, new XYZ(deslocamento, 0, 0));
                }
                else
                {
                    double alvoY = (direcaoMult > 0) ? bAnt.Max.Y + margem : bAnt.Min.Y - margem;
                    deslocamento = alvoY - ((direcaoMult > 0) ? bAtu.Min.Y : bAtu.Max.Y);
                    ElementTransformUtils.MoveElement(doc, atual.Id, new XYZ(0, deslocamento, 0));
                }
                doc.Regenerate();
            }
        }
    }

    public class DirectionPickerWindow : WWindow
    {
        public WKey SelectedDirection { get; private set; }

        public DirectionPickerWindow()
        {
            this.Title = "Alinhamento Inteligente";
            this.Width = 250;
            this.Height = 150;
            this.WindowStyle = System.Windows.WindowStyle.ToolWindow;
            this.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            this.Topmost = true;

            WLabel lb = new WLabel() {
                Content = "Pressione uma SETA\npara alinhar",
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalContentAlignment = System.Windows.VerticalAlignment.Center
            };

            this.Content = lb;
            
            this.KeyDown += OnKeyDownHandler;
        }

        private void OnKeyDownHandler(object sender, WKeyEventArgs e)
        {
            if (e.Key == WKey.Up || e.Key == WKey.Down || 
                e.Key == WKey.Left || e.Key == WKey.Right) {
                SelectedDirection = e.Key;
                this.DialogResult = true;
                this.Close();
            }
        }
    }
}