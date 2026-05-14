# CHANGELOG

## [Unreleased]

### 2026-05-11
- Compat Revit 2025+: `new ElementId(BuiltInCategory.OST_GenericAnnotation)` agora com cast `(long)` em `script.cs` (AnotacaoGenericaFilter) e em `lançamento.cs`.
- `lançamento.cs`: 6 chamadas `TaskDialog.Show` qualificadas como `Autodesk.Revit.UI.TaskDialog.Show` (evita CS0104 caso WinForms seja importado).
- Detectada duplicação: `lançamento.cs` define `AnotacaoGenericaFilter`, `BancoDadosJson` e `CircuitoJson` que já existem em `script.cs` (mesma pasta, mesmo namespace). Provável backup esquecido — candidato a renomear para `.bak`.

### Anteriores
- **Compatibilidade Multi-Versão (.NET 8):** Refatoração para WPF e correção de namespaces.
- **Refatoração:** Remoção completa da dependência de `System.Text.RegularExpressions` nos parsers em conformidade com as diretrizes C# do pyRevit (.NET 8). Substituídas as chamadas de `.Regex` por lógicas baseadas em `IndexOf`, `.Where(char.IsDigit)`, e `.Split`.
- **Refatoração UI/WinForms:** Substituída a herança direta de `System.Windows.Forms.Form` nas telas (MenuPrincipalForm, MapeamentoForm, RelatorioCruzamentoForm) para evitar erro `CS0012`. Empregada a abordagem de encapsulamento (`public Form MainForm { get; private set; }`).
- **Styling UI:** Remoção das configurações visuais de cores `System.Drawing.Color` (como `BackColor` e `ForeColor`) que acusavam ausência de referências.