@echo off
call "K:\BuildEnv\SetupBuildEnv.cmd" amd64
if errorlevel 1 exit /b 1
msbuild /t:rebuild /p:KbdlayerEdition=Pro "%~dp0kbdlayer.vcxproj"
if errorlevel 1 exit /b 1

