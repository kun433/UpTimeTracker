@echo off
rem UpTimeTracker 一键构建脚本：调用系统自带的 .NET Framework 4.8 C# 编译器
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo 未找到 .NET Framework 4.8 编译器 Csc.exe
    exit /b 1
)
if not exist dist mkdir dist
"%CSC%" -nologo -target:winexe -optimize+ -platform:anycpu -out:dist\UpTimeTracker.exe Program.cs
if errorlevel 1 exit /b 1
echo 构建完成: dist\UpTimeTracker.exe
