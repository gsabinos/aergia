# Changelog

## [Unreleased]

### 2026-05-13
- Compat Revit 2025/2026/2027: eliminados acessos a tipos forwarded para assemblies que o pyRevit não referencia em .NET 8/10.
  - `this.Closing` (CancelEventArgs, `System.ComponentModel.dll`) → `this.Closed` (EventArgs). O handler só fazia reset, não cancelava — comportamento preservado.
  - `tabControl.Items.Cast<WTabItem>().ToList()` → snapshot via `object itemsBox = tabControl.Items;` seguido de `foreach (object item in (System.Collections.IEnumerable)itemsBox)`. `ItemCollection` implementa `INotifyCollectionChanged`/`INotifyPropertyChanged` (`System.ObjectModel.dll`). Mesmo o cast direto `(IEnumerable)tabControl.Items` falha porque o compilador enumera todas as interfaces para validar a conversão. Passar pelo `object` faz upcast trivial sem inspeção de interfaces; o cast subsequente é verificação runtime de `object` → interface.

### 2026-05-11
- Compat Revit 2025+: 4× `new ElementId(BuiltInCategory.X)` agora com cast `(long)` em `MapearInfraestrutura` (Conduit, ConduitFitting, CableTray, CableTrayFitting). Sem o cast, o ctor `ElementId(int)` é resolvido — removido em 2025+.

### Anteriores
- Compatibilidade Multi-Versão (.NET 8): Refatoração para WPF e correção de namespaces.
- Refatorado `script.cs` para utilizar `BuiltInParameter.RBS_ELEC_PANEL_NAME` em substituição à busca por string "Panel Name", garantindo funcionamento idêntico nas versões em Inglês e Português do Revit.