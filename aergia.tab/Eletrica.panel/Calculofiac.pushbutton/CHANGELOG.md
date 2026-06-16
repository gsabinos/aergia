# Changelog

## [Unreleased]

### 2026-06-09 (f)
- Nova opção **"Cabo terra único por trecho (atribuído ao último circuito)"** na aba 3 (Configurações). Quando ligada, em cada trecho de infra o condutor de proteção (terra) é contado **uma única vez**, atribuído ao **último circuito** presente no trecho (maior número sequencial — ex.: 10 depois de 09), em vez de um terra por circuito. Afeta a tag `ZFIACAO` (o `1T` aparece só no último circuito), o `Comprimento Terra` e a ocupação NBR (uma seção de terra por trecho).
  - Persistência via `nwrconfig.json` (chave `TerraUnicoPorTrecho`), salva pelo botão "SALVAR REGRAS DE ROTEAMENTO". Lida em `ExecutarCalculo` e repassada a `ProcessadorQuadro.Processar(..., bool terraUnicoPorTrecho)`. Padrão desligado (mantém um terra por circuito).
  - Dimensionamento do terra comum: usa a bitola do circuito ao qual foi atribuído (o último). (Se quiser dimensionar pelo maior circuito do trecho, é um ajuste simples.)

### 2026-06-09 (e)
- Tipos de parâmetros de circuito ajustados no arquivo SP (com GUIDs próprios): **Bitola** Texto → **Número**; **NEUTRO** e **TERRA** Inteiro → **Sim/Não (YESNO)**. `FASE` segue Inteiro. A retipagem automática (entrada (d)) aplica a troca ao reinjetar.
- `NormaNBR.ParseBitola` agora lê a Bitola numérica direto (`AsDouble`) quando o parâmetro é Número, mantendo o parsing de texto (`"3#2,5mm²"`) como fallback. `NEUTRO`/`TERRA` Sim/Não continuam lidos via `ReadParamInt` (0/1).
- ATENÇÃO: retipar um parâmetro o recria do zero, **apagando os valores existentes**. Se você já preencheu Bitola/NEUTRO/TERRA nos circuitos, anote-os antes de reinjetar (só afeta os que mudam de tipo; se já estiverem no tipo novo, são pulados sem perda).

### 2026-06-09 (d)
- `ZOCUPACAO` passa de **Texto** para **Número** (no arquivo SP, com GUID próprio). Agora grava o valor numérico cru (ex.: `42,3`) via novo `Utils.WriteParamNumber` (sem a conversão de unidade do `WriteParam`), permitindo ordenação/filtro numérico na tabela. Limpeza de trechos não roteados usa `Utils.ClearParam` (esvazia o valor em vez de gravar `""`). `ZOCUPSTATUS` segue Texto (`OK`/`EXCEDIDO`).
- Correções na injeção (`GarantirParametrosProjeto`) que impediam a troca de tipo de funcionar:
  - O arquivo SP em `%APPDATA%/Aegia/aegia_parametros.txt` agora é **sempre regravado** a partir de `SP_CONTEUDO` (antes, com `if (!File.Exists)`, ficava "grudado" com a definição antiga — ex.: ZOCUPACAO Texto).
  - **Retipagem automática:** se um parâmetro já existe com tipo divergente do esperado, a injeção **remove e revincula** com o tipo certo (compara `Definition.GetDataType()`), em vez de só pular por nome. Relatório agora tem a categoria **"Retipados"**.
  - `ConfigurarProjetoETabelas` ficou idempotente: garante os campos `Z*` na tabela mesmo quando ela já existe (a coluna reaparece após a retipagem).

### 2026-06-09 (c)
- Roteamento: **quadros/equipamentos elétricos** (`OST_ElectricalEquipment`) passam a ser atravessáveis no **Tier 1** (sempre), não mais só no fallback. Corrige rotas que precisavam passar por um equipamento intermediário (ex.: subquadro) e falhavam. Tomadas (`OST_ElectricalFixtures`) seguem só no Tier 2 (única rota).

