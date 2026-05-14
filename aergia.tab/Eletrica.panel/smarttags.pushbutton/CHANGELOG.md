# CHANGELOG

## [Unreleased]
### Alterado
- **script.cs**: Compatibilidade Multi-Versão (.NET 8): Refatoração para WPF e correção de namespaces.
- **script.cs**: Integrada a lógica do script Dynamo "set cham" para as "Drafting Views" (vistas de desenho). Agora, quando o comando do SmartTags for acionado a partir de uma vista de desenho, a rotina de sincronização identificará as Generic Annotations que possuem o parâmetro `ELID` preenchido. Em seguida, os parâmetros da anotação correspondentes ao tamanho (`tam`), marca (`mk`) e elevação (`el`) serão atualizados automaticamente a partir das propriedades reais do conduto vinculado no modelo.
- **script.cs**: No processo de geração/descarregamento da legenda (Drafting View) a partir da Folha, a ordenação dos grupos (chamadas) passou a ser feita utilizando o parâmetro `Mark` (Marca) do eletroduto/eletrocalha correspondente, de forma alfanumérica natural (ex: A, B, 9, 10). Anteriormente os elementos eram ordenados pelo número do circuito.
- **script.cs**: Adicionado o preenchimento simultâneo dos parâmetros `tam`, `mk` e `el` (idêntico à lógica do "set cham") durante a geração/descarregamento da legenda para otimizar o fluxo, sem a necessidade de acionar a ferramenta uma segunda vez.