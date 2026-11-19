@echo off
chcp 65001 >nul
echo ========================================================
echo FlowVideoStudio Cloud - Cloudflare Pages Yayinlama Araci
echo ========================================================
echo.
echo Cloudflare hesabinizla giris yapiliyor ve proje yukleniyor...
echo.
cd /d "%~dp0"
call npx wrangler pages deploy . --project-name flow-video-studio
echo.
pause
