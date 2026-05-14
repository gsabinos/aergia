# CHANGELOG

## [Unreleased]
### Removido
- Mensagens de "Sucesso" (`TaskDialog`) ao final das duas exportações (data sync e visual). Mantida a da importação e as de erro.

### Corrigido
- Compatibilidade Revit 2025/2026/2027 — eliminação total de tipos forwarded para `System.ObjectModel.dll` (que o pyRevit não referencia em .NET 8/10):
  - `TabelaRevit` deixou de implementar `INotifyPropertyChanged`; `Exportar` virou auto-property simples. A UI é mantida em sincronia via `GridTabelas.Items.Refresh()` que já é chamado nos pontos de mutação.
  - `_todasTabelas` continua `List<TabelaRevit>` (a tentativa anterior com `ObservableCollection<T>` apenas movia o problema para outro tipo da mesma assembly).
  - As 3 chamadas `GridTabelas.Columns.Add(...)` foram substituídas por um helper `AddDataGridColumn` que acessa a coleção via reflection (`DataGrid.Columns` é tipado como `ObservableCollection<DataGridColumn>` e não compila estaticamente sem a referência).
  - Removidos `using System.ComponentModel;` e `using System.Collections.ObjectModel;`, que não eram mais necessários.
- Compatibilidade Revit 2025/2026/2027 (CS0104 `Binding` ambíguo): qualificado `new System.Windows.Data.Binding(...)` nas 3 colunas do `GridTabelas` para evitar colisão com `Autodesk.Revit.DB.Binding`.
- Cast em `btnMarcarTodas` mudado para `IEnumerable<TabelaRevit>` para aceitar tanto a coleção completa quanto a filtrada por busca.

### Adicionado
- Implementação de um método independente de conversão de cores RGB para OLE (`ToOleColor`), removendo completamente a dependência da classe `System.Drawing.ColorTranslator`.

### Alterado
- **Compatibilidade Multi-Versão (.NET 8):** Refatoração massiva da interface do usuário (`DataSyncDashboard`) de `System.Windows.Forms` para `System.Windows.Window` (WPF) usando os prefixos blindados (`WWindow`, `WDataGrid`, `WTextBox`, etc).
- **Correção de Referências de Cores e Interface:** Resolvidos os erros `CS1069` (`Color` e `ColorTranslator`) originados pelas mudanças na arquitetura de assemblies no .NET 8 das versões 2025, 2026 e 2027. O C# interage agora com as APIs de cores do Excel de forma matemática, via `ToOleColor`, bypassando as bibliotecas conflitantes do pyRevit.
- **Diálogos de Sistema:** Migração dos modais `SaveFileDialog` e `OpenFileDialog` da namespace defasada do WinForms para `Microsoft.Win32`, padrão do WPF.

### Removido
- Dependência completa das bibliotecas defasadas de UI do WinForms e de `System.Drawing.Primitives`.