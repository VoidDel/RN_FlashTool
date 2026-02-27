# STM32 固件烧录工具 - 打包脚本 (PowerShell)
# 使用方法: .\build_package.ps1

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "STM32 固件烧录工具 - 自动打包脚本" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 检查虚拟环境
if (-not (Test-Path ".venv\Scripts\activate.bat")) {
    Write-Host "[错误] 未找到虚拟环境，请先创建虚拟环境" -ForegroundColor Red
    Write-Host "运行: python -m venv .venv" -ForegroundColor Yellow
    Read-Host "按回车键退出"
    exit 1
}

# 激活虚拟环境
Write-Host "[1/5] 激活虚拟环境..." -ForegroundColor Green
& ".venv\Scripts\Activate.ps1"

# 检查依赖
Write-Host "[2/5] 检查依赖..." -ForegroundColor Green
$pyinstaller = pip show pyinstaller 2>$null
if (-not $pyinstaller) {
    Write-Host "[安装] PyInstaller 未安装，正在安装..." -ForegroundColor Yellow
    pip install pyinstaller
}

$pyside6 = pip show PySide6 2>$null
if (-not $pyside6) {
    Write-Host "[安装] PySide6 未安装，正在安装..." -ForegroundColor Yellow
    pip install PySide6
}

# 清理旧的构建文件
Write-Host "[3/5] 清理旧的构建文件..." -ForegroundColor Green
if (Test-Path "build") {
    Remove-Item -Recurse -Force "build"
}
if (Test-Path "dist") {
    Remove-Item -Recurse -Force "dist"
}

# 执行打包
Write-Host "[4/5] 开始打包..." -ForegroundColor Green
pyinstaller stm32_flash.spec --clean --noconfirm

# 检查打包结果
if (-not (Test-Path "dist\STM32_Flash_Tool\STM32_Flash_Tool.exe")) {
    Write-Host ""
    Write-Host "[错误] 打包失败！" -ForegroundColor Red
    Read-Host "按回车键退出"
    exit 1
}

# 复制说明文档
Write-Host "[5/5] 复制说明文档..." -ForegroundColor Green
if (Test-Path "README.md") {
    Copy-Item "README.md" "dist\STM32_Flash_Tool\"
}
if (Test-Path "CLAUDE.md") {
    Copy-Item "CLAUDE.md" "dist\STM32_Flash_Tool\"
}

# 显示结果
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "打包完成！" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "输出目录: dist\STM32_Flash_Tool\" -ForegroundColor Cyan
Write-Host "主程序: STM32_Flash_Tool.exe" -ForegroundColor Cyan
Write-Host ""

# 显示大小
$size = (Get-ChildItem -Path "dist\STM32_Flash_Tool" -Recurse | Measure-Object -Property Length -Sum).Sum
$sizeMB = [math]::Round($size / 1MB, 2)
Write-Host "总大小: $sizeMB MB" -ForegroundColor Cyan
Write-Host ""

# 询问是否打开目录
$open = Read-Host "是否打开输出目录? (Y/N)"
if ($open -eq "Y" -or $open -eq "y") {
    explorer "dist\STM32_Flash_Tool"
}

Write-Host ""
Write-Host "提示: 可以使用 7-Zip 压缩 dist\STM32_Flash_Tool 文件夹进行分发" -ForegroundColor Yellow
Read-Host "按回车键退出"
