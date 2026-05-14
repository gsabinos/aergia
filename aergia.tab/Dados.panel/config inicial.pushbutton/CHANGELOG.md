# Changelog

## [Unreleased]

### 2026-05-11
- Migradas 8 chamadas `MessageBox.Show` (System.Windows.Forms) para `Autodesk.Revit.UI.TaskDialog.Show`. Ordem dos argumentos invertida (TaskDialog usa `(title, msg)` contra `(msg, title, buttons, icon)` do MessageBox).
- Removido `using System.Windows.Forms` — elimina ambiguidade com `Autodesk.Revit.UI.TaskDialog` e dependência implícita de `System.Drawing` que gerava CS0012 em alguns cenários.
- Observação: este arquivo (`config ini.cs`) contém uma cópia da classe `SmartTagsCommand` (namespace `Aegia_Automations`) que também existe em `Eletrica.panel\smarttags.pushbutton\script.cs`. Como estão em pushbuttons diferentes, pyRevit compila assemblies separados — sem conflito de símbolos. Mas é código duplicado: qualquer correção futura precisa ser feita nos dois.
