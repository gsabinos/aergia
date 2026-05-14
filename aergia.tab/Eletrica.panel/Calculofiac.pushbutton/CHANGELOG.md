# Changelog

## [Unreleased]

### 2026-05-11
- Compat Revit 2025+: removidos 11 blocos `#if REVIT2024 || REVIT2025 || REVIT2026 || REVIT2027 / #else / #endif`. Código agora usa apenas a API nova (`ElementId.Value` em `long`, `new ElementId((long)BuiltInCategory.X)`, `ParameterId.Value`). Os ramos `#else` apontavam para APIs já removidas em 2025+ (`IntegerValue`, `new ElementId((int)X)`); manter o `#if` era frágil porque pyRevit não garante os símbolos `REVIT*` na compilação.
- Mantido `WorksetId.IntegerValue` em `Utils.GetWorksetId` — `WorksetId` não foi migrado para `.Value` em nenhuma versão alvo (2024-2027).

### Anteriores
### Refactor
- **script.cs**: 
  - Removida a herança de `System.Windows.Forms.Form` das classes `GestorForm` e `WorksetConfigForm` para corrigir erro CS0012 (Missing Reference Form). Agora elas encapsulam uma propriedade `public WinForm.Form MainForm { get; private set; }`.
  - Atualizadas as manipulações de UI WinForms para usar a nova propriedade `MainForm`.
  - Removidas todas as referências a propriedades gráficas de WinForms que exigem o assembly System.Drawing referenciado explicitamente (ex: `BackColor`, `ForeColor`, `Font`, instâncias de `Color` na UI WinForms) para resolver erros CS0012.
  - Modificadas chamadas de `TaskDialog.Show` para a chamada completa e qualificada `Autodesk.Revit.UI.TaskDialog.Show` resolvendo ambiguidades (CS0104).
  - Removida a dependência do namespace `System.Text.RegularExpressions`. O processamento de strings em configurações JSON e filtragens de IDs, elevações e numerações foi substituído por métodos nativos em C# (`Split`, `IndexOf`, LINQ `TakeWhile`/`SkipWhile`).
