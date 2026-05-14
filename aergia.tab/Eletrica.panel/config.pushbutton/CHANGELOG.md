# Changelog

## [Unreleased]

### 2026-05-11
- Renomeados `config mk1.cs` e `config.cs` para `.bak` — eram versões antigas/backup do `script.cs` atual; pyRevit estava compilando os 3 .cs simultaneamente (risco de símbolos duplicados ou regras divergentes). Os `.bak` ficam preservados para auditoria.

### Anteriores
- Compatibilidade Multi-Versão (.NET 8): Refatoração para WPF e correção de namespaces.