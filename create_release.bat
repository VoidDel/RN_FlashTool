@echo off
REM 压缩打包好的程序用于分发

echo ========================================
echo 创建分发压缩包
echo ========================================
echo.

if not exist "dist\STM32_Flash_Tool" (
    echo [错误] 未找到打包文件，请先运行 build_package.bat
    pause
    exit /b 1
)

REM 获取日期作为版本号
for /f "tokens=2 delims==" %%I in ('wmic os get localdatetime /value') do set datetime=%%I
set version=%datetime:~0,4%%datetime:~4,2%%datetime:~6,2%

set output_name=STM32_Flash_Tool_v1.0_%version%_Win64.zip

echo 正在压缩...
echo 输出文件: %output_name%
echo.

REM 使用 PowerShell 压缩（Windows 内置）
powershell -command "Compress-Archive -Path 'dist\STM32_Flash_Tool' -DestinationPath '%output_name%' -Force"

if exist "%output_name%" (
    echo.
    echo ========================================
    echo 压缩完成！
    echo ========================================
    echo.
    echo 文件: %output_name%
    for %%A in ("%output_name%") do echo 大小: %%~zA 字节
    echo.
    echo 提示: 使用 7-Zip 可以获得更小的压缩包
    echo   7z a -t7z -mx=9 %output_name:.zip=.7z% dist\STM32_Flash_Tool
) else (
    echo [错误] 压缩失败！
)

pause
