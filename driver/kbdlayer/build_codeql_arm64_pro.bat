@echo off
call "K:\BuildEnv\SetupBuildEnv.cmd" amd64
if errorlevel 1 exit /b 1
msbuild /t:rebuild /p:Platform=ARM64 /p:KbdlayerEdition=Pro "%~dp0kbdlayer_arm64.vcxproj"
if errorlevel 1 exit /b 1

