@echo off
chcp 65001 >nul
echo FlowVideoStudio.exe derleniyor...
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /target:winexe /platform:x64 /out:"FlowVideoStudio.exe" ^
  /r:"Microsoft.Web.WebView2.Core.dll" ^
  /r:"Microsoft.Web.WebView2.WinForms.dll" ^
  /r:"System.Windows.Forms.dll" ^
  /r:"System.Drawing.dll" ^
  /r:"System.Web.Extensions.dll" ^
  /r:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF\System.Speech.dll" ^
  "Program.cs"

if %ERRORLEVEL% EQU 0 (
    echo [BASARILI] FlowVideoStudio.exe olusturuldu!
) else (
    echo [HATA] Derleme sirasinda hata olustu.
)
pause
