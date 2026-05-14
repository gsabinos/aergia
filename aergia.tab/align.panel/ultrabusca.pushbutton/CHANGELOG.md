# CHANGELOG

## [Unreleased]
### Adicionado
- Implementação inicial da estrutura de log no diretório.

### Alterado
- **Compatibilidade Multi-Versão (.NET 8):** Refatoração da interface de usuário da ferramenta de busca (`BuscaDialog`) e da tabela de relatórios (`RelatorioDialog`). Migração completa de `System.Windows.Forms` (WinForms) para `System.Windows.Window` e `System.Windows.Controls.DataGrid` (WPF) usando os prefixos blindados (`WWindow`, `WTextBox`, `WCheckBox`, `WDataGrid`). Isso resolve permanentemente erros do tipo `CS0012` (`IHandle<>`, `Color`, `Size`, `IPersist.Interface`, etc) presentes nas compilações do pyRevit para o Revit 2025, 2026 e 2027.
- **Correção de Erro de Sintaxe (Top-level statements):** Corrigido o erro `CS8803` removendo as chaves duplicadas no final do arquivo `script.cs` que causavam a interrupção da compilação e fechavam prematuramente o namespace.
- **Correção de Ambiguidade de UI:** Qualificado explicitamente `Autodesk.Revit.UI.TaskDialog.Show` para evitar conflitos (`CS0104`) entre bibliotecas do Revit e do C#.

### Removido
- Dependência completa de bibliotecas defasadas de UI do WinForms.