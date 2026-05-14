# CHANGELOG

## [Unreleased]
### Alterado
- **Compatibilidade Multi-Versão (.NET 8)**: Refatoração para WPF e correção de namespaces. Remoção completa de dependências do `System.Windows.Forms` e `System.Drawing`, garantindo compatibilidade com pyRevit no Revit 2025/2026/2027.
- **Painel de Configuração**: Refatorado de WinForms para WPF (C# nativo via System.Windows).
- **Expressões Regulares**: Remoção do uso de `System.Text.RegularExpressions` e substituição por métodos de string para melhor compatibilidade.

### Adicionado
- **quantitativo.pushbutton**: Novo botão criado na aba "Elétrica" que consolida e transpila a lógica dos scripts Dynamo "set elev", "set item" e "set nome cir" em uma única execução de alta performance em C#.
- **script.cs**: 
  - **SET NOME CIR**: Itera sobre os equipamentos elétricos, busca o primeiro sistema elétrico associado, extrai o parâmetro "Load Name" (Nome da Carga/Circuito) e preenche o parâmetro "Comments" (Comentários).
  - **set elev**: Itera sobre Eletrodutos e Eletrocalhas, obtém o valor de "Reference Level" (Nível de referência) e copia para o parâmetro "ZZ.ELNIV".
  - **set item**: Itera sobre Eletrodutos e Eletrocalhas para formatar a geometria. Nas eletrocalhas gera a formatação "Altura x Largura", e nos eletrodutos concatena "Ø" com o tamanho. Preenche os parâmetros "Dimensões", "Comp" e aplica a regra de "SUB ITEM".
  - **Painel de Configuração**: Adicionada interface de mapeamento para o valor da propriedade "SUB ITEM". Ao segurar a tecla `SHIFT` e clicar no botão, uma janela é aberta para que o usuário consiga visualizar, adicionar, editar e excluir regras de "Dimensão" -> "SUB ITEM" para Eletrodutos e Eletrocalhas. As regras são salvas no arquivo de configuração do projeto (`aegialt_quantitativo_map.json`).
