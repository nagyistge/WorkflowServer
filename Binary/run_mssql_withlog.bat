@set @x=0; new ActiveXObject('Shell.Application').ShellExecute ('cmd.exe','/K ' + '"' + WScript.ScriptFullName + '"' + ' Admin','','runas',1);/*
@echo off
if "%~1" neq "Admin" (
  cscript.exe //nologo //e:jscript "%~f0"
) else (
  cd /d "%~dp0"

  cd bin
  REM use commented sting to run WFE server with callback service
  REM wfes.exe -url="http://*:8077/" -callbackurl="http://localhost:8078/" -befolder="../backend" -callbackgenscheme -dbprovider=mssql -dbcs="Data Source=(local);Initial Catalog=WorkflowApp;Integrated Security=False;User ID=sa;Password=1;"
  wfes.exe -url="http://*:8077/" -callbackurl="" -befolder="../backend" -callbackgenscheme -log -dbprovider=mssql -dbcs="Data Source=(local);Initial Catalog=WorkflowApp;Integrated Security=False;User ID=sa;Password=1;"
  pause
)
exit /B

*/