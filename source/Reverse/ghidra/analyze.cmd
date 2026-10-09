@echo off
rem Imports the dumped image (python image.py dump, then python image.py pe) into a Ghidra project and analyzes it.
rem Ghidra and its JDK live in C:\Tools; the project in C:\Tools\ghidra-projects (Ghidra refuses paths with ".cache").
set JAVA_HOME=C:\Tools\jdk-21.0.12.1+1
set PATH=%JAVA_HOME%\bin;%PATH%
set GHIDRA_HEADLESS_MAXMEM=16G
if not exist C:\Tools\ghidra-projects mkdir C:\Tools\ghidra-projects
call C:\Tools\ghidra_12.1.4_PUBLIC\support\analyzeHeadless.bat C:\Tools\ghidra-projects San14 -import "%~dp0..\.cache\SAN14CE_KO.mem.exe" -overwrite -max-cpu 12 < nul
