@echo off
rem Builds the spike DragDropHandler DLL with the newest installed MSVC x64 toolset.
rem Output: bin\FerrySpikeDragDrop.dll (git-ignored). Does NOT register anything.
setlocal
set "HERE=%~dp0"
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (echo vswhere.exe not found: no Visual Studio installed. & exit /b 1)

rem Set FERRY_VSPATH to force a specific Visual Studio install folder.
set "VSPATH=%FERRY_VSPATH%"
if not defined VSPATH for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSPATH=%%i"
if not defined VSPATH (echo No Visual Studio with the C++ x64 tools found. & exit /b 1)

call "%VSPATH%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1

if not exist "%HERE%bin" mkdir "%HERE%bin"
pushd "%HERE%"
cl /nologo /LD /EHsc /O2 /W4 /WX /permissive- /std:c++17 /MT /GS /sdl /guard:cf /Zi /DUNICODE /D_UNICODE ^
   /Fo"bin\\" /Fd"bin\\" FerrySpikeDragDrop.cpp ^
   /link /DEF:FerrySpikeDragDrop.def /OUT:bin\FerrySpikeDragDrop.dll /PDB:bin\FerrySpikeDragDrop.pdb /IMPLIB:bin\FerrySpikeDragDrop.lib ^
   /DYNAMICBASE /NXCOMPAT /HIGHENTROPYVA /GUARD:CF /CETCOMPAT /INCREMENTAL:NO /OPT:REF /OPT:ICF ^
   ole32.lib shell32.lib user32.lib uuid.lib
set "RC=%ERRORLEVEL%"
if "%RC%"=="0" (
  rem Test harness: loads the DLL by path in its OWN process (never Explorer) and drives it 200 times.
  cl /nologo /EHsc /O2 /W4 /WX /permissive- /std:c++17 /MT /DUNICODE /D_UNICODE ^
     /Fo"bin\\" /Fd"bin\\" harness.cpp /link /OUT:bin\harness.exe /INCREMENTAL:NO ole32.lib shell32.lib user32.lib
  set "RC=%ERRORLEVEL%"
)
popd
if not "%RC%"=="0" (echo Build failed. & exit /b %RC%)
echo Built %HERE%bin\FerrySpikeDragDrop.dll using "%VSPATH%"
exit /b 0