### 2026-06-09 (b)
- **Injeção de parâmetros reescrita** (`GarantirParametrosOcupacao` → `GarantirParametrosProjeto`). O botão "INJETAR TABELAS E PARÂMETROS" não vinculava nada e ainda assim mostrava "sucesso" (falhas silenciosas):
  - Causa: retornos silenciosos (`faltantes==0`, `dfile==null`), arquivo SP temporário "grudado" (`if (!File.Exists)` nunca regravava), quebras de linha `\n` (parser do Revit exige `\r\n`), `Insert` sem fallback `ReInsert` e `jaExistem` largo demais.
  - Agora usa um arquivo de parâmetros compartilhados com **GUIDs fixos** (constante `SP_CONTEUDO`, `\r\n`) gravado em `%APPDATA%/Aegia/aegia_parametros.txt`; **auto-curável** (regrava se ausente/corrompido) e **idempotente** por nome (não conflita com parâmetros de projeto pré-existentes).
  - Passa a injetar **todos** os customizados que o script usa: `ZIDS`, `ZFIACAO`, `ZOCUPACAO`, `ZOCUPSTATUS`, `ZTIPOFAM` (infra/fittings), `ZIDC` (luminárias) e `Tipo Circuito`, `Bitola`, `FASE`, `NEUTRO`, `TERRA`, `Comp`, `Comprimento Fase/Neutro/Terra/Retorno` (circuitos), cada um nas categorias corretas.
  - O dialog de conclusão agora mostra **relatório**: criados / já existiam / falharam (com motivo). Falhas reais passam a aparecer em vez de silenciar.
  - Cópia canônica do arquivo SP disponível em `aegia_parametros.txt` (mesmo conteúdo/GUIDs) para uso manual via Gerenciar > Parâmetros Compartilhados, se preferir.

### 2026-06-09
- Roteamento de luminárias corrigido em `NetworkRouter`:
  - `GetConLumin` passa a usar **conectividade real primeiro**: se a luminária está fisicamente ligada (conector Revit) a uma infra permitida (eletroduto/eletrocalha ou fitting), usa-a diretamente como ponto de partida, antes de cair para a busca por proximidade a fittings. Grava o resultado em `ZIDC`. Resolve luminárias diretamente conectadas à infra que antes ficavam sem rota ou ligadas ao fitting errado.
  - `FindPath` reescrito como BFS de dois níveis (novo helper `BfsToEnds(start, ends, tipoCirc, podeAtravessar)`):
    - **Tier 1 (normal):** a rota atravessa infra e também luminárias/interruptores (`OST_LightingFixtures`/`OST_LightingDevices`) **apenas quando estes estão inline na infra** (`GetNeighbors(n).Any(IsInfra)`). Carga ligada só a outras cargas deixa de ser usada como ponte (elimina atalhos artificiais e vazamento de workset).
    - **Tier 2 (fallback):** só quando não há rota pelo Tier 1, permite atravessar tomadas (`OST_ElectricalFixtures`) e quadros (`OST_ElectricalEquipment`) — "única rota".
  - Endpoints do quadro (`GetPanelEndpoints`) continuam sempre aceitos como destino; o filtro de workset (`IsWsAllowed`) segue aplicado à infra em ambos os níveis.

### 2026-06-05
- Cálculo automático de ocupação de eletrodutos e eletrocalhas (NBR 5410) embutido no roteamento em lote. Ao final do cálculo, cada trecho recebe `ZOCUPACAO` (% de ocupação) e `ZOCUPSTATUS` (OK/EXCEDIDO).
  - Nova classe `NormaNBR`: tabela bitola→área externa de cabo PVC 450/750V, redução de bitola do terra (Tabela 6.5), área interna do eletroduto (diâmetro interno do tipo via `RBS_CONDUIT_INNER_DIAM_PARAM`, fallback 0,85×nominal) e da eletrocalha (largura×altura), e limites 53%/31%/40% (1/2/3+ condutores) + 50% para eletrocalha.
  - A área dos condutores reusa o inventário por trecho já calculado no roteamento (qtdF/qtdN/qtdT/retornos), acumulado entre todos os quadros do lote via `OcupacaoTrecho`. Bitola lida do parâmetro `Bitola` do circuito.
  - `LimparInfraestruturaDosQuadros` zera `ZOCUPACAO`/`ZOCUPSTATUS`; `ConfigurarProjetoETabelas` adiciona os dois campos à ViewSchedule Aegia.
  - `ConfigurarProjetoETabelas` agora injeta automaticamente `ZOCUPACAO` e `ZOCUPSTATUS` (parâmetros de instância, tipo Texto) nas categorias Conduit e Cable Tray, via `GarantirParametrosOcupacao` (arquivo de parâmetros compartilhados temporário + `BindingMap`, API nova `SpecTypeId.String.Text`/`GroupTypeId.Electrical`). Idempotente: só cria os que faltam. Botão "INJETAR TABELAS E PARÂMETROS" da aba Configurações passa a deixar o projeto pronto sem setup manual.

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
