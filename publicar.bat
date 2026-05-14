@echo off
chcp 65001 >nul
echo ==========================================
echo   Publicador da Extensao Aergia (pyRevit)
echo ==========================================
echo.

set /p msg="Digite a mensagem de commit (ex: atualizando scripts da Eletrica): "

if "%msg%"=="" (
    echo.
    echo [ERRO] A mensagem nao pode ser vazia. Cancelando...
    echo.
    pause
    exit /b
)

echo.
echo [1/3] Adicionando arquivos modificados...
git add .

echo.
echo [2/3] Criando commit...
git commit -m "%msg%"

echo.
echo [3/3] Enviando para o GitHub...
git push origin master

echo.
echo ==========================================
echo        Alteracoes enviadas com sucesso!
echo ==========================================
pause
