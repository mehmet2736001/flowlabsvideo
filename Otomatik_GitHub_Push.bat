@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo Degisiklikler GitHub'a (https://github.com/mehmet2736001/flowlabsvideo.git) gonderiliyor...
git add -A
git commit -m "Auto-update FlowVideoStudio desktop & Cloudflare Pages app"
git push -u origin main
echo [TAMAMLANDI] GitHub deponuz guncellendi!
