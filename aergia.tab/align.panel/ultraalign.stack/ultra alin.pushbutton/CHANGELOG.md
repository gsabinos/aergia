# CHANGELOG

## [Unreleased]
### Adicionado
- Integração das regras da skill `transaction-memory-manager`.
- **Compatibilidade Multi-Versão (.NET 8):** Refatoração da interface de usuário. O formulário migrou de `System.Windows.Forms.Form` (WinForms) para `System.Windows.Window` (WPF) construído dinamicamente no código. Isso resolve permanentemente erros do tipo `CS0012` (`IHandle<>`, `Color`, `Size`) ao compilar no pyRevit usando o .NET 8 (Revit 2025, 2026, 2027), eliminando a dependência do assembly `System.Drawing.Primitives` e `System.Windows.Forms.Primitives`.
- **Correção de Ambiguidade:** Qualificado explicitamente `Autodesk.Revit.UI.TaskDialog` e `System.Text.RegularExpressions.Regex` para evitar os erros `CS0104` e `CS0433` em compilações do .NET 8.

### Alterado
- **script.cs**: Modificado o método `ExecutarMotorGrid` para extrair as `BoundingBoxes` dentro de uma `SubTransaction`. As leaders de `IndependentTag`, `SpatialElementTag` e `TextNote` são desativadas ou removidas temporariamente apenas dentro da `SubTransaction` que sofre `RollBack`. Isso permite medir e alinhar as caixas perfeitamente enquanto restaura as leaders com precisão absoluta (inclusive as posições dos cotovelos/setas originais).
- **script.cs**: Unificado com o código do `ultra align.cs`.
- **script.cs**: O método `ExecutarMotorGrid` agora desativa temporariamente a propriedade `HasLeader` das tags (`IndependentTag`) antes de calcular suas caixas delimitadoras (`BoundingBox`). As leaders são restauradas ao final da execução, evitando interferência no alinhamento das caixas.
- **Otimização de Performance**: Removido o recalculo do modelo (`doc.Regenerate()`) de dentro do laço de repetição `for` principal. A regeneração fica unicamente a cargo do `Transaction.Commit()`, tornando o script instantâneo mesmo para centenas de elementos.

### Removido
- Dependência das bibliotecas defasadas de UI do WinForms.
- Arquivos duplicados `ultra align.cs` e `script - Copia.cs` deletados para evitar erro de conflito de compilação por classes duplicadas no pyRevit.